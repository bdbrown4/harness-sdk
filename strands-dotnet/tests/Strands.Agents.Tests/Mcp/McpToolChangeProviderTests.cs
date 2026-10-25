using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeProviderTests
{
    [Fact]
    public void AProviderObserverRequiresExplicitNotificationOptIn()
    {
        Assert.Throws<ArgumentException>(() => new McpToolProvider(_ => throw new NotSupportedException(),
            new() { OnToolsChanged = (snapshot, token) => default }));
    }

    [Fact]
    public async Task OptInReplacesTheSharedCacheBeforeCallingItsBorrowedObserver()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        var changed = Signal<McpToolsChanged>();
        var provider = new McpToolProvider(token => ConnectAsync(http, server.Endpoint, token), new()
        {
            ToolChanges = new() { Debounce = TimeSpan.Zero },
            Discovery = new() { Prefix = "api" },
            OnToolsChanged = (snapshot, token) => { changed.TrySetResult(snapshot); return default; },
        });
        await provider.AddConsumerAsync(context);
        try
        {
            var old = await provider.LoadToolsAsync(context);
            Assert.Equal(1, server.ListenCount);
            Assert.Equal("api_first", Assert.Single(old).Spec.Name);
            server.ToolName = "updated";
            await server.NotifyAsync();
            var accepted = await Deadline(changed.Task);

            Assert.Equal("api_updated", Assert.Single(await provider.LoadToolsAsync(context)).Spec.Name);
            Assert.Equal("api_updated", Assert.Single(accepted.Tools).Spec.Name);
            Assert.Equal("api_first", Assert.Single(old).Spec.Name);
        }
        finally { await provider.RemoveConsumerAsync(context); }
        await Deadline(server.ListenClosed.Task);
    }

    [Fact]
    public async Task TerminalSubscriptionLossIsObservableOnFutureLoads()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        Client? client = null;
        var provider = new McpToolProvider(async token => client = await ConnectAsync(http, server.Endpoint, token),
            new() { ToolChanges = new() { Debounce = TimeSpan.Zero } });
        await provider.AddConsumerAsync(context);
        Exception? terminal = null;
        Exception? primary = null;
        try
        {
            Assert.Single(await provider.LoadToolsAsync(context));
            Assert.Equal(1, server.ListenCount);
            var completion = Signal<Exception>();
            await using var updates = await provider.WatchToolsAsync(context, new TerminalObserver(completion));
            var dispatched = Signal<bool>();
            await using var observe = client!.Protocol.RegisterNotificationHandler("notifications/cancelled", (notification, token) =>
            { dispatched.TrySetResult(true); return default; });
            await server.CancelListenAsync();
            await Deadline(dispatched.Task);

            // The terminal observer runs after the notification channel has physically closed.
            var subscriptionFailure = await Deadline(completion.Task);
            var observed = await Assert.ThrowsAnyAsync<Exception>(() => provider.LoadToolsAsync(context).AsTask());
            var failure = Assert.IsAssignableFrom<OperationCanceledException>(observed);
            terminal = failure;
            Assert.Same(subscriptionFailure, failure);
            Assert.Contains("server cancelled", failure.Message);
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => provider.LoadToolsAsync(context).AsTask()));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                if (terminal is null) await provider.RemoveConsumerAsync(context);
                else Assert.Same(terminal, await Assert.ThrowsAnyAsync<Exception>(() => provider.RemoveConsumerAsync(context).AsTask()));
            }
            catch (Exception cleanup) when (primary is not null)
            {
                if (!ReferenceEquals(primary, cleanup))
                {
                    const string key = "Strands.CleanupErrors";
                    var errors = primary.Data[key] is IEnumerable<Exception> existing ? existing.ToList() : new List<Exception>();
                    if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                    primary.Data[key] = errors;
                }
            }
        }
        await provider.AddConsumerAsync(context);
        Assert.Single(await provider.LoadToolsAsync(context));
        Assert.Equal(2, server.ListenCount);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task DefaultProviderRemainsCachedWithoutNotificationTraffic()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        var provider = new McpToolProvider(token => ConnectAsync(http, server.Endpoint, token));
        await provider.AddConsumerAsync(context);
        try
        {
            var first = await provider.LoadToolsAsync(context);
            server.ToolName = "ignored";
            Assert.Same(first, await provider.LoadToolsAsync(context));
            Assert.Equal("first", Assert.Single(first).Spec.Name);
            Assert.Equal(0, server.ListenCount);
            Assert.Equal(1, server.ListCount);
        }
        finally { await provider.RemoveConsumerAsync(context); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProviderCannotWaitOnItsOwnRefreshFilterOrObserver(bool fromFilter)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null;
        var refresh = false;
        var attempted = Signal<Exception?>();
        void Attempt()
        {
            Exception? failure = null;
            try { provider!.RemoveConsumerAsync(context).AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; }
            attempted.TrySetResult(failure);
        }
        provider = new(token => ConnectAsync(http, server.Endpoint, token), new()
        {
            ToolChanges = new() { Debounce = TimeSpan.Zero },
            Discovery = new() { ToolFilter = name => { if (refresh && fromFilter) Attempt(); return true; } },
            OnToolsChanged = (snapshot, token) => { if (!fromFilter) Attempt(); return default; },
        });
        await provider.AddConsumerAsync(context);
        try
        {
            Assert.Single(await provider.LoadToolsAsync(context));
            refresh = true;
            await server.NotifyAsync();
            Assert.IsType<InvalidOperationException>(await Deadline(attempted.Task));
            Assert.Single(await provider.LoadToolsAsync(context));
        }
        finally { await provider.RemoveConsumerAsync(context); }
    }

    [Fact]
    public async Task InitialProviderFiltersCannotEnterTheirOwnProviderLifecycle()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null;
        Exception? rejected = null;
        provider = new(token => ConnectAsync(http, server.Endpoint, token), new()
        {
            ToolChanges = new() { Debounce = TimeSpan.Zero },
            Discovery = new()
            {
                ToolFilter = name =>
            {
                try { provider!.RemoveConsumerAsync(context).AsTask().GetAwaiter().GetResult(); }
                catch (Exception error) { rejected = error; }
                return true;
            }
            },
        });
        await provider.AddConsumerAsync(context);
        try
        {
            Assert.Single(await provider.LoadToolsAsync(context));
            Assert.IsType<InvalidOperationException>(rejected);
        }
        finally { await provider.RemoveConsumerAsync(context); }
    }

    [Theory]
    [InlineData(McpToolChangeProfile.Python)]
    [InlineData(McpToolChangeProfile.TypeScript)]
    public async Task AFailedProviderObserverDoesNotRollBackItsAcceptedCache(McpToolChangeProfile profile)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        var called = Signal<bool>();
        var provider = new McpToolProvider(token => ConnectAsync(http, server.Endpoint, token), new()
        {
            ToolChanges = new() { Profile = profile, Debounce = TimeSpan.Zero },
            OnToolsChanged = (snapshot, token) => { called.TrySetResult(true); throw new IOException("borrowed provider observer"); },
        });
        await provider.AddConsumerAsync(context);
        try
        {
            Assert.Single(await provider.LoadToolsAsync(context));
            server.ToolName = "accepted";
            await server.NotifyAsync();
            await Deadline(called.Task);
            Assert.Equal("accepted", Assert.Single(await provider.LoadToolsAsync(context)).Spec.Name);
        }
        finally { await provider.RemoveConsumerAsync(context); }
    }

    [Fact]
    public async Task CancellingOneProviderLoadDoesNotCancelAnotherConsumersPendingAcknowledgement()
    {
        await using var server = new McpToolChangeLoopbackServer { HoldAcknowledgement = true };
        using var http = new HttpClient();
        await using var first = NewAgent(); await using var second = NewAgent();
        var firstContext = new ToolProviderContext(first); var secondContext = new ToolProviderContext(second);
        var provider = new McpToolProvider(token => ConnectAsync(http, server.Endpoint, token),
            new() { ToolChanges = new() { Debounce = TimeSpan.Zero } });
        await provider.AddConsumerAsync(firstContext); await provider.AddConsumerAsync(secondContext);
        using var caller = new CancellationTokenSource();
        var leader = provider.LoadToolsAsync(firstContext, caller.Token).AsTask();
        await Deadline(server.ListenEntered.Task);
        var follower = provider.LoadToolsAsync(secondContext).AsTask();
        try
        {
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
            Assert.Equal(caller.Token, error.CancellationToken);
            await provider.RemoveConsumerAsync(firstContext);
            Assert.False(server.ListenClosed.Task.IsCompleted);
        }
        finally { server.ReleaseAcknowledgement.TrySetResult(true); }
        Assert.Single(await Deadline(follower));
        await provider.RemoveConsumerAsync(secondContext);
        await Deadline(server.ListenClosed.Task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectorCancellationCallbacksCannotJoinTheirOwnClosingGeneration(bool notifications)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        await using var agent = NewAgent();
        var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null;
        var attempted = Signal<Exception?>(); var release = Signal<bool>();
        Task? nested = null;
        CancellationTokenRegistration registration = default;
        provider = new(async token =>
        {
            registration = token.Register(() =>
            {
                Exception? rejected = null;
                try { nested = provider!.RemoveConsumerAsync(context).AsTask(); } catch (Exception error) { rejected = error; }
                attempted.TrySetResult(rejected);
                // Keep a failing draft physically drainable rather than awaiting its self-cycle.
                release.Task.GetAwaiter().GetResult();
            });
            return await ConnectAsync(http, server.Endpoint, token);
        }, new() { ToolChanges = notifications ? new() { Debounce = TimeSpan.Zero } : null });
        await provider.AddConsumerAsync(context);
        Assert.Single(await provider.LoadToolsAsync(context));
        var closing = Task.Run(async () => await provider.RemoveConsumerAsync(context));
        try { Assert.IsType<InvalidOperationException>(await Deadline(attempted.Task)); }
        finally { release.TrySetResult(true); }
        await Deadline(closing);
        if (nested is not null) await Deadline(nested);
        registration.Dispose();
    }

    private static Agent NewAgent() => new(new AgentOptions { Model = new NeverModel() });
    private sealed class TerminalObserver(TaskCompletionSource<Exception> completion) : IObserver<IReadOnlyList<ITool>>
    {
        public void OnNext(IReadOnlyList<ITool> tools) { }
        public void OnError(Exception error) => completion.TrySetResult(error);
        public void OnCompleted() => completion.TrySetException(new InvalidOperationException("The MCP subscription completed without its expected terminal error."));
    }
    private sealed class NeverModel : IModel
    { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Deadline<T>(Task<T> task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); return await task; }
    private static async Task Deadline(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint, CancellationToken token) => Client.CreateHttpAsync(http,
        new() { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new() { ProtocolVersion = "2026-07-28" }, token);
}
