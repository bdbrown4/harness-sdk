using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Strands.Harness.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Harness.Tests;

public sealed class McpIdentityStartupTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TypedIdentityAndStartupTimeoutSurviveTheOwnedDeclarationAndReachInitialize()
    {
        var declarations = new Dictionary<string, McpServerOptions>
        {
            ["search"] = new()
            {
                Url = "https://m05.example.test/mcp",
                ApplicationName = "typed-application",
                ApplicationVersion = "7.2.3",
                StartupTimeout = TimeSpan.FromSeconds(7)
            }
        };
        var configuration = McpServerConfiguration.FromServers(declarations, continueOnError: false);
        declarations.Clear();
        using var wire = new Wire();
        await using var factory = new Factory(wire);
        var server = await ResolveAsync(configuration);
        await using var client = await ConnectAsync(server, factory, default);

        Assert.Equal("typed-application", server.Options.ApplicationName);
        Assert.Equal("7.2.3", server.Options.ApplicationVersion);
        Assert.Equal(TimeSpan.FromSeconds(7), server.Options.StartupTimeout);
        AssertIdentity(wire.Initialize, "typed-application", "7.2.3");
        Assert.Equal(TimeSpan.FromSeconds(7), factory.ObservedTimeout);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task JsonIdentityAndMillisecondBudgetReachTheSameOfficialOptionsAndWire()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"search":{"url":"https://m05.example.test/mcp","applicationName":"json-application","applicationVersion":"4.5.6","startupTimeoutMs":4321}}
            """, continueOnError: false);
        using var wire = new Wire();
        await using var factory = new Factory(wire);
        var server = await ResolveAsync(configuration);
        await using var client = await ConnectAsync(server, factory, default);

        AssertIdentity(wire.Initialize, "json-application", "4.5.6");
        Assert.Equal(TimeSpan.FromMilliseconds(4321), server.Options.StartupTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(4321), factory.ObservedTimeout);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task OmittedFieldsRetainServerIdentityAndTheOfficialNativeSixtySecondDefault()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"search":{"url":"https://m05.example.test/mcp"}}
            """, continueOnError: false);
        using var wire = new Wire();
        await using var factory = new Factory(wire);
        var server = await ResolveAsync(configuration);
        await using var client = await ConnectAsync(server, factory, default);

        Assert.Null(server.Options.ApplicationName);
        Assert.Null(server.Options.ApplicationVersion);
        Assert.Null(server.Options.StartupTimeout);
        AssertIdentity(wire.Initialize, "search", "1.0.0");
        Assert.Equal(TimeSpan.FromSeconds(60), new McpClientOptions().InitializationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), factory.ObservedTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveStartupBudgetIsRejectedBeforeTheConnector(int milliseconds)
    {
        var configuration = McpServerConfiguration.Parse(
            "{\"search\":{\"url\":\"https://m05.example.test/mcp\",\"startupTimeoutMs\":" + milliseconds + "}}", continueOnError: false);
        using var wire = new Wire();
        await using var factory = new Factory(wire);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            var server = await ResolveAsync(configuration);
            await using var client = await ConnectAsync(server, factory, default);
        });

        Assert.Equal(0, factory.Calls);
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("initialize")]
    public async Task ExplicitStartupDeadlineCoversBothPhasesAndWaitsForOwnedCleanup(string phase)
    {
        var configuration = McpServerConfiguration.Parse("""
            {"search":{"url":"https://m05.example.test/mcp","startupTimeoutMs":2000}}
            """, continueOnError: false);
        using var caller = new CancellationTokenSource();
        using var wire = new Wire { HoldInitialize = phase == "initialize" };
        await using var factory = new Factory(wire) { HoldTransport = phase == "transport", HoldCleanup = true };
        var server = await ResolveAsync(configuration);
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await (phase == "transport" ? factory.TransportEntered.Task : wire.InitializeEntered.Task).WaitAsync(Watchdog);
            await factory.CancellationObserved.Task.WaitAsync(Watchdog);
            await factory.CleanupEntered.Task.WaitAsync(Watchdog);
            Assert.False(caller.IsCancellationRequested);
            Assert.False(connecting.IsCompleted);
            factory.CleanupRelease.TrySetResult(true);
            observedFailure = await Assert.ThrowsAsync<TimeoutException>(() => connecting).WaitAsync(Watchdog);
            Assert.Equal(1, factory.Disposals);
#if NETFRAMEWORK
            Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            if (phase == "transport") Assert.Empty(wire.Requests);
            else AssertIdentity(wire.Initialize, "search", "1.0.0");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Release every fixture gate before unconditionally joining the original task.
            factory.CleanupRelease.TrySetResult(true);
            try { caller.Cancel(); }
            catch (Exception cleanup)
            {
                if (primary is null) { primary = cleanup; throw; }
                if (!ReferenceEquals(primary, cleanup)) primary.Data["McpIdentityStartup.CancelFailure"] = cleanup;
            }
            finally { await ObserveJoinedAsync(connecting, primary, observedFailure); }
        }
    }

    [Fact]
    public async Task RequestedCallerCancellationRetainsItsTokenAndStillJoinsOwnedCleanup()
    {
        var configuration = McpServerConfiguration.Parse("""
            {"search":{"url":"https://m05.example.test/mcp","startupTimeoutMs":30000}}
            """, continueOnError: false);
        using var caller = new CancellationTokenSource();
        using var wire = new Wire();
        await using var factory = new Factory(wire) { HoldTransport = true, HoldCleanup = true };
        var server = await ResolveAsync(configuration);
        var connecting = ConnectAsync(server, factory, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await factory.TransportEntered.Task.WaitAsync(Watchdog);
            caller.Cancel();
            await factory.CancellationObserved.Task.WaitAsync(Watchdog);
            await factory.CleanupEntered.Task.WaitAsync(Watchdog);
            Assert.False(connecting.IsCompleted);
            factory.CleanupRelease.TrySetResult(true);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting).WaitAsync(Watchdog);
            observedFailure = error;
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(1, factory.Disposals);
#if NETFRAMEWORK
            Assert.True(factory.CleanupCompleted.Task.Status == TaskStatus.RanToCompletion);
#else
            Assert.True(factory.CleanupCompleted.Task.IsCompletedSuccessfully);
#endif
            Assert.Empty(wire.Requests);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            factory.CleanupRelease.TrySetResult(true);
            try { caller.Cancel(); }
            catch (Exception cleanup)
            {
                if (primary is null) { primary = cleanup; throw; }
                if (!ReferenceEquals(primary, cleanup)) primary.Data["McpIdentityStartup.CancelFailure"] = cleanup;
            }
            finally { await ObserveJoinedAsync(connecting, primary, observedFailure); }
        }
    }

    [Fact]
    public async Task DirectCoreOptionsAndInitializeMetadataStayAuthoritativeAndHttpClientStaysBorrowed()
    {
        using var wire = new Wire();
        using var http = new HttpClient(wire, disposeHandler: false);
        var metadata = JsonNode.Parse("""{"proof":{"nested":7}}""")!.AsObject();
        var info = new Implementation { Name = "core-owner", Version = "9.8.7" };
        var options = new McpClientOptions
        {
            ClientInfo = info,
            InitializeMeta = metadata,
            InitializationTimeout = TimeSpan.FromSeconds(13),
            ProtocolVersion = "2025-11-25"
        };
        var transport = TransportOptions();
        await using (var client = await Client.CreateHttpAsync(http, transport, options))
        {
            AssertIdentity(wire.Initialize, "core-owner", "9.8.7");
            Assert.Equal(7, wire.Initialize.GetProperty("params").GetProperty("_meta").GetProperty("proof").GetProperty("nested").GetInt32());
        }

        Assert.False(wire.Disposed);
        Assert.Same(info, options.ClientInfo);
        Assert.Same(metadata, options.InitializeMeta);
        Assert.Equal(TimeSpan.FromSeconds(13), options.InitializationTimeout);
        using var response = await http.GetAsync("https://m05.example.test/borrowed-control");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<ResolvedMcpServer> ResolveAsync(McpServerConfiguration configuration)
        => Assert.Single(await configuration.ResolveAsync(new LocalFileSandbox(), default));

    private static Task<Client> ConnectAsync(ResolvedMcpServer server, Factory factory, CancellationToken token)
        => McpConnections.ConnectServerAsync(server, token, factory.ConnectAsync);

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

    private static async Task ObserveJoinedAsync(Task<Client> task, Exception? primary, Exception? observedFailure)
    {
        Client? late = null;
        try { late = await task; }
        catch (Exception cleanup) when (primary is not null || ReferenceEquals(cleanup, observedFailure))
        {
            if (primary is not null && !ReferenceEquals(primary, cleanup) && !ReferenceEquals(cleanup, observedFailure))
                primary.Data["McpIdentityStartup.JoinFailure"] = cleanup;
        }
        if (late is not null)
        {
            try { await late.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null)
            {
                if (!ReferenceEquals(primary, cleanup)) primary.Data["McpIdentityStartup.LateClientCleanupFailure"] = cleanup;
            }
            // Both callers require failure: a watchdog cannot replace the task's actual outcome.
            if (primary is null) throw new InvalidOperationException("The owned connection succeeded after a fixture failure assertion.");
        }
    }

    private sealed class Factory : IClientTransport, IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClientTransport _transport;
        private int _disposed;

        internal Factory(Wire wire)
        {
            _http = new HttpClient(wire, disposeHandler: false);
            _transport = new HttpClientTransport(TransportOptions(), _http, ownsHttpClient: false);
        }

        internal bool HoldTransport { get; init; }
        internal bool HoldCleanup { get; init; }
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        internal TimeSpan? ObservedTimeout { get; private set; }
        internal TaskCompletionSource<bool> TransportEntered { get; } = Signal();
        internal TaskCompletionSource<bool> CancellationObserved { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupEntered { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupRelease { get; } = Signal();
        internal TaskCompletionSource<bool> CleanupCompleted { get; } = Signal();
        public string Name => "m05-owned-fake-transport";

        internal async Task<Client> ConnectAsync(McpClientOptions options, CancellationToken token)
        {
            Calls++;
            ObservedTimeout = options.InitializationTimeout;
            // Pin only the fixture protocol so the real official serializer emits initialize.
            // The Harness-produced identity and timeout enter the same real client unchanged.
            var selected = new McpClientOptions
            {
                ClientInfo = options.ClientInfo,
                InitializationTimeout = options.InitializationTimeout,
                InitializeMeta = options.InitializeMeta,
                ProtocolVersion = "2025-11-25"
            };
            using var registration = token.Register(() => CancellationObserved.TrySetResult(true));
            return await Client.CreateAsync(new McpClientSettings { OwnsTransport = true }, this, selected, token);
        }

        public async Task<ModelContextProtocol.Protocol.ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            TransportEntered.TrySetResult(true);
            if (HoldTransport) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
        internal bool HoldInitialize { get; init; }
        internal bool Disposed { get; private set; }
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
            if (HoldInitialize) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "m05-fixture", version = "1.0" } }
                }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Mcp-Session-Id", "m05-session");
            return response;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
