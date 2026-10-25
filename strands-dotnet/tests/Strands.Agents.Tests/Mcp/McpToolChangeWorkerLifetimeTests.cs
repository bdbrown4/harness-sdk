using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeWorkerLifetimeTests
{
    [Fact]
    public async Task InitialDiscoveryFilterCannotDisposeItsOwningClient()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        Exception? rejected = null;
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => default, new()
        {
            ToolFilter = name =>
            {
                try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { rejected = error; }
                return true;
            }
        });

        Assert.IsType<InvalidOperationException>(rejected);
        Assert.True(client.IsConnected);
        Assert.Equal("first", Assert.Single(subscription.Current.Tools).ServerName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshFiltersRejectOwnWorkerLifecycleBeforeReturningAPendingSelfWait(bool clientDisposal)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var refresh = false;
        var attempted = NewSignal<(Exception? Error, Task? Pending)>();
        McpToolChangeSubscription? subscription = null;
        subscription = await client.WatchToolChangesAsync((snapshot, token) => default, new()
        {
            ToolFilter = name =>
            {
                if (!refresh) return true;
                try
                {
                    var operation = clientDisposal ? client.DisposeAsync().AsTask() : subscription!.DisposeAsync().AsTask();
                    attempted.TrySetResult((null, operation));
                }
                catch (Exception error) { attempted.TrySetResult((error, null)); }
                return true;
            }
        }, new() { Debounce = TimeSpan.Zero });
        try
        {
            refresh = true;
            await server.NotifyAsync();
            var result = await Deadline(attempted.Task);
            // The failing draft is drained cooperatively; do not leave a deliberately blocked worker.
            if (result.Pending is not null) await Deadline(result.Pending);
            Assert.IsType<InvalidOperationException>(result.Error);
            Assert.True(client.IsConnected);
        }
        finally { await subscription.DisposeAsync(); }
    }

    [Fact]
    public async Task AnExecutionContextCapturedByTheInitialFilterExpiresAfterDiscoveryReturns()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var release = NewSignal<bool>();
        Task<McpToolChangeSubscription>? escaped = null;
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => default, new()
        {
            ToolFilter = name =>
            {
                escaped = Task.Run(async () => { await release.Task; return await client.WatchToolChangesAsync((snapshot, token) => default); });
                return true;
            }
        });
        release.TrySetResult(true);

        await using var later = await Deadline(escaped!);

        Assert.Equal(1, server.ListenCount);
        Assert.Equal("first", Assert.Single(later.Current.Tools).ServerName);
    }

    [Fact]
    public async Task ExpiredWorkerContextsAreConservativelyRejectedOnlyDuringCancellationDelivery()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var callbackDone = NewSignal<bool>(); var cancelEntered = NewSignal<bool>(); var releaseCancel = NewSignal<bool>();
        var examineDuring = NewSignal<bool>(); var examined = NewSignal<Exception?>(); var examineAfter = NewSignal<bool>();
        Task? escaped = null;
        CancellationTokenRegistration registration = default;
        McpToolChangeSubscription? subscription = null;
        subscription = await client.WatchToolChangesAsync((snapshot, token) =>
        {
            registration = token.Register(() => { cancelEntered.TrySetResult(true); releaseCancel.Task.GetAwaiter().GetResult(); });
            escaped = Task.Run(async () =>
            {
                await examineDuring.Task;
                Exception? rejected = null;
                try { _ = subscription!.DisposeAsync(); } catch (Exception error) { rejected = error; }
                examined.TrySetResult(rejected);
                await examineAfter.Task;
                await subscription!.DisposeAsync();
            });
            callbackDone.TrySetResult(true);
            return default;
        }, options: new() { Debounce = TimeSpan.Zero });
        await server.NotifyAsync();
        await Deadline(callbackDone.Task); await Deadline(subscription.RefreshDrain);
        var closing = Task.Run(async () => await subscription.DisposeAsync());
        try
        {
            await Deadline(cancelEntered.Task);
            examineDuring.TrySetResult(true);
            Assert.IsType<InvalidOperationException>(await Deadline(examined.Task));
        }
        finally { releaseCancel.TrySetResult(true); }
        await Deadline(closing);
        examineAfter.TrySetResult(true);
        await Deadline(escaped!);
        registration.Dispose();
    }

    [Theory]
    [InlineData(McpToolChangeProfile.Python)]
    [InlineData(McpToolChangeProfile.TypeScript)]
    public async Task RoundsStaySerializedWithinAWatcherWhileOtherScopesProgressIndependently(McpToolChangeProfile profile)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var entered = NewSignal<bool>(); var release = NewSignal<bool>(); var trailing = NewSignal<bool>();
        var secondFirst = NewSignal<bool>(); var secondTrailing = NewSignal<bool>();
        var calls = 0; var active = 0; var maximum = 0;
        await using var first = await client.WatchToolChangesAsync(async (snapshot, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(true); await release.Task; }
                else trailing.TrySetResult(true);
            }
            finally { Interlocked.Decrement(ref active); }
        }, new() { Prefix = "first" }, new() { Profile = profile, Debounce = TimeSpan.Zero });
        await using var second = await client.WatchToolChangesAsync((snapshot, token) =>
        {
            if (snapshot.Version == 1) secondFirst.TrySetResult(true);
            if (snapshot.Version == 2) secondTrailing.TrySetResult(true);
            return default;
        }, new() { Prefix = "second" }, new() { Profile = profile, Debounce = TimeSpan.Zero });
        try
        {
            await server.NotifyAsync();
            await Deadline(entered.Task); await Deadline(secondFirst.Task);
            await server.NotifyAsync();
            await Deadline(secondTrailing.Task);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(1, Volatile.Read(ref active));
        }
        finally { release.TrySetResult(true); }
        await Deadline(trailing.Task); await Deadline(first.RefreshDrain); await Deadline(second.RefreshDrain);
        Assert.Equal(1, maximum);
        Assert.Equal(2, calls);
        Assert.Equal(2, first.Current.Version); Assert.Equal(2, second.Current.Version);
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
