using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Strands.Harness.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Harness.Tests;

public sealed class McpIdentityStartupConformanceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    public async Task PartialIdentityReachesInitializeWithTheOmittedCounterpartDefault(string field)
    {
        var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["search"] = new()
            {
                Url = "https://m05.example.test/mcp",
                ApplicationName = field == "name" ? "partial-application" : null,
                ApplicationVersion = field == "version" ? "2.3.4" : null
            }
        }, continueOnError: false);
        using var wire = new Wire();
        await using var factory = new OwnedFactory(wire);
        var server = await ResolveAsync(configuration);
        await using var client = await ConnectAsync(server, factory, default);

        AssertIdentity(wire.Initialize, field == "name" ? "partial-application" : "search", field == "version" ? "2.3.4" : "1.0.0");
        Assert.Null(server.Options.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), factory.ObservedTimeout);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.TransportCalls);
    }

    [Theory]
    [InlineData("tick", "0.0001", 1L)]
    [InlineData("upper", "2147483647", 21474836470000L)]
    public async Task JsonBudgetAcceptsExactNativeTickAndPortableUpperBound(string id, string raw, long ticks)
    {
        var server = await ResolveAsync(JsonBudget(raw));

        Assert.NotEmpty(id);
        Assert.Equal(TimeSpan.FromTicks(ticks), server.Options.StartupTimeout);
        // Parsing precision is asserted without starting a timer or promising its scheduling granularity.
    }

    [Theory]
    [InlineData("string", "\"100\"")]
    [InlineData("overflow", "1e400")]
    [InlineData("above-upper", "2147483648")]
    [InlineData("sub-tick", "0.00001")]
    public async Task InvalidJsonBudgetRejectsBeforeConnectorAdmission(string id, string raw)
    {
        using var wire = new Wire();
        await using var factory = new OwnedFactory(wire);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            var server = await ResolveAsync(JsonBudget(raw));
            await using var client = await ConnectAsync(server, factory, default);
        });

        Assert.NotEmpty(id);
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, factory.Disposals);
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData("negative", -1L)]
    [InlineData("above-upper", 21474836470001L)]
    public async Task InvalidTypedBudgetRejectsBeforeConnectorAdmission(string id, long ticks)
    {
        using var wire = new Wire();
        await using var factory = new OwnedFactory(wire);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["search"] = new() { Url = "https://m05.example.test/mcp", StartupTimeout = TimeSpan.FromTicks(ticks) }
            }, continueOnError: false);
            var server = await ResolveAsync(configuration);
            await using var client = await ConnectAsync(server, factory, default);
        });

        Assert.NotEmpty(id);
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, factory.Disposals);
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public Task LateSuccessAfterDeadlineIsRejectedOnlyAfterOwnedCleanupJoins()
        => ExerciseLateCandidateAsync("deadline", failCleanup: false);

    [Fact]
    public Task LateSuccessAfterCallerCancellationRetainsCallerTokenAndJoinsCleanup()
        => ExerciseLateCandidateAsync("caller", failCleanup: false);

    [Fact]
    public Task CallerCancellationDuringDeadlineCleanupWinsAtSettlement()
        => ExerciseLateCandidateAsync("caller-during-cleanup", failCleanup: false);

    [Theory]
    [InlineData("direct")]
    [InlineData("core")]
    public async Task UnrequestedForeignCancellationRetainsItsExactExceptionAndToken(string route)
    {
        using var caller = new CancellationTokenSource();
        using var foreignSource = new CancellationTokenSource();
        var foreign = new OperationCanceledException("independent transport cancellation", foreignSource.Token);
        foreign.Data["m05-marker"] = "foreign";
        var server = await ResolveAsync(JsonBudget("30000"));
        using var wire = new Wire();
        var factory = new OwnedFactory(wire) { ConnectFailure = foreign };
        var directCalls = 0;
        CancellationToken operationToken = default;
        Task<Client> connecting = route == "direct"
            ? McpConnections.ConnectServerAsync(server, caller.Token, (_, token) =>
            {
                directCalls++;
                operationToken = token;
                return Task.FromException<Client>(foreign);
            })
            : ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting).WaitAsync(Watchdog);
            observedFailure = error;
            Assert.Same(foreign, error);
            Assert.Equal(foreignSource.Token, error.CancellationToken);
            Assert.Equal("foreign", error.Data["m05-marker"]);
            Assert.False(caller.IsCancellationRequested);
            Assert.False((route == "direct" ? operationToken : factory.OperationToken).IsCancellationRequested);
            Assert.Equal(route == "direct" ? 1 : 0, directCalls);
            Assert.Equal(route == "core" ? 1 : 0, factory.Calls);
            Assert.Equal(route == "core" ? 1 : 0, factory.Disposals);
#if NETFRAMEWORK
            if (route == "core") Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            if (route == "core") Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            Assert.Empty(wire.Requests);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(connecting, factory, caller, primary, observedFailure); }
    }

    [Fact]
    public async Task CoreFailureAfterRequestedDeadlineRetainsTheNormalizedCancellationCauseChain()
    {
        using var caller = new CancellationTokenSource();
        var failure = new IOException("independent connect failure after cancellation");
        failure.Data["m05-marker"] = "original-connect-failure";
        var server = await ResolveAsync(JsonBudget("2000"));
        using var wire = new Wire();
        var factory = new OwnedFactory(wire) { HoldConnectFailure = true, ConnectFailure = failure, HoldCleanup = true };
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await factory.TransportEntered.Task.WaitAsync(Watchdog);
            await factory.CancellationObserved.Task.WaitAsync(Watchdog);
            Assert.False(caller.IsCancellationRequested);
            factory.ConnectFailureRelease.TrySetResult(true);
            await factory.CleanupEntered.Task.WaitAsync(Watchdog);
            Assert.False(connecting.IsCompleted);
            factory.CleanupRelease.TrySetResult(true);
            var error = await Assert.ThrowsAsync<TimeoutException>(() => connecting).WaitAsync(Watchdog);
            observedFailure = error;
            var normalized = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Equal(factory.OperationToken, normalized.CancellationToken);
            Assert.Same(failure, normalized.InnerException);
            Assert.Equal("original-connect-failure", error.Data["m05-marker"]);
            Assert.Equal(1, factory.Disposals);
#if NETFRAMEWORK
            Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            Assert.Empty(wire.Requests);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(connecting, factory, caller, primary, observedFailure); }
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("caller")]
    public Task LateCandidateCleanupFailureIsSecondaryToTheCancellationOrDeadlineWinner(string winner)
        => ExerciseLateCandidateAsync(winner, failCleanup: true);

    [Fact]
    public async Task IndependentInitializeFailureRetainsExactPrimaryAndDistinctOwnedFactoryCleanup()
    {
        using var caller = new CancellationTokenSource();
        var failure = new IOException("independent initialize failure");
        failure.Data["m05-marker"] = "initialize-primary";
        var cleanup = new IOException("owned factory cleanup failure");
        var server = await ResolveAsync(JsonBudget("30000"));
        using var wire = new Wire { InitializeFailure = failure };
        var factory = new OwnedFactory(wire) { HoldCleanup = true, DisposeFailure = cleanup };
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await wire.InitializeEntered.Task.WaitAsync(Watchdog);
            await factory.CleanupEntered.Task.WaitAsync(Watchdog);
            Assert.False(connecting.IsCompleted);
            Assert.False(caller.IsCancellationRequested);
            Assert.False(factory.OperationToken.IsCancellationRequested);
            factory.CleanupRelease.TrySetResult(true);
            var error = await Assert.ThrowsAsync<IOException>(() => connecting).WaitAsync(Watchdog);
            observedFailure = error;
            Assert.Same(failure, error);
            Assert.Equal("initialize-primary", error.Data["m05-marker"]);
            Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
            Assert.Equal(1, factory.Disposals);
#if NETFRAMEWORK
            Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            AssertIdentity(wire.Initialize, "search", "1.0.0");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(connecting, factory, caller, primary, observedFailure); }
    }

    [Fact]
    public async Task PrecancelledCallerRejectsBeforeConnectorOrOwnershipAdmission()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var server = await ResolveAsync(JsonBudget("30000"));
        using var wire = new Wire();
        var factory = new OwnedFactory(wire);
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting).WaitAsync(Watchdog);
            observedFailure = error;
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(0, factory.TransportCalls);
            Assert.Equal(0, factory.Disposals);
            Assert.Empty(wire.Requests);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(connecting, factory, caller, primary, observedFailure); }
    }

    private static async Task ExerciseLateCandidateAsync(string winner, bool failCleanup)
    {
        using var caller = new CancellationTokenSource();
        var cleanup = failCleanup ? new IOException("late owned factory cleanup") : null;
        var server = await ResolveAsync(JsonBudget(winner == "caller" ? "30000" : "2000"));
        using var wire = new Wire();
        var factory = new OwnedFactory(wire) { HoldReturn = true, HoldCleanup = true, DisposeFailure = cleanup };
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await factory.ClientReady.Task.WaitAsync(Watchdog);
            Assert.False(connecting.IsCompleted);
            if (winner == "caller") caller.Cancel();
            await factory.CancellationObserved.Task.WaitAsync(Watchdog);
            factory.ReturnRelease.TrySetResult(true);
            await factory.CleanupEntered.Task.WaitAsync(Watchdog);
            Assert.False(connecting.IsCompleted);
            if (winner == "caller-during-cleanup") caller.Cancel();
            factory.CleanupRelease.TrySetResult(true);
            Exception error;
            if (winner == "deadline")
            {
                error = await Assert.ThrowsAsync<TimeoutException>(() => connecting).WaitAsync(Watchdog);
                Assert.False(caller.IsCancellationRequested);
            }
            else
            {
                var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting).WaitAsync(Watchdog);
                error = cancelled;
                Assert.Equal(caller.Token, cancelled.CancellationToken);
            }
            observedFailure = error;
            if (cleanup is not null)
                Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, factory.TransportCalls);
            Assert.Equal(1, factory.Disposals);
#if NETFRAMEWORK
            Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            AssertIdentity(wire.Initialize, "search", "1.0.0");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(connecting, factory, caller, primary, observedFailure); }
    }

    private static McpServerConfiguration JsonBudget(string raw)
        => McpServerConfiguration.Parse("{\"search\":{\"url\":\"https://m05.example.test/mcp\",\"startupTimeoutMs\":" + raw + "}}", continueOnError: false);

    private static async Task<ResolvedMcpServer> ResolveAsync(McpServerConfiguration configuration)
        => Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

    private static Task<Client> ConnectAsync(ResolvedMcpServer server, OwnedFactory factory, CancellationToken token)
        => McpConnections.ConnectServerAsync(server, token, factory.OpenAsync);

    private static HttpClientTransportOptions TransportOptions() => new()
    {
        Endpoint = new Uri("https://m05.example.test/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,
        EnableStandaloneGetStream = false
    };

    private static void AssertIdentity(JsonElement request, string name, string version)
    {
        var info = request.GetProperty("params").GetProperty("clientInfo");
        Assert.Equal(name, info.GetProperty("name").GetString());
        Assert.Equal(version, info.GetProperty("version").GetString());
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task FinishAsync(Task<Client> task, OwnedFactory factory, CancellationTokenSource caller, Exception? primary, Exception? observedFailure)
    {
        // Watchdogs are body failure escapes. Every gate opens before the original task is joined.
        factory.ReturnRelease.TrySetResult(true);
        factory.ConnectFailureRelease.TrySetResult(true);
        factory.CleanupRelease.TrySetResult(true);
        Exception? cancelFailure = null;
        try { caller.Cancel(); }
        catch (Exception cleanup)
        {
            if (primary is null) { primary = cleanup; cancelFailure = cleanup; }
            else if (!ReferenceEquals(primary, cleanup)) primary.Data["McpIdentityStartupConformance.CancelFailure"] = cleanup;
        }
        finally
        {
            Exception? joinFailure = null;
            try { await ObserveJoinedAsync(task, primary, observedFailure); }
            catch (Exception error) { joinFailure = error; throw; }
            finally
            {
                try { await factory.DisposeAsync(); }
                catch (Exception cleanup) when (primary is not null || joinFailure is not null)
                {
                    var failure = primary ?? joinFailure!;
                    if (!ReferenceEquals(failure, cleanup)) failure.Data["McpIdentityStartupConformance.FactoryCleanupFailure"] = cleanup;
                }
            }
        }
        if (cancelFailure is not null) ExceptionDispatchInfo.Capture(cancelFailure).Throw();
    }

    private static async Task ObserveJoinedAsync(Task<Client> task, Exception? primary, Exception? observedFailure)
    {
        Client? late = null;
        try { late = await task; }
        catch (Exception cleanup) when (primary is not null || ReferenceEquals(cleanup, observedFailure))
        {
            if (primary is not null && !ReferenceEquals(primary, cleanup) && !ReferenceEquals(cleanup, observedFailure))
                primary.Data["McpIdentityStartupConformance.JoinFailure"] = cleanup;
        }
        if (late is not null)
        {
            try { await late.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null)
            {
                if (!ReferenceEquals(primary, cleanup)) primary.Data["McpIdentityStartupConformance.LateClientCleanupFailure"] = cleanup;
            }
            if (primary is null) throw new InvalidOperationException("The owned connection succeeded after a fixture failure assertion.");
        }
    }

    private sealed class OwnedFactory : IClientTransport, IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClientTransport _transport;
        private int _disposed;

        internal OwnedFactory(Wire wire)
        {
            _http = new HttpClient(wire, disposeHandler: false);
            _transport = new HttpClientTransport(TransportOptions(), _http, ownsHttpClient: false);
        }

        internal bool HoldReturn { get; init; }
        internal bool HoldConnectFailure { get; init; }
        internal bool HoldCleanup { get; init; }
        internal Exception? ConnectFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        internal int Calls { get; private set; }
        internal int TransportCalls { get; private set; }
        internal int Disposals { get; private set; }
        internal TimeSpan? ObservedTimeout { get; private set; }
        internal CancellationToken OperationToken { get; private set; }
        internal TaskCompletionSource<bool> ClientReady { get; } = Signal();
        internal TaskCompletionSource<bool> ReturnRelease { get; } = Signal();
        internal TaskCompletionSource<bool> TransportEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ConnectFailureRelease { get; } = Signal();
        internal TaskCompletionSource<bool> CancellationObserved { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupEntered { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupRelease { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupCompleted { get; } = Signal();
        public string Name => "m05-conformance-owned-fake-transport";

        internal async Task<Client> OpenAsync(McpClientOptions options, CancellationToken token)
        {
            Calls++;
            ObservedTimeout = options.InitializationTimeout;
            OperationToken = token;
            var selected = new McpClientOptions
            {
                ClientInfo = options.ClientInfo,
                InitializeMeta = options.InitializeMeta,
                InitializationTimeout = options.InitializationTimeout,
                ProtocolVersion = "2025-11-25"
            };
            using var registration = token.Register(() => CancellationObserved.TrySetResult(true));
            var client = await Client.CreateAsync(new McpClientSettings { OwnsTransport = true }, this, selected, token);
            ClientReady.TrySetResult(true);
            // Deliberately bounded by the fixture's finally release, rather than honoring cancellation,
            // to expose Harness ownership of a late candidate without a real transport race.
            if (HoldReturn) await ReturnRelease.Task;
            return client;
        }

        public async Task<ModelContextProtocol.Protocol.ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            TransportCalls++;
            TransportEntered.TrySetResult(true);
            if (HoldConnectFailure) await ConnectFailureRelease.Task;
            if (ConnectFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return await _transport.ConnectAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Disposals++;
            CleanupEntered.TrySetResult(true);
            try
            {
                if (HoldCleanup) await CleanupRelease.Task;
                await _transport.DisposeAsync();
                if (DisposeFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            }
            finally
            {
                _http.Dispose();
                CleanupCompleted.TrySetResult(true);
            }
        }
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal Exception? InitializeFailure { get; init; }
        internal List<JsonElement> Requests { get; } = [];
        internal TaskCompletionSource<bool> InitializeEntered { get; } = Signal();
        internal JsonElement Initialize => Assert.Single(Requests, request => request.GetProperty("method").GetString() == "initialize");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete || request.Method == HttpMethod.Get) return new(HttpStatusCode.NoContent);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement.Clone();
            Requests.Add(root);
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            Assert.Equal("initialize", root.GetProperty("method").GetString());
            InitializeEntered.TrySetResult(true);
            if (InitializeFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "m05-conformance", version = "1.0" } }
                }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Mcp-Session-Id", "m05-conformance-session");
            return response;
        }
    }
}
