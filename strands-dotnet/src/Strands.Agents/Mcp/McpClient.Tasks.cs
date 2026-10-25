using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProtocolClient = ModelContextProtocol.Client.McpClient;

namespace Strands.Agents.Mcp;

public sealed partial class McpClient
{
    private readonly McpTasksConfig? _tasksConfig;
    private readonly CancellationTokenSource _taskShutdown = new();
    private const string TasksExtension = "io.modelcontextprotocol/tasks";

    /// <summary>Connects an official transport with explicit native features. The session is owned; OwnsTransport additionally transfers the original transport factory.</summary>
    public static async Task<McpClient> CreateAsync(McpClientSettings settings, IClientTransport transport,
        McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(settings, nameof(settings));
        Guard.NotNull(transport, nameof(transport));
        var ownedTransport = settings.OwnsTransport ? transport : null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = ValidateTasksConfig(settings.TasksConfig);
            var metadata = SnapshotDefaultToolCallMetadata(settings.DefaultToolCallMetadata);
            LegacyTaskCapabilities? legacy = null;
            if (tasks is { ProtocolProfile: McpTasksProtocolProfile.Legacy20251125 or McpTasksProtocolProfile.Negotiated })
            {
                legacy = new();
                transport = new LegacyTaskTransport(transport, legacy);
            }
            var toolChanges = new ToolChangeWireState();
            var protocol = await ProtocolClient.CreateAsync(new ToolChangeTransport(transport, toolChanges), tasks is null ? options : WithTaskCapability(options, tasks.ProtocolProfile != McpTasksProtocolProfile.Legacy20251125), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(protocol, toolChanges, tasks, legacy, ownedTransport, metadata);
        }
        catch (Exception error)
        {
            Exception? cleanupFailure = null;
            if (ownedTransport is not null)
                try { await DisposeTransportAsync(ownedTransport).ConfigureAwait(false); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }
            if (cancellationToken.IsCancellationRequested && (error is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken || cleanupFailure is not null && ownedTransport is OwnedStdioClientTransport))
            {
                var cancellation = new ConnectionCanceledException(error, cancellationToken);
                foreach (System.Collections.DictionaryEntry entry in error.Data) cancellation.Data[entry.Key] = entry.Value;
                if (cleanupFailure is not null) AttachDisposalError(cancellation, cleanupFailure);
                throw cancellation;
            }
            if (cleanupFailure is not null) AttachDisposalError(error, cleanupFailure);
            throw;
        }
    }

    private static JsonElement? SnapshotDefaultToolCallMetadata(JsonElement? metadata)
    {
        if (metadata is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Default tool-call metadata must be a JSON object.", nameof(McpClientSettings.DefaultToolCallMetadata));
        return value.Clone();
    }

    private sealed class ConnectionCanceledException(Exception cause, CancellationToken token)
        : OperationCanceledException("MCP connection was cancelled.", cause, token)
    {
        public override System.Collections.IDictionary Data { get; } = new System.Collections.Hashtable();
    }

    /// <summary>Connects HTTP with explicit native task features. The supplied HTTP client remains borrowed.</summary>
    public static Task<McpClient> CreateHttpAsync(McpClientSettings settings, HttpClient httpClient, HttpClientTransportOptions transportOptions,
        McpClientOptions? clientOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(settings, nameof(settings));
        Guard.NotNull(httpClient, nameof(httpClient));
        Guard.NotNull(transportOptions, nameof(transportOptions));
        return CreateHttpCoreAsync(settings, httpClient, transportOptions, clientOptions, cancellationToken);
    }

    /// <summary>Connects a stdio process with explicit native task features. The protocol connection owns process cleanup.</summary>
    public static Task<McpClient> CreateStdioAsync(McpClientSettings settings, StdioClientTransportOptions transportOptions,
        McpClientOptions? clientOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(settings, nameof(settings));
        Guard.NotNull(transportOptions, nameof(transportOptions));
        return CreateAsync(settings with { OwnsTransport = true }, new OwnedStdioClientTransport(transportOptions), clientOptions, cancellationToken);
    }

    /// <summary>Calls a tool once without polling, retaining either its direct result or its server-created task handle.</summary>
    public async ValueTask<McpToolSubmission> SubmitToolAsync(string serverName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        RequireTaskLifecycle();
        ValidateTaskToolInput(serverName, arguments);
        using var operation = StartTaskOperation(_tasksConfig!.PollTimeout, cancellationToken);
        try { return await SubmitToolCoreAsync(serverName, arguments, null, null, operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("MCP task submission was cancelled.", error, cancellationToken); }
        catch (OperationCanceledException error) when (!_taskShutdown.IsCancellationRequested && operation.IsCancellationRequested) { throw new TimeoutException("MCP task submission exceeded its overall deadline.", error); }
    }

    /// <summary>Retrieves a validated complete task state and verifies its task identifier.</summary>
    public async ValueTask<McpTask> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        RequireTaskLifecycle();
        ValidateTaskId(taskId);
        var result = await RequestTaskLifecycleAsync("tasks/get", new JsonObject { ["taskId"] = taskId }, taskId, cancellationToken).ConfigureAwait(false);
        return ParseTaskResponse(result, taskId);
    }

    /// <summary>Sends a detached input response batch to the selected task and validates its empty acknowledgement.</summary>
    public async ValueTask<JsonElement> UpdateTaskAsync(string taskId, IReadOnlyDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        RequireTaskLifecycle();
        ValidateTaskId(taskId);
        Guard.NotNull(inputResponses, nameof(inputResponses));
        var detached = McpTaskInputs.ValidateResponses(inputResponses);
        var result = await RequestTaskLifecycleAsync("tasks/update", new JsonObject { ["taskId"] = taskId, ["inputResponses"] = JsonNode.Parse(detached.GetRawText()) }, taskId, cancellationToken).ConfigureAwait(false);
        return ValidateAcknowledgement(result);
    }

    /// <summary>Requests cooperative cancellation. A valid acknowledgement does not guarantee remote termination.</summary>
    public async ValueTask<JsonElement> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        RequireTaskLifecycle();
        ValidateTaskId(taskId);
        var result = await RequestTaskLifecycleAsync("tasks/cancel", new JsonObject { ["taskId"] = taskId }, taskId, cancellationToken).ConfigureAwait(false);
        return ValidateAcknowledgement(result);
    }

    private async ValueTask<McpToolSubmission> SubmitToolCoreAsync(string serverName, JsonElement arguments,
        IProgress<ProgressNotificationValue>? progress, RequestOptions? options, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["name"] = serverName, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) };
        if (options?.GetMetaForRequest() is { } metadata) parameters["_meta"] = metadata.DeepClone();
        var result = await SendTaskRequestAsync("tools/call", parameters, null, progress, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("resultType", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "task")
            return new(null, McpTask.Parse(result, "task"));
        if (type.ValueKind != JsonValueKind.String || type.GetString() != "complete") throw new InvalidDataException("MCP tools/call returned a malformed SEP-2663 resultType.");
        McpTask.ValidateToolResult(result);
        return new(result, null);
    }

    private async ValueTask<McpTask> GetTaskCoreAsync(string taskId, CancellationToken cancellationToken, IProgress<ProgressNotificationValue>? progress = null, bool joinPending = false)
    {
        var response = await SendTaskRequestAsync("tasks/get", new JsonObject { ["taskId"] = taskId }, taskId, progress, cancellationToken, joinPending: joinPending).ConfigureAwait(false);
        return ParseTaskResponse(response, taskId);
    }

    private static McpTask ParseTaskResponse(JsonElement response, string taskId)
    {
        var task = McpTask.Parse(response);
        if (task.TaskId != taskId) throw new InvalidDataException("MCP tasks/get returned a different taskId.");
        return task;
    }

    private async ValueTask<JsonElement> RequestTaskLifecycleAsync(string method, JsonObject parameters, string taskId, CancellationToken cancellationToken)
    {
        using var operation = StartTaskOperation(_tasksConfig!.RequestTimeout, cancellationToken);
        try { return await SendTaskRequestAsync(method, parameters, taskId, null, operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("MCP " + method + " was cancelled.", error, cancellationToken); }
        catch (OperationCanceledException error) when (!_taskShutdown.IsCancellationRequested && operation.IsCancellationRequested)
        { throw new TimeoutException("MCP " + method + " exceeded its request deadline.", error); }
    }

    private async ValueTask<CallToolResult> CompleteTaskToolAsync(string serverName, JsonElement arguments,
        IProgress<ProgressNotificationValue>? progress, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var operation = StartTaskOperation(_tasksConfig!.PollTimeout, cancellationToken);
        McpTask? task = null;
        var completed = false;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        TaskNotificationState? notifications = null;
        IAsyncDisposable? registration = null;
        Exception? primary = null;
        long sequence = 0;
        try
        {
            var submission = await SubmitToolCoreAsync(serverName, arguments, progress, options, operation.Token).ConfigureAwait(false);
            if (submission.Result is { } direct) return direct;
            task = submission.Task!;
            if (_tasksConfig.ObservationMode == McpTaskObservationMode.NotificationsAndPolling)
            {
                var observer = new TaskNotificationState(task.TaskId);
                notifications = observer;
                registration = _protocol.RegisterNotificationHandler("notifications/tasks", (notification, _) =>
                {
                    try { observer.Notify(McpTask.ParseNotification(JsonSerializer.SerializeToElement(notification.Params, JsonOptions))); }
                    catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or NotSupportedException)
                    { TryWarn("MCP task notification was ignored: " + error.GetType().Name, error); }
                    return default;
                });
            }
            while (true)
            {
                if (notifications is null)
                {
                    await Task.Delay(task.PollInterval is { } interval ? MaxPollInterval(interval) : _tasksConfig.PollInterval, operation.Token).ConfigureAwait(false);
                    task = await GetTaskCoreAsync(task.TaskId, operation.Token, progress).ConfigureAwait(false);
                }
                else (task, sequence) = await ObserveTaskAsync(task, notifications, sequence, progress, operation.Token).ConfigureAwait(false);
                switch (task.Status)
                {
                    case McpTaskStatus.Completed: completed = true; return task.Result!;
                    case McpTaskStatus.Failed: completed = true; throw new McpTaskFailedException(task);
                    case McpTaskStatus.Cancelled: completed = true; throw new McpTaskCancelledException(task.StatusMessage);
                    case McpTaskStatus.InputRequired:
                        if (notifications?.Terminal is not null) continue;
                        var requests = task.InputRequests!.Where(pair => !answered.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        if (requests.Count == 0) break;
                        var pending = _protocol.ResolveInputRequestsAsync(requests, operation.Token).AsTask();
                        var responses = await WaitForTaskOperationAsync(pending, operation.Token).ConfigureAwait(false);
                        if (notifications?.Terminal is not null) continue;
                        _ = McpTaskInputs.ValidateResponses(new Dictionary<string, InputResponse>(responses, StringComparer.Ordinal), requests);
                        await UpdateTaskAsync(task.TaskId, new Dictionary<string, InputResponse>(responses, StringComparer.Ordinal), operation.Token).ConfigureAwait(false);
                        foreach (var key in responses.Keys) answered.Add(key);
                        break;
                }
            }
        }
        catch (Exception error)
        {
            primary = error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? new OperationCanceledException("MCP tool task was cancelled.", error, cancellationToken)
                : error is OperationCanceledException && !_taskShutdown.IsCancellationRequested && operation.IsCancellationRequested
                ? new TimeoutException("MCP tool task exceeded its overall deadline.", error) : error;
            if (ReferenceEquals(primary, error)) throw;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (registration is not null)
                try { await registration.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { if (primary is not null) AttachDisposalError(primary, error); else cleanupFailure = error; }
            if (task is not null && !completed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await CancelTaskAsync(task.TaskId, cleanup.Token).ConfigureAwait(false); }
                catch (Exception error) { TryWarn("MCP task cancellation failed: " + error.GetType().Name, error); }
            }
            if (cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private async ValueTask<(McpTask Task, long Sequence)> ObserveTaskAsync(McpTask task, TaskNotificationState notifications,
        long sequence, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observation = notifications.ReadAfter(sequence);
        if (observation.Task is { } cached) return (notifications.Accept(cached), observation.Sequence);
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? delay = null;
        Task<McpTask>? poll = null;
        Exception? primary = null;
        try
        {
            delay = Task.Delay(task.PollInterval is { } interval ? MaxPollInterval(interval) : _tasksConfig!.PollInterval, turn.Token);
            await Task.WhenAny(delay, observation.Changed).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            observation = notifications.ReadAfter(sequence);
            if (observation.Task is { } notified) return (notifications.Accept(notified), observation.Sequence);
            await delay.ConfigureAwait(false);
            poll = GetTaskCoreAsync(task.TaskId, turn.Token, progress, joinPending: true).AsTask();
            await Task.WhenAny(poll, observation.Changed).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            observation = notifications.ReadAfter(sequence);
            if (observation.Task is { } latest) return (notifications.Accept(latest), observation.Sequence);
            return (notifications.Accept(await poll.ConfigureAwait(false)), sequence);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            try { turn.Cancel(); } catch (Exception error) { failures.Add(error); }
            if (delay is not null)
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) when (turn.IsCancellationRequested) { }
                catch (Exception error) { if (!ReferenceEquals(error, primary)) failures.Add(error); }
            if (poll is not null)
                try { await poll.ConfigureAwait(false); }
                catch (OperationCanceledException) when (turn.IsCancellationRequested) { }
                catch (Exception error) { if (!ReferenceEquals(error, primary)) failures.Add(error); }
            if (primary is not null)
                foreach (var error in failures) AttachDisposalError(primary, error);
            else if (failures.Count != 0)
            {
                foreach (var error in failures.Skip(1)) AttachDisposalError(failures[0], error);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
        }
    }

    private sealed class TaskNotificationState(string taskId)
    {
        private readonly object _gate = new();
        private McpTask? _notification;
        private McpTask? _terminal;
        private long _sequence;
        private TaskCompletionSource<bool> _changed = NewSignal();
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal McpTask? Terminal { get { lock (_gate) return _terminal; } }
        internal void Notify(McpTask task)
        {
            if (!StringComparer.Ordinal.Equals(task.TaskId, taskId)) return;
            TaskCompletionSource<bool> changed;
            lock (_gate)
            {
                if (_terminal is not null) return;
                if (IsTerminal(task)) _terminal = task;
                _notification = task;
                _sequence++;
                changed = _changed;
                _changed = NewSignal();
            }
            changed.TrySetResult(true);
        }
        internal McpTask Accept(McpTask task)
        {
            lock (_gate)
            {
                if (_terminal is not null) return _terminal;
                if (IsTerminal(task)) _terminal = task;
                return task;
            }
        }
        internal (McpTask? Task, long Sequence, Task Changed) ReadAfter(long sequence)
        { lock (_gate) return (_sequence > sequence ? _terminal ?? _notification : null, _sequence, _changed.Task); }
        private static bool IsTerminal(McpTask task) => task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;
    }

    private async ValueTask<JsonElement> SendTaskRequestAsync(string method, JsonObject parameters, string? taskId,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken, TimeSpan? requestTimeout = null, bool joinPending = false)
    {
        ThrowIfDisposed();
        var timeout = requestTimeout ?? _tasksConfig!.RequestTimeout;
        using var request = StartTaskOperation(timeout, cancellationToken);
        var id = "strands-task:" + Guid.NewGuid().ToString("N");
        var metadata = parameters["_meta"] as JsonObject ?? new JsonObject();
        parameters["_meta"] = metadata;
        metadata["progressToken"] = id;
        await using var registration = _protocol.RegisterNotificationHandler("notifications/progress", (notification, _) =>
        {
            if (notification.Params is JsonObject values && values["progressToken"] is JsonValue token && token.TryGetValue<string>(out var value) && value == id)
            {
                try { request.CancelAfter(timeout); }
                catch (ObjectDisposedException) { }
                if (progress is not null)
                    try
                    {
                        var parsed = values.Deserialize<ProgressNotificationParams>(JsonOptions);
                        if (parsed is not null) progress.Report(parsed.Progress);
                    }
                    catch (Exception error) { TryWarn("MCP progress observer failed: " + error.GetType().Name, error); }
            }
            return default;
        }).ConfigureAwait(false);
#pragma warning disable MCPEXP002 // SEP-2663 requires the official extension routing-name seam.
        var pending = _protocol.SendRequestAsync(new JsonRpcRequest
        {
            Id = new RequestId(id),
            Method = method,
            Params = parameters,
            Context = new JsonRpcMessageContext { RoutingName = taskId is null ? null : EncodeRoutingName(taskId) }
        }, request.Token);
#pragma warning restore MCPEXP002
        Exception? primary = null;
        try
        {
            try
            {
                if (joinPending)
                {
                    await TaskCancellation.WaitAsync(pending, request.Token).ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                }
                else await WaitForRequestAsync(pending, request.Token).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement((await pending.ConfigureAwait(false)).Result, JsonOptions);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("MCP " + method + " was cancelled.", error, cancellationToken); }
            catch (OperationCanceledException error) when (!_taskShutdown.IsCancellationRequested && request.IsCancellationRequested)
            { throw new TimeoutException("MCP " + method + " request exceeded its inactivity deadline.", error); }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (joinPending)
                try { await pending.ConfigureAwait(false); }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                catch (Exception error) { if (primary is null) throw; AttachDisposalError(primary, error); }
        }
    }

    private static async Task WaitForRequestAsync(Task<JsonRpcResponse> pending, CancellationToken cancellationToken)
    {
        _ = ObserveRequestAsync(pending);
        await TaskCancellation.WaitAsync(pending, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<T> WaitForTaskOperationAsync<T>(Task<T> pending, CancellationToken cancellationToken)
    {
        _ = ObserveRequestAsync(pending);
        await TaskCancellation.WaitAsync(pending, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await pending.ConfigureAwait(false);
    }

    private static async Task ObserveRequestAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private CancellationTokenSource StartTaskOperation(TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _taskShutdown.Token);
            operation.CancelAfter(timeout);
            return operation;
        }
    }

    private bool SupportsTaskExtension()
    {
        if (_protocol.NegotiatedProtocolVersion != "2026-07-28") return false;
        var capabilities = JsonSerializer.SerializeToElement(_protocol.ServerCapabilities, JsonOptions);
        return capabilities.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object &&
            extensions.TryGetProperty(TasksExtension, out var settings) && settings.ValueKind == JsonValueKind.Object;
    }

    private void RequireTaskLifecycle()
    {
        ThrowIfDisposed();
        if (_tasksConfig is null) throw new InvalidOperationException("MCP task lifecycle operations require TasksConfig.");
        if (_tasksConfig.ProtocolProfile == McpTasksProtocolProfile.Legacy20251125) throw new InvalidOperationException("Explicit MCP task lifecycle operations require finalized SEP-2663 execution, not the legacy-only profile.");
        if (!SupportsTaskExtension()) throw new InvalidOperationException("MCP task lifecycle operations require protocol 2026-07-28 and the advertised " + TasksExtension + " extension.");
    }

    internal static McpTasksConfig? ValidateTasksConfig(McpTasksConfig? config)
    {
        if (config is null) return null;
        if (config.ProtocolProfile is not (McpTasksProtocolProfile.Finalized20260728 or McpTasksProtocolProfile.Legacy20251125 or McpTasksProtocolProfile.Negotiated)) throw new ArgumentOutOfRangeException(nameof(config), "Unsupported MCP task protocol profile.");
        if (config.ObservationMode is not (McpTaskObservationMode.Polling or McpTaskObservationMode.NotificationsAndPolling)) throw new ArgumentOutOfRangeException(nameof(config), "Unsupported MCP task observation mode.");
        foreach (var value in new[] { config.PollTimeout, config.RequestTimeout, config.PollInterval })
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(config), "MCP task durations must be positive and at most Int32.MaxValue milliseconds.");
        if (config.LegacyTimeToLive is { } ttl && (config.ProtocolProfile == McpTasksProtocolProfile.Finalized20260728 || ttl <= TimeSpan.Zero || ttl.Ticks % TimeSpan.TicksPerMillisecond != 0))
            throw new ArgumentOutOfRangeException(nameof(config), "LegacyTimeToLive requires a legacy-capable profile and a positive whole-millisecond duration.");
        return config with { };
    }

    private static McpClientOptions WithTaskCapability(McpClientOptions? options, bool advertise = true)
    {
        options ??= new();
        var capabilities = JsonSerializer.SerializeToNode(options.Capabilities ?? new ClientCapabilities(), JsonOptions)!.AsObject();
        if (advertise)
        {
            var extensions = capabilities["extensions"] as JsonObject;
            if (extensions is null) capabilities["extensions"] = extensions = new JsonObject();
            extensions[TasksExtension] = new JsonObject();
        }
        return new()
        {
            ClientInfo = options.ClientInfo,
            Capabilities = capabilities.Deserialize<ClientCapabilities>(JsonOptions),
            InitializeMeta = options.InitializeMeta?.DeepClone().AsObject(),
            ProtocolVersion = options.ProtocolVersion,
            InitializationTimeout = options.InitializationTimeout,
            DiscoverProbeTimeout = options.DiscoverProbeTimeout,
            Handlers = options.Handlers
        };
    }

    private static JsonElement ValidateAcknowledgement(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("resultType", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "complete")
            throw new InvalidDataException("MCP task operation returned a malformed acknowledgement.");
        if (new[] { "taskId", "status", "statusMessage", "createdAt", "lastUpdatedAt", "ttlMs", "pollIntervalMs", "inputRequests", "result", "error" }.Any(field => value.TryGetProperty(field, out _)))
            throw new InvalidDataException("MCP task acknowledgement cannot contain task state.");
        return value.Clone();
    }

    private static void ValidateTaskId(string taskId)
    { Guard.NotNull(taskId, nameof(taskId)); if (taskId.Length == 0) throw new ArgumentException("MCP taskId cannot be empty.", nameof(taskId)); }
    private static void ValidateTaskToolInput(string name, JsonElement input)
    { Guard.NotNullOrWhiteSpace(name, nameof(name)); if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP tool arguments must be an object.", nameof(input)); }
    private static TimeSpan MaxPollInterval(TimeSpan interval) => TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, Math.Max(10, interval.TotalMilliseconds)));
    private static string EncodeRoutingName(string value) => value.Length > 0 && value == value.Trim() &&
        !(value.StartsWith("=?base64?", StringComparison.Ordinal) && value.EndsWith("?=", StringComparison.Ordinal)) &&
        value.All(character => character == '\t' || character is >= ' ' and <= '~')
        ? value : "=?base64?" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)) + "?=";
    private static void TryWarn(string message, Exception? error = null)
    {
        try
        {
            if (error is null) SdkLogging.Warning("{Message}", message);
            else SdkLogging.Warning(error, "{Message}", message);
        }
        catch (Exception) { }
    }
}

/// <summary>A direct tool result or an immutable server-created task handle. Exactly one is present.</summary>
public sealed class McpToolSubmission
{
    private readonly JsonElement? _result;
    internal McpToolSubmission(JsonElement? result, McpTask? task) { _result = result?.Clone(); Task = task; }
    /// <summary>The direct result, detached anew for each read, or null when the server created a task.</summary>
    public CallToolResult? Result => _result?.Deserialize<CallToolResult>();
    /// <summary>The immutable task handle, or null for a direct result.</summary>
    public McpTask? Task { get; }
}

/// <summary>A server-reported task failure, distinct from local cancellation or a completed isError tool result.</summary>
public sealed class McpTaskFailedException : Exception
{
    internal McpTaskFailedException(McpTask task) : base("MCP task failed: " + task.Error!.Value.GetProperty("message").GetString())
    { Code = task.Error.Value.GetProperty("code").GetInt32(); Details = task.Error.Value.TryGetProperty("data", out var value) ? value.Clone() : null; }
    /// <summary>The server-reported JSON-RPC failure code.</summary>
    public int Code { get; }
    /// <summary>Detached sender-defined details, if provided.</summary>
    public JsonElement? Details { get; }
}

/// <summary>A server-reported cancellation, distinct from cancellation requested by the local caller.</summary>
public sealed class McpTaskCancelledException : Exception
{
    internal McpTaskCancelledException(string? statusMessage) : base(statusMessage is null ? "MCP task was cancelled." : "MCP task was cancelled: " + statusMessage) { }
}
