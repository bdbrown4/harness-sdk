using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeTests
{
    [Fact]
    public async Task ModernListenAcknowledgesAndRefreshesACompleteSnapshotOverRealHttp()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var changed = NewSignal<McpToolsChanged>();
        await using var subscription = await client.WatchToolChangesAsync((snapshot, ignored) => { changed.TrySetResult(snapshot); return default; },
            new() { Prefix = "remote" }, new() { Debounce = TimeSpan.Zero });
        Assert.Equal("remote_first", Assert.Single(subscription.Current.Tools).Spec.Name);
        Assert.Equal(0, subscription.Current.Version);
        Assert.False(changed.Task.IsCompleted);

        server.ToolName = "second";
        await server.NotifyAsync();
        var refreshed = await Deadline(changed.Task);

        Assert.Equal("remote_second", Assert.Single(refreshed.Tools).Spec.Name);
        Assert.Equal("remote_first", Assert.Single(refreshed.PreviousNames));
        Assert.Equal(1, refreshed.Version);
        Assert.Same(refreshed, subscription.Current);
        Assert.Equal(1, server.ListenCount);
        Assert.Equal(2, server.ListCount);
    }

    [Fact]
    public async Task DeniedModernGrantFailsBeforeDiscoveryAndDrainsOnlyItsOwnListen()
    {
        await using var server = new McpToolChangeLoopbackServer { Grant = false };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.WatchToolChangesAsync((snapshot, token) => default).AsTask());

        await Deadline(server.ListenClosed.Task);
        Assert.Equal(0, server.ListCount);
        Assert.True(client.IsConnected);
        Assert.Equal("first", Assert.Single(await client.ListToolsAsync()).ServerName);
    }

    [Fact]
    public async Task WatchersShareOneListenAndTheLastDisposalPhysicallyClosesIt()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var first = await client.WatchToolChangesAsync((snapshot, token) => default, new() { Prefix = "left" });
        var second = await client.WatchToolChangesAsync((snapshot, token) => default, new() { Prefix = "right" });
        try
        {
            await first.DisposeAsync();
            Assert.False(server.ListenClosed.Task.IsCompleted);
            Assert.Equal(1, server.ListenCount);
        }
        finally { await second.DisposeAsync(); }

        await Deadline(server.ListenClosed.Task);
        await first.Completion;
        await second.Completion;
        Assert.True(client.IsConnected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public async Task InvalidDelayIsRejectedBeforeProtocolEffects(long milliseconds)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.WatchToolChangesAsync((snapshot, token) => default,
            options: new() { Debounce = TimeSpan.FromMilliseconds(milliseconds) }).AsTask());

        Assert.Equal(0, server.ListenCount);
        Assert.Equal(0, server.ListCount);
    }

    [Fact]
    public async Task CallerCancellationWhileAwaitingAcknowledgementDrainsTheListenAndKeepsTheClient()
    {
        await using var server = new McpToolChangeLoopbackServer { HoldAcknowledgement = true };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        using var caller = new CancellationTokenSource();
        var creation = client.WatchToolChangesAsync((snapshot, token) => default, cancellationToken: caller.Token).AsTask();
        await Deadline(server.ListenEntered.Task);

        caller.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);

        Assert.Equal(caller.Token, failure.CancellationToken);
        await Deadline(server.ListenClosed.Task);
        Assert.Equal(0, server.ListCount);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ClientDisposalWakesAndJoinsAnObserverStillAwaitingAcknowledgement()
    {
        await using var server = new McpToolChangeLoopbackServer { HoldAcknowledgement = true };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        using var caller = new CancellationTokenSource();
        var creation = client.WatchToolChangesAsync((snapshot, token) => default, cancellationToken: caller.Token).AsTask();
        await Deadline(server.ListenEntered.Task);
        try
        {
            var disposal = client.DisposeAsync().AsTask();
            await Deadline(disposal);
            Assert.Same(creation, await Task.WhenAny(creation, Task.Delay(2000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
            await Deadline(server.ListenClosed.Task);
        }
        finally
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        }
    }

    [Fact]
    public async Task AListenEndingWithoutAResultFaultsCompletionAndAllowsAnExplicitNewWatch()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var subscription = await client.WatchToolChangesAsync((snapshot, token) => default);

        server.EndListen();
        await Assert.ThrowsAnyAsync<Exception>(() => Deadline(subscription.Completion));
        Assert.True(subscription.Completion.IsFaulted);
        await Assert.ThrowsAnyAsync<Exception>(() => subscription.DisposeAsync().AsTask());

        Assert.True(client.IsConnected);
        await using var next = await client.WatchToolChangesAsync((snapshot, token) => default);
        Assert.Equal(2, server.ListenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackLifecycleReentryFailsPromptlyWithoutDeadlocking(bool disposeClient)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var observed = NewSignal<Exception>();
        McpToolChangeSubscription? subscription = null;
        subscription = await client.WatchToolChangesAsync(async (snapshot, token) =>
        {
            try
            {
                if (disposeClient) await client.DisposeAsync();
                else await subscription!.DisposeAsync();
                observed.TrySetException(new InvalidOperationException("Reentry was accepted."));
            }
            catch (InvalidOperationException error) { observed.TrySetResult(error); }
        }, options: new() { Debounce = TimeSpan.Zero });
        try
        {
            await server.NotifyAsync();
            Assert.IsType<InvalidOperationException>(await Deadline(observed.Task));
            Assert.True(client.IsConnected);
        }
        finally { await subscription.DisposeAsync(); }
    }

    [Theory]
    [InlineData(McpToolChangeProfile.Python, 3)]
    [InlineData(McpToolChangeProfile.TypeScript, 2)]
    public async Task ProfilesRetainOrDropOneQueuedRoundAfterListingFailure(McpToolChangeProfile profile, int expectedListings)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var listing = NewSignal<bool>(); var release = NewSignal<bool>();
        server.BeforeList = async (number, token) => { if (number == 2) { listing.TrySetResult(true); await release.Task; } };
        server.FailList = number => number == 2;
        var delivered = NewSignal<bool>();
        var notifications = 0;
        await using var observer = client.Protocol.RegisterNotificationHandler("notifications/tools/list_changed", (notification, token) =>
        { if (Interlocked.Increment(ref notifications) == 2) delivered.TrySetResult(true); return default; });
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => default,
            options: new() { Profile = profile, Debounce = TimeSpan.Zero });
        try
        {
            server.ToolName = "second";
            await server.NotifyAsync();
            await Deadline(listing.Task);
            await server.NotifyAsync();
            await Deadline(delivered.Task);
            var drain = subscription.RefreshDrain;
            release.TrySetResult(true);
            await Deadline(drain);

            Assert.Equal(expectedListings, server.ListCount);
            Assert.Equal(profile == McpToolChangeProfile.Python ? "second" : "first", Assert.Single(subscription.Current.Tools).ServerName);
            Assert.False(subscription.Completion.IsCompleted);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task EscapedCallbackReceiptsExpireBeforeLaterIndependentLifecycleWork()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var release = NewSignal<bool>();
        var created = NewSignal<Task<McpToolChangeSubscription>>();
        var callbackDone = NewSignal<bool>();
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) =>
        {
            created.TrySetResult(Task.Run(async () => { await release.Task; return await client.WatchToolChangesAsync((next, ignored) => default); }));
            callbackDone.TrySetResult(true);
            return default;
        }, options: new() { Debounce = TimeSpan.Zero });
        await server.NotifyAsync();
        await Deadline(callbackDone.Task);
        await Deadline(subscription.RefreshDrain);

        release.TrySetResult(true);
        await using var escaped = await Deadline(await Deadline(created.Task));

        Assert.Equal(1, server.ListenCount);
        Assert.Equal("first", Assert.Single(escaped.Current.Tools).ServerName);
    }

    [Fact]
    public async Task ACallbackCannotJoinAnotherWatcherOnItsSharedChannel()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var other = await client.WatchToolChangesAsync((snapshot, token) => default);
        var rejected = NewSignal<Exception?>();
        await using var subscription = await client.WatchToolChangesAsync(async (snapshot, token) =>
        {
            try { await other.DisposeAsync(); rejected.TrySetResult(null); }
            catch (Exception error) { rejected.TrySetResult(error); }
        }, options: new() { Debounce = TimeSpan.Zero });
        try
        {
            await server.NotifyAsync();
            Assert.IsType<InvalidOperationException>(await Deadline(rejected.Task));
            Assert.False(subscription.Completion.IsCompleted);
            Assert.False(server.ListenClosed.Task.IsCompleted);
            Assert.Equal(1, server.ListenCount);
        }
        finally { await other.DisposeAsync(); }
    }

    [Fact]
    public async Task ForeignAcknowledgementsAndChangesDoNotAuthorizeOrRefreshThisSubscription()
    {
        await using var server = new McpToolChangeLoopbackServer { ForeignAcknowledgementFirst = true };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var received = NewSignal<bool>();
        await using var observation = client.Protocol.RegisterNotificationHandler("notifications/tools/list_changed", (notification, token) =>
        { received.TrySetResult(true); return default; });
        var changed = NewSignal<McpToolsChanged>();
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => { changed.TrySetResult(snapshot); return default; },
            options: new() { Debounce = TimeSpan.Zero });
        server.ToolName = "second";

        await server.NotifyAsync("unrelated-application-listen");
        await Deadline(received.Task);
        await subscription.RefreshDrain;
        Assert.Equal(0, subscription.Current.Version);
        Assert.Equal(1, server.ListCount);
        Assert.False(changed.Task.IsCompleted);

        await server.NotifyAsync();
        Assert.Equal("second", Assert.Single((await Deadline(changed.Task)).Tools).ServerName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCorrelationOrPreAcknowledgementChangesCannotBeginDiscovery(bool preAcknowledgementChange)
    {
        await using var server = new McpToolChangeLoopbackServer
        {
            HoldAcknowledgement = preAcknowledgementChange,
            OmitAcknowledgementMetadata = !preAcknowledgementChange,
            ChangeBeforeAcknowledgement = preAcknowledgementChange
        };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        using var cancellation = new CancellationTokenSource();
        var creation = client.WatchToolChangesAsync((snapshot, token) => default,
            options: preAcknowledgementChange ? null : new() { AcknowledgementTimeout = TimeSpan.FromMilliseconds(100) },
            cancellationToken: cancellation.Token).AsTask();
        try
        {
            if (preAcknowledgementChange)
            {
                await Deadline(server.ListenEntered.Task);
                var changeReceived = NewSignal<bool>();
                await using var observation = client.Protocol.RegisterNotificationHandler("notifications/tools/list_changed", (notification, token) =>
                { changeReceived.TrySetResult(true); return default; });

                server.ReleaseAcknowledgement.TrySetResult(true);
                await Deadline(changeReceived.Task);
                await Assert.ThrowsAsync<InvalidDataException>(() => Deadline(creation));
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => creation);

            await Deadline(server.ListenClosed.Task);
            Assert.Equal(0, server.ListCount);
            Assert.True(client.IsConnected);
        }
        finally
        {
            server.ReleaseAcknowledgement.TrySetResult(true);
            cancellation.Cancel();
            try { await creation; }
            catch (Exception) { /* The assertion owns the creation failure; cleanup still joins its task. */ }
        }
    }

    [Fact]
    public async Task ARepeatedMatchingAcknowledgementFaultsOnlyTheWatcherChannel()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var subscription = await client.WatchToolChangesAsync((snapshot, token) => default);

        await server.RepeatAcknowledgementAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Deadline(subscription.Completion));
        await Assert.ThrowsAsync<InvalidDataException>(() => subscription.DisposeAsync().AsTask());

        Assert.True(client.IsConnected);
        Assert.Equal(1, server.ListCount);
    }

    [Fact]
    public async Task ServerCancellationTerminatesItsMatchingListenWithoutDisposingTheClient()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var subscription = await client.WatchToolChangesAsync((snapshot, token) => default);
        try
        {
            await server.CancelListenAsync();
            Assert.Same(subscription.Completion, await Task.WhenAny(subscription.Completion, Task.Delay(2000)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.Completion);
            Assert.True(client.IsConnected);
        }
        finally
        {
            if (subscription.Completion.IsFaulted) await Assert.ThrowsAnyAsync<Exception>(() => subscription.DisposeAsync().AsTask());
            else await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelingOneUnacknowledgedWatcherKeepsAnotherPendingWatcherAndItsListen()
    {
        await using var server = new McpToolChangeLoopbackServer { HoldAcknowledgement = true };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        using var caller = new CancellationTokenSource();
        var canceled = client.WatchToolChangesAsync((snapshot, token) => default, cancellationToken: caller.Token).AsTask();
        await Deadline(server.ListenEntered.Task);
        var survivor = client.WatchToolChangesAsync((snapshot, token) => default).AsTask();

        caller.Cancel();
        Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled)).CancellationToken);
        Assert.False(survivor.IsCompleted);
        Assert.False(server.ListenClosed.Task.IsCompleted);
        server.ReleaseAcknowledgement.TrySetResult(true);

        await using var subscription = await Deadline(survivor);
        Assert.Equal(1, server.ListenCount);
        Assert.Equal("first", Assert.Single(subscription.Current.Tools).ServerName);
    }

    [Fact]
    public async Task OrderedWireAcknowledgementCanBeDispatchedAfterAChangeWithoutPublishingBeforeGrant()
    {
        await using var server = new McpToolChangeLoopbackServer { HoldAcknowledgement = true };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var changeDispatched = NewSignal<bool>();
        await using var changeObservation = client.Protocol.RegisterNotificationHandler("notifications/tools/list_changed", (notification, token) =>
        { changeDispatched.TrySetResult(true); return default; });
        var changed = NewSignal<McpToolsChanged>();
        var creation = client.WatchToolChangesAsync((snapshot, token) => { changed.TrySetResult(snapshot); return default; },
            options: new() { Debounce = TimeSpan.Zero }).AsTask();
        await Deadline(server.ListenEntered.Task);
        var acknowledgementDispatched = NewSignal<bool>(); var release = NewSignal<bool>();
        // Registered last: the SDK awaits this borrowed handler before invoking our ACK callback.
        await using var delayAcknowledgement = client.Protocol.RegisterNotificationHandler("notifications/subscriptions/acknowledged", async (notification, token) =>
        { acknowledgementDispatched.TrySetResult(true); await release.Task; });
        try
        {
            server.ReleaseAcknowledgement.TrySetResult(true);
            await Deadline(acknowledgementDispatched.Task);
            server.ToolName = "updated";
            await server.NotifyAsync();
            await Deadline(changeDispatched.Task);

            Assert.False(creation.IsCompleted);
            Assert.Equal(0, server.ListCount);
        }
        finally { release.TrySetResult(true); }
        await using var subscription = await Deadline(creation);
        Assert.Equal("updated", Assert.Single((await Deadline(changed.Task)).Tools).ServerName);
        Assert.Equal(2, server.ListCount);
        Assert.Equal(1, subscription.Current.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrWrongTypedAcknowledgementsDoNotPublishTools(bool wrongIdType)
    {
        await using var server = new McpToolChangeLoopbackServer
        { MalformedAcknowledgement = !wrongIdType, NumericAcknowledgementId = wrongIdType };
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var creation = client.WatchToolChangesAsync((snapshot, token) => default,
            options: new() { AcknowledgementTimeout = TimeSpan.FromMilliseconds(100) }).AsTask();

        if (wrongIdType) await Assert.ThrowsAsync<TimeoutException>(() => creation);
        else await Assert.ThrowsAsync<InvalidDataException>(() => creation);

        await Deadline(server.ListenClosed.Task);
        Assert.Equal(0, server.ListCount);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task AnUnexpectedRemoteErrorBeforeCloseRemainsTheTerminalCause()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var subscription = await client.WatchToolChangesAsync((snapshot, token) => default);

        await server.FailListenAsync("unexpected remote failure");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Deadline(subscription.Completion));

        Assert.Contains("unexpected remote failure", failure.Message);
        Assert.IsNotType<OperationCanceledException>(failure);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => subscription.DisposeAsync().AsTask()));
        Assert.True(client.IsConnected);
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Deadline<T>(Task<T> task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); return await task; }
    private static async Task Deadline(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint) => Client.CreateHttpAsync(http,
        new() { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new() { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });
}
