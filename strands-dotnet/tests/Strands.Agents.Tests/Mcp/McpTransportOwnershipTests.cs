using System.Net.Http;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using McpClient = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTransportOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConnectionDisposesOnlyATransferredFactoryAndPreservesItsPrimaryError(bool owned)
    {
        var primary = new IOException("connection failed");
        var cleanup = new InvalidOperationException("transport cleanup");
        var transport = new FailingTransport(primary, cleanup);

        var failure = await Assert.ThrowsAsync<IOException>(() => McpClient.CreateAsync(new McpClientSettings { OwnsTransport = owned }, transport));

        Assert.Same(primary, failure);
        Assert.Equal(owned ? 1 : 0, transport.Disposals);
        if (owned) Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(failure.Data["Strands.CleanupErrors"])));
        else Assert.Null(failure.Data["Strands.CleanupErrors"]);
    }

    [Fact]
    public async Task CallerCancellationClosesATransferredFactoryWithItsOriginalCause()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var transport = new FailingTransport(new IOException("must not connect"), null);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, transport, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, transport.Connections);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task CancellationDuringAConnectFailureWinsWithoutLosingTheOwnedCleanupDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new IOException("failed while cancelling");
        var cleanup = new InvalidOperationException("cleanup failure");
        var transport = new FailingTransport(primary, cleanup) { Connecting = cancellation.Cancel };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, transport, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(primary, error.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, transport.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSessionsCloseBeforeOwnedFactoriesAndBorrowedHttpClientsStayOpen(bool legacyObserver)
    {
        using var server = new McpRouterTests.RouterServer();
        using var http = new HttpClient(server);
        var owner = new OwnedFactory(new HttpClientTransport(new() { Endpoint = new Uri("https://router.example.test/mcp"), EnableStandaloneGetStream = false, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false));
        var settings = new McpClientSettings { OwnsTransport = true, TasksConfig = legacyObserver ? new() : null };
        var client = await McpClient.CreateAsync(settings, owner, new() { ProtocolVersion = "2025-11-25" });
        owner.OnDispose = () => Assert.Equal(1, server.Deletes);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(1, owner.Disposals);
        Assert.False(server.Disposed);
    }

    [Fact]
    public async Task SessionCleanupFailureRemainsPrimaryAndFactoryCleanupIsStillAttempted()
    {
        using var server = new McpRouterTests.RouterServer();
        using var http = new HttpClient(server);
        var primary = new IOException("session cleanup");
        var secondary = new InvalidOperationException("factory cleanup");
        var owner = new OwnedFactory(new HttpClientTransport(new() { Endpoint = new Uri("https://router.example.test/mcp"), EnableStandaloneGetStream = false, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false))
        { SessionFailure = primary, FactoryFailure = secondary };
        var client = await McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, owner, new() { ProtocolVersion = "2025-11-25" });

        var error = await Assert.ThrowsAsync<IOException>(() => client.DisposeAsync().AsTask());

        Assert.Same(primary, error);
        Assert.Same(secondary, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task RouterDisposalJoinsAnOwnedCloseAfterTheDisconnectCallerCancelsItsWait()
    {
        var calling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new McpRouterTests.RouterServer { HandleAsync = async (_, _) => { calling.TrySetResult(true); await releaseCall.Task; return new { content = new[] { new { type = "text", text = "done" } } }; } };
        using var http = new HttpClient(server);
        var owner = new OwnedFactory(new HttpClientTransport(new() { Endpoint = new Uri("https://router.example.test/mcp"), EnableStandaloneGetStream = false, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false))
        { OnDispose = () => disposing.TrySetResult(true), DisposeGate = releaseDispose.Task };
        var client = await McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, owner, new() { ProtocolVersion = "2025-11-25" });
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => Task.FromResult(client) } });
        await using var agent = McpRouterTests.NewAgent();
        await McpRouterTests.Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        var active = McpRouterTests.Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "echo" });
        Task? routerDisposal = null;
        try
        {
            await McpRouterTests.Bounded(calling.Task);
            using var cancellation = new CancellationTokenSource();
            var disconnect = McpRouterTests.Command(router, agent, new { command = "disconnect", connection_id = "c" }, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect);
            releaseCall.TrySetResult(true); await active;
            await McpRouterTests.Bounded(disposing.Task);
            routerDisposal = router.DisposeAsync().AsTask();
            Assert.False(routerDisposal.IsCompleted, "The router still owns the removed connection until its actual close finishes.");
        }
        finally
        {
            releaseCall.TrySetResult(true); releaseDispose.TrySetResult(true);
            await active;
            if (routerDisposal is not null) await routerDisposal;
        }
        Assert.Equal(1, owner.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedTransportCleanupCannotAwaitTheRoutersOwnDisposal(bool canceledDisconnect)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enterCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new McpRouterTests.RouterServer();
        using var http = new HttpClient(server);
        McpRouter? instance = null;
        var owner = new OwnedFactory(new HttpClientTransport(new() { Endpoint = new Uri("https://router.example.test/mcp"), EnableStandaloneGetStream = false, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false))
        { OnDisposeAsync = async () => { entered.TrySetResult(true); if (canceledDisconnect) await enterCallback.Task; var nested = instance!.DisposeAsync().AsTask(); if (await Task.WhenAny(nested, release.Task) == nested) await nested; } };
        var client = await McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, owner, new() { ProtocolVersion = "2025-11-25" });
        var router = instance = new(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => Task.FromResult(client) } });
        await using var agent = McpRouterTests.NewAgent();
        await McpRouterTests.Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        if (canceledDisconnect)
        {
            using var cancellation = new CancellationTokenSource();
            var disconnect = McpRouterTests.Command(router, agent, new { command = "disconnect", connection_id = "c" }, cancellation.Token);
            await McpRouterTests.Bounded(entered.Task);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect);
        }
        var disposing = router.DisposeAsync().AsTask();
        enterCallback.TrySetResult(true);
        try
        {
            await McpRouterTests.Bounded(disposing);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => disposing);
            Assert.Contains("own", failure.Message);
        }
        finally
        {
            release.TrySetResult(true);
            try { await disposing; } catch (InvalidOperationException) { }
        }
        Assert.Equal(1, owner.Disposals);
    }

    private sealed class OwnedFactory(HttpClientTransport transport) : IClientTransport, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal Action? OnDispose { get; set; }
        internal Func<Task>? OnDisposeAsync { get; init; }
        internal Exception? SessionFailure { get; init; }
        internal Exception? FactoryFailure { get; init; }
        internal Task? DisposeGate { get; init; }
        public string Name => transport.Name;
        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => new Session(await transport.ConnectAsync(cancellationToken), SessionFailure);
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            try { OnDispose?.Invoke(); if (OnDisposeAsync is { } callback) await callback(); if (DisposeGate is { } gate) await gate; }
            finally { await transport.DisposeAsync(); }
            if (FactoryFailure is { } failure) throw failure;
        }
        private sealed class Session(ITransport connection, Exception? failure) : ITransport
        {
            public string? SessionId => connection.SessionId;
            public ChannelReader<JsonRpcMessage> MessageReader => connection.MessageReader;
            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => connection.SendMessageAsync(message, cancellationToken);
            public async ValueTask DisposeAsync() { await connection.DisposeAsync(); if (failure is not null) throw failure; }
        }
    }

    private sealed class FailingTransport(Exception primary, Exception? cleanup) : IClientTransport, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal int Connections { get; private set; }
        internal Action? Connecting { get; init; }
        public string Name => "ownership-fixture";
        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) { Connections++; Connecting?.Invoke(); throw primary; }
        public ValueTask DisposeAsync() { Disposals++; return cleanup is null ? default : new(Task.FromException(cleanup)); }
    }
}
