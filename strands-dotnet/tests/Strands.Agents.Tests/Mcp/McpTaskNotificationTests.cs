using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTaskNotificationTests
{
    [Fact]
    public async Task AValidatedNotificationSettlesThePublicToolCallWhileItsGetIsStillHeld()
    {
        await using var server = new TaskServer();
        await using var client = await ConnectAsync(server);
        await client.ListToolsAsync();
        using var cancellation = new CancellationTokenSource();
        var delivered = Signal<JsonRpcNotification>();
        var observation = client.Protocol.RegisterNotificationHandler("notifications/tasks", (notification, _) =>
        {
            delivered.TrySetResult(notification);
            return default;
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { query = "notification" }), cancellationToken: cancellation.Token).AsTask();
        Exception? primary = null;
        try
        {
            await Within(server.GetEntered.Task);
            var notification = server.Notify(TaskServer.Completed(server.TaskId, "notification result", 7));
            var received = await Within(delivered.Task);
            Assert.Same(notification, received);
            Assert.Equal(server.TaskId, received.Params!["taskId"]!.GetValue<string>());
            Assert.False(received.Params!.AsObject().ContainsKey("resultType"));

            using var watchdog = new CancellationTokenSource();
            var winner = await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(8), watchdog.Token));
            watchdog.Cancel();
            Assert.True(ReferenceEquals(invocation, winner), "The validated notification must settle the public call before the held tasks/get response is released.");
            var result = await invocation;

            Assert.False(server.GetReleased);
            Assert.Equal("notification result", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(7, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
            Assert.Equal(1, server.Submissions);
            Assert.Equal(1, server.Gets);
            Assert.Equal(0, server.Cancels);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(server, cancellation, invocation, observation, primary); }

        await client.DisposeAsync();
        Assert.Equal(1, server.SessionDisposals);
        Assert.Equal(0, server.FactoryDisposals);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("malformed")]
    public async Task ForeignOrMalformedNotificationsLeaveTheControlledPollingResultAuthoritative(string shape)
    {
        await using var server = new TaskServer();
        await using var client = await ConnectAsync(server);
        await client.ListToolsAsync();
        using var cancellation = new CancellationTokenSource();
        var delivered = Signal<JsonRpcNotification>();
        var observation = client.Protocol.RegisterNotificationHandler("notifications/tasks", (notification, _) =>
        {
            delivered.TrySetResult(notification);
            return default;
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { query = "fallback" }), cancellationToken: cancellation.Token).AsTask();
        Exception? primary = null;
        try
        {
            await Within(server.GetEntered.Task);
            var parameters = TaskServer.Completed(shape == "foreign" ? "other task/雪" : server.TaskId, "unaccepted notification", 999);
            if (shape == "malformed") parameters.Remove("result");
            var notification = server.Notify(parameters);
            var received = await Within(delivered.Task);
            Assert.Same(notification, received);
            Assert.Equal(parameters["taskId"]!.GetValue<string>(), received.Params!["taskId"]!.GetValue<string>());
            Assert.False(invocation.IsCompleted);

            server.ReleaseGet();
            var result = await Within(invocation);

            Assert.Equal("poll result", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(42, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
            Assert.Equal(1, server.Submissions);
            Assert.Equal(1, server.Gets);
            Assert.Equal(0, server.Cancels);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await FinishAsync(server, cancellation, invocation, observation, primary); }

        await client.DisposeAsync();
        Assert.Equal(1, server.SessionDisposals);
        Assert.Equal(0, server.FactoryDisposals);
    }

    [Fact]
    public void ObservationModeDefaultsToOmittedPollingAndRoundTripsAnExplicitSelection()
    {
        var defaults = new McpTasksConfig();
        Assert.Equal(McpTaskObservationMode.Polling, defaults.ObservationMode);
        using var legacyShape = JsonDocument.Parse(JsonSerializer.Serialize(defaults));
        Assert.Equal(new[] { "ProtocolProfile", "PollTimeout", "RequestTimeout", "PollInterval", "LegacyTimeToLive" },
            legacyShape.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(legacyShape.RootElement.TryGetProperty("ObservationMode", out _));
        Assert.Equal(McpTasksProtocolProfile.Negotiated, defaults.ProtocolProfile);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.PollTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), defaults.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.PollInterval);
        Assert.Null(defaults.LegacyTimeToLive);

        var selected = defaults with { ObservationMode = McpTaskObservationMode.NotificationsAndPolling };
        var json = JsonSerializer.Serialize(selected);
        using var selectedShape = JsonDocument.Parse(json);
        Assert.Equal(1, selectedShape.RootElement.GetProperty("ObservationMode").GetInt32());
        Assert.Equal(McpTaskObservationMode.NotificationsAndPolling, JsonSerializer.Deserialize<McpTasksConfig>(json)!.ObservationMode);
        Assert.Equal(McpTaskObservationMode.Polling, JsonSerializer.Deserialize<McpTasksConfig>(legacyShape.RootElement.GetRawText())!.ObservationMode);
    }

    private static Task<Client> ConnectAsync(TaskServer server) => Client.CreateAsync(
        new McpClientSettings
        {
            TasksConfig = new()
            {
                ProtocolProfile = McpTasksProtocolProfile.Finalized20260728,
                ObservationMode = McpTaskObservationMode.NotificationsAndPolling,
                PollInterval = TimeSpan.FromMilliseconds(10),
                RequestTimeout = TimeSpan.FromSeconds(30),
                PollTimeout = TimeSpan.FromSeconds(45)
            }
        }, server, new McpClientOptions { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T> Within<T>(Task<T> operation)
    {
        using var watchdog = new CancellationTokenSource();
        var winner = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(8), watchdog.Token));
        watchdog.Cancel();
        Assert.Same(operation, winner);
        return await operation;
    }

    private static async Task FinishAsync(TaskServer server, CancellationTokenSource cancellation, Task invocation,
        IAsyncDisposable observation, Exception? primary)
    {
        var failures = new List<Exception>();
        try { cancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
        server.ReleaseGet();
        try { await server.JoinGetResponsesAsync(); } catch (Exception error) { failures.Add(error); }
        try { await invocation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!ReferenceEquals(error, primary)) failures.Add(error); }
        try { await observation.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 0) return;
        if (primary is not null) { primary.Data["Strands.TestCleanupErrors"] = failures.ToArray(); return; }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("MCP notification fixture cleanup failed.", failures);
    }

    private sealed class TaskServer : IClientTransport, IAsyncDisposable
    {
        private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly ConcurrentQueue<Task> _getResponses = new();
        private readonly TaskCompletionSource<bool> _releaseGet = Signal<bool>();
        private int _connections;
        private int _submissions;
        private int _gets;
        private int _cancels;
        private int _sessionDisposals;
        private int _factoryDisposals;
        internal string TaskId { get; } = " task/雪 ";
        public string Name => "m23-in-memory-task-server";
        internal TaskCompletionSource<bool> GetEntered { get; } = Signal<bool>();
        internal bool GetReleased => _releaseGet.Task.IsCompleted;
        internal int Submissions => Volatile.Read(ref _submissions);
        internal int Gets => Volatile.Read(ref _gets);
        internal int Cancels => Volatile.Read(ref _cancels);
        internal int SessionDisposals => Volatile.Read(ref _sessionDisposals);
        internal int FactoryDisposals => Volatile.Read(ref _factoryDisposals);

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _connections) != 1) throw new InvalidOperationException("The fixture permits one protocol connection.");
            return Task.FromResult<ITransport>(new Session(this));
        }

        internal static JsonObject Completed(string taskId, string text, int answer) => JsonSerializer.SerializeToNode(new
        {
            taskId,
            status = "completed",
            createdAt = "2026-10-24T10:00:00Z",
            lastUpdatedAt = "2026-10-24T10:00:01Z",
            ttlMs = (int?)null,
            result = new { resultType = "complete", content = new[] { new { type = "text", text } }, structuredContent = new { answer } }
        })!.AsObject();

        internal JsonRpcNotification Notify(JsonObject parameters)
        {
            var notification = new JsonRpcNotification { Method = "notifications/tasks", Params = parameters.DeepClone() };
            if (!_incoming.Writer.TryWrite(notification)) throw new InvalidOperationException("The protocol reader has already closed.");
            return notification;
        }

        internal void ReleaseGet() => _releaseGet.TrySetResult(true);
        internal Task JoinGetResponsesAsync() => Task.WhenAll(_getResponses.ToArray());

        private Task SendAsync(JsonRpcMessage message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (message is not JsonRpcRequest request) return Task.CompletedTask;
            switch (request.Method)
            {
                case "server/discover":
                    Reply(request.Id, new
                    {
                        supportedVersions = new[] { "2026-07-28" },
                        _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = Name, version = "1" } },
                        capabilities = Capabilities(),
                        ttlMs = 0,
                        cacheScope = "private"
                    });
                    break;
                case "initialize":
                    Reply(request.Id, new { protocolVersion = "2026-07-28", serverInfo = new { name = Name, version = "1" }, capabilities = Capabilities() });
                    break;
                case "tools/list":
                    Reply(request.Id, new
                    {
                        tools = new[] { new { name = "lookup", inputSchema = new { type = "object" }, outputSchema = new { type = "object", properties = new { answer = new { type = "integer" } }, required = new[] { "answer" } } } },
                        ttlMs = (int?)null,
                        cacheScope = "private"
                    });
                    break;
                case "tools/call":
                    Interlocked.Increment(ref _submissions);
                    Reply(request.Id, new { resultType = "task", taskId = TaskId, status = "working", createdAt = "2026-10-24T10:00:00Z", lastUpdatedAt = "2026-10-24T10:00:00Z", ttlMs = (int?)null, pollIntervalMs = 10 });
                    break;
                case "tasks/get":
                    if (request.Params?["taskId"]?.GetValue<string>() != TaskId) throw new InvalidDataException("The fixture task ID must be preserved.");
                    Interlocked.Increment(ref _gets);
                    _getResponses.Enqueue(ReplyWhenReleasedAsync(request.Id));
                    GetEntered.TrySetResult(true);
                    break;
                case "tasks/cancel":
                    Interlocked.Increment(ref _cancels);
                    Reply(request.Id, new { resultType = "complete" });
                    break;
                default:
                    throw new InvalidOperationException("Unexpected fixture request: " + request.Method);
            }
            return Task.CompletedTask;
        }

        private static object Capabilities() => new { tools = new { }, extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new { } } };

        private void Reply(RequestId id, object result)
        {
            if (!_incoming.Writer.TryWrite(new JsonRpcResponse { Id = id, Result = JsonSerializer.SerializeToNode(result) }))
                throw new InvalidOperationException("The fixture response was written after protocol disposal.");
        }

        private async Task ReplyWhenReleasedAsync(RequestId id)
        {
            await _releaseGet.Task;
            var result = Completed(TaskId, "poll result", 42);
            result["resultType"] = "complete";
            Reply(id, result);
        }

        private async ValueTask CloseSessionAsync()
        {
            if (Interlocked.Exchange(ref _sessionDisposals, 1) != 0) return;
            _incoming.Writer.TryComplete();
            await JoinGetResponsesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _factoryDisposals, 1) != 0) return;
            ReleaseGet();
            await JoinGetResponsesAsync();
            _incoming.Writer.TryComplete();
        }

        private sealed class Session(TaskServer server) : ITransport
        {
            public string? SessionId => "m23-session";
            public ChannelReader<JsonRpcMessage> MessageReader => server._incoming.Reader;
            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => server.SendAsync(message, cancellationToken);
            public ValueTask DisposeAsync() => server.CloseSessionAsync();
        }
    }
}
