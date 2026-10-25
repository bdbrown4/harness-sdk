using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Strands.Agents.Tests.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesMcpLifecycleTests
{
    [Fact]
    public async Task ANewWatchAfterReaddingTheOwnerIsNotCapturedByItsEarlierPhysicalRemoval()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var ownerAgent = Agent(); await using var otherAgent = Agent();
        var context = new ToolProviderContext(ownerAgent); var other = new ToolProviderContext(otherAgent);
        await provider.AddConsumerAsync(context); await provider.AddConsumerAsync(other);
        var entered = Signal(); var release = Signal(); var next = Signal(); var oldCalls = 0;
        var oldLease = await provider.WatchToolsAsync(context, new Observer(tools =>
        {
            Interlocked.Increment(ref oldCalls);
            if (tools[0].Spec.Name == "updated") { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }
        }));
        Task? removing = null; IAsyncDisposable? newLease = null;
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(entered.Task);
            removing = provider.RemoveConsumerAsync(context).AsTask(); Assert.False(removing.IsCompleted);
            await provider.AddConsumerAsync(context);
            newLease = await provider.WatchToolsAsync(context, new Observer(tools => { if (tools[0].Spec.Name == "again") next.TrySetResult(true); }));
            release.TrySetResult(true); await Deadline(removing);
            server.ToolName = "again"; await server.NotifyAsync(); await Deadline(next.Task);
            Assert.Equal(2, oldCalls); Assert.Single(await provider.LoadToolsAsync(context)); Assert.Equal(1, server.ListenCount);
        }
        finally
        {
            release.TrySetResult(true); if (removing is not null) await removing;
            await oldLease.DisposeAsync(); if (newLease is not null) await newLease.DisposeAsync();
            await provider.RemoveConsumerAsync(context); await provider.RemoveConsumerAsync(other);
        }
    }

    [Fact]
    public async Task OneSetOwnerRemovalRetiresAndJoinsEveryIndependentLeaseForThatContext()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var ownerAgent = Agent(); await using var otherAgent = Agent();
        var context = new ToolProviderContext(ownerAgent); var otherContext = new ToolProviderContext(otherAgent);
        await provider.AddConsumerAsync(context); await provider.AddConsumerAsync(context); await provider.AddConsumerAsync(otherContext);
        var entered1 = Signal(); var entered2 = Signal(); var release1 = Signal(); var release2 = Signal();
        var lease1 = await provider.WatchToolsAsync(context, new Observer(tools =>
        { if (tools[0].Spec.Name == "updated") { entered1.TrySetResult(true); release1.Task.GetAwaiter().GetResult(); } }));
        var lease2 = await provider.WatchToolsAsync(context, new Observer(tools =>
        { if (tools[0].Spec.Name == "updated") { entered2.TrySetResult(true); release2.Task.GetAwaiter().GetResult(); } }));
        Task? close = null;
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(Task.WhenAll(entered1.Task, entered2.Task));
            close = provider.RemoveConsumerAsync(context).AsTask(); Assert.False(close.IsCompleted);
            release1.TrySetResult(true); Assert.False(close.IsCompleted);
            release2.TrySetResult(true); await Deadline(close);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadToolsAsync(context).AsTask());
            Assert.Single(await provider.LoadToolsAsync(otherContext)); Assert.Equal(1, server.ListenCount);
        }
        finally
        {
            release1.TrySetResult(true); release2.TrySetResult(true); if (close is not null) await close;
            await lease1.DisposeAsync(); await lease2.DisposeAsync(); await provider.RemoveConsumerAsync(otherContext);
        }
    }

    [Fact]
    public async Task RemovingOneConsumerJoinsItsOwnObserversWhileAnotherConsumerKeepsTheGeneration()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var firstAgent = Agent(); await using var secondAgent = Agent();
        var firstContext = new ToolProviderContext(firstAgent); var secondContext = new ToolProviderContext(secondAgent);
        await provider.AddConsumerAsync(firstContext); await provider.AddConsumerAsync(secondContext);
        var entered = Signal(); var release = Signal(); var other = Signal(); var again = Signal(); var firstCalls = 0;
        var firstLease = await provider.WatchToolsAsync(firstContext, new Observer(tools =>
        {
            Interlocked.Increment(ref firstCalls);
            if (tools[0].Spec.Name == "updated") { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }
        }));
        var secondLease = await provider.WatchToolsAsync(secondContext, new Observer(tools =>
        { if (tools[0].Spec.Name == "updated") other.TrySetResult(true); if (tools[0].Spec.Name == "again") again.TrySetResult(true); }));
        Task? close = null;
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(entered.Task); await Deadline(other.Task);
            close = provider.RemoveConsumerAsync(firstContext).AsTask();
            Assert.False(close.IsCompleted);
            release.TrySetResult(true); await Deadline(close);
            server.ToolName = "again"; await server.NotifyAsync(); await Deadline(again.Task);
            Assert.Equal(2, firstCalls); Assert.Equal(1, server.ListenCount);
        }
        finally
        {
            release.TrySetResult(true); if (close is not null) await close;
            await firstLease.DisposeAsync(); await secondLease.DisposeAsync();
            await provider.RemoveConsumerAsync(firstContext); await provider.RemoveConsumerAsync(secondContext);
        }
    }

    [Fact]
    public async Task DuplicateDeclarationsReceiveIndependentLeasesWithoutReplacingTheirObserver()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context); await provider.AddConsumerAsync(context);
        var left = Signal(); var right = Signal();
        var first = await provider.WatchToolsAsync(context, new Observer(tools => { if (tools[0].Spec.Name == "updated") left.TrySetResult(true); }));
        var second = await provider.WatchToolsAsync(context, new Observer(tools => { if (tools[0].Spec.Name == "updated") right.TrySetResult(true); }));
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(Task.WhenAll(left.Task, right.Task));
            await first.DisposeAsync(); server.ToolName = "again"; await server.NotifyAsync();
            Assert.Equal(1, server.ListenCount);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); await provider.RemoveConsumerAsync(context); await provider.RemoveConsumerAsync(context); }
    }

    [Fact]
    public async Task LastConsumerRemovalJoinsAnInitialObserverBeforeTheWatchLeaseReturns()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context); await provider.LoadToolsAsync(context);
        var entered = Signal(); var release = Signal();
        var watch = Task.Run(async () => await provider.WatchToolsAsync(context, new Observer(_ =>
        { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); })));
        await Deadline(entered.Task);
        var close = provider.RemoveConsumerAsync(context).AsTask();
        try { Assert.False(close.IsCompleted); Assert.False(watch.IsCompleted); }
        finally { release.TrySetResult(true); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
        await Deadline(close); await Deadline(server.ListenClosed.Task);
    }

    [Fact]
    public async Task EstablishmentFailureRetiresOnlyItsProvisionalObserverAndKeepsTheSharedClientUsable()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        Client? client = null;
        var provider = Provider(http, server, captured: value => client = value);
        await using var agent = Agent(); var context = new ToolProviderContext(agent); await provider.AddConsumerAsync(context);
        var expected = new IOException("initial observer");
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => provider.WatchToolsAsync(context, new Observer(_ => throw expected)).AsTask()));
        Assert.True(client!.IsConnected);
        var changed = Signal();
        await using (await provider.WatchToolsAsync(context, new Observer(tools => { if (tools[0].Spec.Name == "updated") changed.TrySetResult(true); })))
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(changed.Task);
        }
        await provider.RemoveConsumerAsync(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserverCannotJoinItsOwnProviderOrSubscriptionLifecycle(bool removeConsumer)
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        IAsyncDisposable? lease = null; var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease = await provider.WatchToolsAsync(context, new Observer(tools =>
        {
            if (tools[0].Spec.Name != "updated") return;
            var error = Record.Exception(() =>
            {
                if (removeConsumer) provider.RemoveConsumerAsync(context).AsTask().GetAwaiter().GetResult();
                else lease!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });
            observed.TrySetResult(error!);
        }));
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync();
            Assert.IsType<InvalidOperationException>(await Deadline(observed.Task));
        }
        finally { await lease.DisposeAsync(); await provider.RemoveConsumerAsync(context); }
    }

    [Fact]
    public async Task ExpiredObserverContextCanDisposeTheLeaseAfterTheWorkerHasReturned()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        IAsyncDisposable? lease = null; var release = Signal(); var escaped = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease = await provider.WatchToolsAsync(context, new Observer(tools =>
        {
            if (tools[0].Spec.Name == "updated") escaped.TrySetResult(Task.Run(async () => { await release.Task; await lease!.DisposeAsync(); }));
        }));
        server.ToolName = "updated"; await server.NotifyAsync();
        var close = await Deadline(escaped.Task);
        // A second full snapshot proves the first callback/receipt returned, without a timing delay.
        var next = Signal(); await using (await provider.WatchToolsAsync(context, new Observer(_ => next.TrySetResult(true)))) { await Deadline(next.Task); }
        release.TrySetResult(true); await Deadline(close); await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task ABlockedObserverDoesNotBlockAnotherConsumerAndDisposeJoinsItPhysically()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent); await provider.AddConsumerAsync(context);
        var entered = Signal(); var release = Signal(); var other = Signal();
        var slow = await provider.WatchToolsAsync(context, new Observer(tools =>
        { if (tools[0].Spec.Name == "updated") { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); } }));
        var fast = await provider.WatchToolsAsync(context, new Observer(tools => { if (tools[0].Spec.Name == "updated") other.TrySetResult(true); }));
        Task? close = null;
        try
        {
            server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(entered.Task); await Deadline(other.Task);
            close = slow.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); await fast.DisposeAsync(); Assert.False(close.IsCompleted);
        }
        finally { release.TrySetResult(true); await slow.DisposeAsync(); await fast.DisposeAsync(); await provider.RemoveConsumerAsync(context); }
        await Deadline(close!);
    }

    [Fact]
    public async Task AChangeDuringInitialDeliveryIsQueuedInVersionOrderWithoutASecondListen()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var accepted = Signal(); var provider = Provider(http, server, changed: (_, _) => { accepted.TrySetResult(true); return default; });
        await using var agent = Agent(); var context = new ToolProviderContext(agent); await provider.AddConsumerAsync(context);
        var entered = Signal(); var release = Signal(); var refreshed = Signal(); var names = new List<string>();
        var watch = Task.Run(async () => await provider.WatchToolsAsync(context, new Observer(tools =>
        {
            lock (names) names.Add(tools[0].Spec.Name);
            if (tools[0].Spec.Name == "first") { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }
            else refreshed.TrySetResult(true);
        })));
        try
        {
            await Deadline(entered.Task); server.ToolName = "updated"; await server.NotifyAsync(); await Deadline(accepted.Task);
            Assert.False(watch.IsCompleted); release.TrySetResult(true); await Deadline(refreshed.Task);
            lock (names) Assert.Equal(new[] { "first", "updated" }, names);
            Assert.Equal(1, server.ListenCount);
        }
        finally { release.TrySetResult(true); await (await watch).DisposeAsync(); await provider.RemoveConsumerAsync(context); }
    }

    [Fact]
    public async Task TerminalListenFailureIsRelayedToEveryObserverAndRetainsItsLastPublishedBatch()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = Provider(http, server); await using var agent = Agent(); var context = new ToolProviderContext(agent); await provider.AddConsumerAsync(context);
        var first = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease1 = await provider.WatchToolsAsync(context, new Observer(_ => { }, error => first.TrySetResult(error)));
        var lease2 = await provider.WatchToolsAsync(context, new Observer(_ => { }, error => second.TrySetResult(error)));
        try
        {
            await server.CancelListenAsync(); var error = await Deadline(first.Task);
            Assert.Same(error, await Deadline(second.Task));
            Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(() => provider.LoadToolsAsync(context).AsTask()));
        }
        finally
        {
            await lease1.DisposeAsync(); await lease2.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => provider.RemoveConsumerAsync(context).AsTask());
        }
    }

    private sealed class Observer(Action<IReadOnlyList<ITool>> next, Action<Exception>? error = null) : IObserver<IReadOnlyList<ITool>>
    {
        public void OnNext(IReadOnlyList<ITool> value) => next(value);
        public void OnError(Exception value) => error?.Invoke(value);
        public void OnCompleted() { }
    }
    private static Agent Agent() => new(new() { Model = new AgentToolProviderUpdatesTests.TextModel() });
    private static McpToolProvider Provider(HttpClient http, McpToolChangeLoopbackServer server,
        Action<Client>? captured = null, Func<McpToolsChanged, CancellationToken, ValueTask>? changed = null) => new(async token =>
        {
            var client = await Client.CreateHttpAsync(http,
                new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
                new() { ProtocolVersion = "2026-07-28" }, token); captured?.Invoke(client); return client;
        }, new() { ToolChanges = new() { Debounce = TimeSpan.Zero }, OnToolsChanged = changed });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Deadline(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static async Task<T> Deadline<T>(Task<T> task) { await Deadline((Task)task); return await task; }
}
