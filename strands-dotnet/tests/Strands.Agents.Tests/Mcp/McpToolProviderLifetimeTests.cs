using System.Net.Http;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using McpClient = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolProviderLifetimeTests
{
    [Fact]
    public Task RemovingAndReaddingAnOwnerCannotResurrectItsPendingWatch() => VerifyPendingWatchAdmission(remove: true);

    [Fact]
    public Task RepeatedAddKeepsAPendingWatchUnderItsOriginalAdmission() => VerifyPendingWatchAdmission(remove: false);

    private static async Task VerifyPendingWatchAdmission(bool remove)
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal();
        using var server = new McpRouterTests.RouterServer();
        var connections = 0;
        var provider = new McpToolProvider(async token =>
        {
            Interlocked.Increment(ref connections); entered.TrySetResult(true);
            await release.Task;
            return await server.ConnectAsync(token);
        });
        await using var firstAgent = McpRouterTests.NewAgent(); await using var secondAgent = McpRouterTests.NewAgent();
        var first = new ToolProviderContext(firstAgent); var second = new ToolProviderContext(secondAgent);
        await provider.AddConsumerAsync(first); await provider.AddConsumerAsync(second);
        var previousObserver = new CutoffObserver();
        var pending = provider.WatchToolsAsync(first, previousObserver).AsTask();
        IAsyncDisposable? previousLease = null; IAsyncDisposable? currentLease = null;
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            if (remove) await provider.RemoveConsumerAsync(first);
            await provider.AddConsumerAsync(first);
            release.TrySetResult(true);
            await McpToolProviderTests.Bounded(pending);
            Exception? failure = null;
            try { previousLease = await pending; } catch (Exception error) { failure = error; }
            Assert.Equal(remove ? 0 : 1, previousObserver.Calls);
            if (remove) Assert.IsType<InvalidOperationException>(failure);
            else Assert.Null(failure);
            var currentObserver = new CutoffObserver();
            currentLease = await provider.WatchToolsAsync(first, currentObserver);
            Assert.Equal(1, currentObserver.Calls);
            Assert.Same(Assert.Single(await provider.LoadToolsAsync(second)), Assert.Single(currentObserver.Tools!));
            Assert.Equal(1, connections);
            Assert.False(server.Disposed);
        }
        finally
        {
            release.TrySetResult(true);
            await McpToolProviderTests.Bounded(pending);
            if (previousLease is null && pending.Status == TaskStatus.RanToCompletion) previousLease = await pending;
            if (previousLease is not null) await previousLease.DisposeAsync();
            if (currentLease is not null) await currentLease.DisposeAsync();
            await provider.RemoveConsumerAsync(first); await provider.RemoveConsumerAsync(second);
        }
        Assert.True(server.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredCallbackContextCannotHideANewerPhysicalObserver(bool removeConsumer)
    {
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(server.ConnectAsync);
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        using var applicationCancellation = new CancellationTokenSource();
        CancellationTokenRegistration registration = default;
        IAsyncDisposable? firstLease = null; IAsyncDisposable? secondLease = null;
        Exception? rejection = null; Exception? secondFailure = null; Task? reentered = null;
        try
        {
            firstLease = await provider.WatchToolsAsync(context, new CutoffObserver(_ =>
            {
                registration = applicationCancellation.Token.Register(() =>
                {
                    try
                    {
                        reentered = removeConsumer ? provider.RemoveConsumerAsync(context).AsTask() : firstLease!.DisposeAsync().AsTask();
                    }
                    catch (Exception error) { rejection = error; }
                });
            }));
            try
            {
                secondLease = await provider.WatchToolsAsync(context, new CutoffObserver(_ => applicationCancellation.Cancel()));
            }
            catch (Exception error) { secondFailure = error; }
            if (reentered is not null) { await McpToolProviderTests.Bounded(reentered); await reentered; }
            Assert.IsType<InvalidOperationException>(rejection);
            Assert.Null(reentered);
            Assert.Null(secondFailure);
        }
        finally
        {
            registration.Dispose();
            if (secondLease is not null) await secondLease.DisposeAsync();
            if (firstLease is not null) await firstLease.DisposeAsync();
            await provider.RemoveConsumerAsync(context);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredCallbackContextCannotHideTheSynchronousLoadCallback(bool connector)
    {
        using var firstServer = new McpRouterTests.RouterServer(); using var secondServer = new McpRouterTests.RouterServer();
        using var applicationCancellation = new CancellationTokenSource();
        var connections = 0;
        var provider = new McpToolProvider(token =>
        {
            var current = ++connections;
            if (current == 2 && connector) applicationCancellation.Cancel();
            return (current == 1 ? firstServer : secondServer).ConnectAsync(token);
        }, new() { Discovery = new() { ToolFilter = _ => { if (connections == 2 && !connector) applicationCancellation.Cancel(); return true; } } });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        CancellationTokenRegistration registration = default;
        Exception? rejection = null; Task? reentered = null;
        try
        {
            await using (await provider.WatchToolsAsync(context, new CutoffObserver(_ =>
            {
                registration = applicationCancellation.Token.Register(() =>
                {
                    try { reentered = provider.RemoveConsumerAsync(context).AsTask(); }
                    catch (Exception error) { rejection = error; }
                });
            }))) { }
            await provider.RemoveConsumerAsync(context);
            Assert.True(firstServer.Disposed);
            await provider.AddConsumerAsync(context);
            Exception? failure = null; IReadOnlyList<ITool>? tools = null;
            try { tools = await provider.LoadToolsAsync(context); } catch (Exception error) { failure = error; }
            if (reentered is not null) { await McpToolProviderTests.Bounded(reentered); await reentered; }
            Assert.IsType<InvalidOperationException>(rejection);
            Assert.Null(reentered);
            Assert.Null(failure);
            Assert.Single(tools!);
            Assert.Equal(2, connections);
            Assert.False(secondServer.Disposed);
        }
        finally { registration.Dispose(); await provider.RemoveConsumerAsync(context); }
        Assert.True(secondServer.Disposed);
    }

    [Fact]
    public async Task CompletedObserverContextDoesNotForbidLaterExternalRemoval()
    {
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(server.ConnectAsync);
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        using var applicationCancellation = new CancellationTokenSource();
        CancellationTokenRegistration registration = default;
        Task? removal = null; Exception? failure = null;
        try
        {
            await using var lease = await provider.WatchToolsAsync(context, new CutoffObserver(_ =>
            {
                registration = applicationCancellation.Token.Register(() =>
                {
                    try { removal = provider.RemoveConsumerAsync(context).AsTask(); }
                    catch (Exception error) { failure = error; }
                });
            }));
            applicationCancellation.Cancel();
            Assert.Null(failure);
            Assert.NotNull(removal);
            await McpToolProviderTests.Bounded(removal!); await removal!;
            Assert.True(server.Disposed);
        }
        finally { registration.Dispose(); await provider.RemoveConsumerAsync(context); }
    }

    private sealed class CutoffObserver(Action<IReadOnlyList<ITool>>? next = null) : IObserver<IReadOnlyList<ITool>>
    {
        internal int Calls;
        internal IReadOnlyList<ITool>? Tools;
        public void OnNext(IReadOnlyList<ITool> tools) { Tools = tools; Interlocked.Increment(ref Calls); next?.Invoke(tools); }
        public void OnError(Exception error) => throw new InvalidOperationException("Unexpected provider termination.", error);
        public void OnCompleted() { }
    }

    [Fact]
    public async Task LastRemovalJoinsAnUncooperativeConnectorBeforeTheNextGenerationStarts()
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal();
        var calls = 0; CancellationToken lifetime = default;
        using var firstServer = new McpRouterTests.RouterServer(); using var secondServer = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { lifetime = token; entered.TrySetResult(true); await release.Task; return await firstServer.ConnectAsync(CancellationToken.None); }
            return await secondServer.ConnectAsync(token);
        });
        await using var firstAgent = McpRouterTests.NewAgent(); await using var nextAgent = McpRouterTests.NewAgent();
        var first = new ToolProviderContext(firstAgent); var next = new ToolProviderContext(nextAgent);
        await provider.AddConsumerAsync(first);
        using var cancellation = new CancellationTokenSource();
        var loading = provider.LoadToolsAsync(first, cancellation.Token).AsTask();
        await McpToolProviderTests.Bounded(entered.Task);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        var removing = provider.RemoveConsumerAsync(first).AsTask();
        Assert.True(lifetime.IsCancellationRequested); Assert.False(removing.IsCompleted);
        await provider.AddConsumerAsync(next);
        var nextLoading = provider.LoadToolsAsync(next).AsTask();
        Assert.False(nextLoading.IsCompleted); Assert.Equal(1, calls);
        release.TrySetResult(true);
        await McpToolProviderTests.Bounded(removing); await removing;
        Assert.True(firstServer.Disposed);
        Assert.Single(await nextLoading); Assert.Equal(2, calls);
        await provider.RemoveConsumerAsync(next);
    }

    [Fact]
    public async Task LastRemovalJoinsAnAdmittedUncooperativeDiscoveryCallback()
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal();
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(server.ConnectAsync, new() { Discovery = new() { ToolFilter = _ => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); return true; } } });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        using var caller = new CancellationTokenSource();
        var loading = Task.Run(async () => await provider.LoadToolsAsync(context, caller.Token));
        Task? removing = null;
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
            removing = provider.RemoveConsumerAsync(context).AsTask();
            Assert.False(removing.IsCompleted); Assert.False(server.Disposed);
        }
        finally { release.TrySetResult(true); if (removing is not null) await removing; }
        Assert.True(server.Disposed);
    }

    [Fact]
    public async Task LastRemovalJoinsOwnedFactoryDisposalAndDoesNotDisposeBorrowedHttp()
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal();
        using var server = new McpRouterTests.RouterServer(); using var http = new HttpClient(server);
        var owner = new OwnedTransport(http) { Disposing = () => { entered.TrySetResult(true); return release.Task; } };
        var provider = new McpToolProvider(token => owner.ConnectClientAsync(token));
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context); Assert.Single(await provider.LoadToolsAsync(context));
        var removing = provider.RemoveConsumerAsync(context).AsTask();
        try
        {
            await McpToolProviderTests.Bounded(entered.Task);
            Assert.False(removing.IsCompleted); Assert.Equal(1, server.Deletes); Assert.Equal(1, owner.Disposals);
        }
        finally { release.TrySetResult(true); await removing; }
        Assert.False(server.Disposed);
        await provider.RemoveConsumerAsync(context); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task CleanupPreservesTheSessionFailureAndAttemptsFactoryCleanup()
    {
        using var server = new McpRouterTests.RouterServer(); using var http = new HttpClient(server);
        var primary = new IOException("session close"); var secondary = new InvalidOperationException("factory close");
        var owner = new OwnedTransport(http) { SessionFailure = primary, FactoryFailure = secondary };
        var provider = new McpToolProvider(owner.ConnectClientAsync);
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context); await provider.LoadToolsAsync(context);
        var failure = await Assert.ThrowsAsync<IOException>(() => provider.RemoveConsumerAsync(context).AsTask());
        Assert.Same(primary, failure);
        Assert.Same(secondary, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(failure.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, owner.Disposals); Assert.False(server.Disposed);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task AnUnobservedLateConnectorFailureIsObservableFromPhysicalRemoval()
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal(); var original = new IOException("late connector failure");
        var provider = new McpToolProvider(async _ => { entered.TrySetResult(true); await release.Task; throw original; });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        using var caller = new CancellationTokenSource();
        var load = provider.LoadToolsAsync(context, caller.Token).AsTask(); await McpToolProviderTests.Bounded(entered.Task);
        caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        var removing = provider.RemoveConsumerAsync(context).AsTask(); Assert.False(removing.IsCompleted);
        release.TrySetResult(true);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => removing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnobservedLateIndependentCancellationRemainsObservableAfterLifetimeCancellation(bool independentToken)
    {
        var entered = McpToolProviderTests.Signal();
        var connector = new TaskCompletionSource<McpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var independent = new CancellationTokenSource(); independent.Cancel();
        var original = new OperationCanceledException("late independent cancellation", null, independentToken ? independent.Token : default);
        var provider = new McpToolProvider(_ => { entered.TrySetResult(true); return connector.Task; });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        using var caller = new CancellationTokenSource();
        var loading = provider.LoadToolsAsync(context, caller.Token).AsTask();
        await McpToolProviderTests.Bounded(entered.Task);
        caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        var removing = provider.RemoveConsumerAsync(context).AsTask();
        Assert.False(removing.IsCompleted, "Removal must join the admitted connector rather than abandoning it.");
        connector.TrySetException(original);
        Assert.Same(original, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removing));
    }

    [Fact]
    public async Task ConnectorCallbacksCannotAwaitTheirOwnProviderLifecycle()
    {
        using var server = new McpRouterTests.RouterServer();
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null;
        provider = new(async token =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider!.RemoveConsumerAsync(context).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider!.LoadToolsAsync(context).AsTask());
            return await server.ConnectAsync(token);
        });
        await provider.AddConsumerAsync(context); Assert.Single(await provider.LoadToolsAsync(context));
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task OwnedCleanupCallbacksCannotAwaitTheirOwnPublishedCloseTask()
    {
        using var server = new McpRouterTests.RouterServer(); using var http = new HttpClient(server);
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null;
        var owner = new OwnedTransport(http) { Disposing = async () => await provider!.RemoveConsumerAsync(context) };
        provider = new(owner.ConnectClientAsync);
        await provider.AddConsumerAsync(context); await provider.LoadToolsAsync(context);
        var removing = provider.RemoveConsumerAsync(context).AsTask();
        await McpToolProviderTests.Bounded(removing);
        Assert.Contains("own provider", (await Assert.ThrowsAsync<InvalidOperationException>(() => removing)).Message);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task CapturedLifetimeCancellationCallbacksCannotAwaitTheirOwnRemoval()
    {
        using var server = new McpRouterTests.RouterServer();
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        McpToolProvider? provider = null; Exception? nested = null; CancellationTokenRegistration registration = default;
        provider = new(async token =>
        {
            registration = token.Register(() => { try { provider!.RemoveConsumerAsync(context).AsTask().GetAwaiter().GetResult(); } catch (Exception error) { nested = error; } });
            return await server.ConnectAsync(token);
        });
        await provider.AddConsumerAsync(context); await provider.LoadToolsAsync(context);
        try { await provider.RemoveConsumerAsync(context); Assert.IsType<InvalidOperationException>(nested); }
        finally { registration.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentConnectorCancellationIsNeverAStickySuccessfulEmptyLoad(bool continueOnError)
    {
        var calls = 0; using var independent = new CancellationTokenSource(); independent.Cancel();
        var original = new OperationCanceledException("independent cancellation", null, independent.Token);
        var provider = new McpToolProvider(_ => { calls++; return Task.FromException<McpClient>(original); }, new() { ContinueOnError = continueOnError });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        for (var index = 0; index < 2; index++) Assert.Same(original, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoadToolsAsync(context).AsTask()));
        Assert.Equal(2, calls); await provider.RemoveConsumerAsync(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredObserverContextCannotReenterOwnedFactoryDisposal(bool deferredSession)
    {
        using var server = new McpRouterTests.RouterServer(); using var http = new HttpClient(server);
        using var application = new CancellationTokenSource();
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal();
        Exception? rejection = null; Task? reentered = null;
        McpToolProvider? provider = null;
        var transport = new OwnedTransport(http)
        {
            BeforeSessionDispose = deferredSession ? () => { entered.TrySetResult(true); return release.Task; } : null,
            Disposing = () =>
            {
                application.Cancel();
                return Task.CompletedTask;
            }
        };
        provider = new(transport.ConnectClientAsync);
        await provider.AddConsumerAsync(context);
        CancellationTokenRegistration registration = default;
        Task? removing = null; IAsyncDisposable? lease = null;
        try
        {
            lease = await provider.WatchToolsAsync(context, new CutoffObserver(_ =>
            {
                registration = application.Token.Register(() =>
                {
                    try { reentered = provider.RemoveConsumerAsync(context).AsTask(); }
                    catch (Exception error) { rejection = error; }
                });
            }));
            removing = provider.RemoveConsumerAsync(context).AsTask();
            if (deferredSession)
            {
                await McpToolProviderTests.Bounded(entered.Task);
                Assert.False(removing.IsCompleted);
                release.TrySetResult(true);
            }
            await McpToolProviderTests.Bounded(removing); await removing;
            Assert.IsType<InvalidOperationException>(rejection);
            Assert.Null(reentered);
            Assert.Equal(1, transport.Disposals);
        }
        finally
        {
            release.TrySetResult(true);
            registration.Dispose();
            if (removing is not null) { await McpToolProviderTests.Bounded(removing); await removing; }
            if (lease is not null) await lease.DisposeAsync();
            await provider.RemoveConsumerAsync(context);
        }
    }

    internal sealed class OwnedTransport : IClientTransport, IAsyncDisposable
    {
        private readonly HttpClientTransport _transport;
        internal OwnedTransport(HttpClient http) => _transport = new(new() { Endpoint = new Uri("https://router.example.test/mcp"), EnableStandaloneGetStream = false, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false);
        internal int Disposals { get; private set; }
        internal Func<Task>? Disposing { get; init; }
        internal Func<Task>? BeforeSessionDispose { get; init; }
        internal Exception? SessionFailure { get; init; }
        internal Exception? FactoryFailure { get; init; }
        public string Name => _transport.Name;
        internal Task<McpClient> ConnectClientAsync(CancellationToken token) => McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, this, new() { ProtocolVersion = "2025-11-25" }, token);
        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
            => new Session(await _transport.ConnectAsync(cancellationToken), SessionFailure, BeforeSessionDispose);
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            try { if (Disposing is { } callback) await callback(); }
            finally { await _transport.DisposeAsync(); }
            if (FactoryFailure is { } failure) throw failure;
        }
        private sealed class Session(ITransport inner, Exception? failure, Func<Task>? beforeDispose) : ITransport
        {
            public string? SessionId => inner.SessionId;
            public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);
            public async ValueTask DisposeAsync()
            {
                if (beforeDispose is not null) await beforeDispose();
                await inner.DisposeAsync(); if (failure is not null) throw failure;
            }
        }
    }
}
