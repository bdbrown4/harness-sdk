using System.Net;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A framework-neutral A2A JSON-RPC HTTP handler. The embedding host owns authentication and request routing.</summary>
public sealed partial class A2AServer : IAsyncDisposable
{
    private readonly A2AExecutor _executor;
    private readonly A2AServerOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedTask> _tasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _disposeTask;
    private long _sequence;
    private static readonly AsyncLocal<HostScope?> Scope = new();
    /// <summary>Configures a borrowed executor and explicit discovery metadata.</summary>
    public A2AServer(A2AExecutor executor, A2AServerOptions options)
    {
        Guard.NotNull(executor, nameof(executor)); Guard.NotNull(options, nameof(options));
        if (!Enum.IsDefined(typeof(A2ACardFormat), options.CardFormat)) throw new ArgumentOutOfRangeException(nameof(options));
        Guard.NotNull(options.Skills, nameof(options));
        if (options.PushNotifications is { } push)
        {
            Guard.NotNull(push.SendAsync, nameof(options)); Guard.NotNull(push.AuthorizeConfiguration, nameof(options)); Guard.NotNull(push.ConfigStore, nameof(options));
        }
        if (options.Skills.Any(skill => skill is null)) throw new ArgumentException("A2A skills cannot contain null.", nameof(options));
        if ((options.AuthenticatedExtendedCard is null) != (options.AuthorizeExtendedCard is null)) throw new ArgumentException("An authenticated extended card and its explicit authorization callback must be configured together.", nameof(options));
        A2AHttp.ValidateUri(options.Url, nameof(options)); Guard.NotNullOrWhiteSpace(options.Name, nameof(options)); Guard.NotNullOrWhiteSpace(options.Version, nameof(options));
        if (options.Description is null || options.MaxTasks <= 0 || options.MaxRequestBytes <= 0 || options.MaxTaskBytes <= 0 || options.MaxReplayEvents <= 0 || options.MaxReplayBytes <= 0 || options.MaxSubscribers <= 0 || options.MaxCardBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _executor = executor; _options = options with { InputModes = SnapshotModes(options.InputModes), OutputModes = SnapshotModes(options.OutputModes), Skills = Array.AsReadOnly(options.Skills.ToArray()), AdditionalCardFields = options.AdditionalCardFields?.Clone(), AuthenticatedExtendedCard = options.AuthenticatedExtendedCard?.Clone(), Description = options.Description.Length == 0 ? options.Name : options.Description };
        InitializeCard();
    }
    /// <summary>Handles one HTTP request and transfers response ownership to the host.</summary>
    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request)); cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/.well-known/agent-card.json") return JsonResponse(Card());
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != _options.Url.AbsolutePath) return new(HttpStatusCode.NotFound) { Content = new StringContent("") };
        JsonElement? id = null;
        var streaming = false;
        try
        {
            if (!string.Equals(request.Content?.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return new(HttpStatusCode.UnsupportedMediaType) { Content = new StringContent("") };
            JsonElement rpc;
            try { rpc = await A2AHttp.ReadJsonAsync(request.Content!, _options.MaxRequestBytes, cancellationToken).ConfigureAwait(false); }
            catch (A2AProtocolException) { throw new A2ARpcException(-32700, "The JSON-RPC body is malformed or exceeds the configured limit."); }
            if (rpc.ValueKind != JsonValueKind.Object || !rpc.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !rpc.TryGetProperty("id", out var requestId) || requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
                !rpc.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                throw new A2ARpcException(-32600, "A JSON-RPC 2.0 request with an identifier is required.");
            id = requestId.Clone();
            if (method.GetString() == "agent/getAuthenticatedExtendedCard")
            {
                if (rpc.TryGetProperty("params", out var cardParameters) && cardParameters.ValueKind != JsonValueKind.Object) throw new A2ARpcException(-32602, "Extended-card parameters, when supplied, must be an object.");
                var card = await ReadAuthenticatedCardAsync(request, cancellationToken).ConfigureAwait(false);
                return JsonResponse(new { jsonrpc = "2.0", id, result = card });
            }
            streaming = method.GetString() is "message/stream" or "tasks/resubscribe";
            if (IsPushMethod(method.GetString()))
            {
                if (!rpc.TryGetProperty("params", out var pushParameters) || pushParameters.ValueKind != JsonValueKind.Object) throw new A2ARpcException(-32602, "Object parameters are required.");
                var pushResult = await RunPushOperationAsync(token => HandlePushRequestAsync(request, method.GetString()!, pushParameters, token), cancellationToken).ConfigureAwait(false);
                return JsonResponse(new { jsonrpc = "2.0", id, result = pushResult });
            }
            if (method.GetString() is not ("message/send" or "message/stream" or "tasks/get" or "tasks/cancel" or "tasks/resubscribe"))
                throw new A2ARpcException(-32601, "The JSON-RPC method is not supported.");
            if (!rpc.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) throw new A2ARpcException(-32602, "Object parameters are required.");
            if (method.GetString() == "message/stream")
            {
                var execution = await StartAsync(request, parameters, cancellationToken).ConfigureAwait(false);
                return new(HttpStatusCode.OK) { Content = new SseContent(execution.Events, id.Value, _options.MaxSubscribers) };
            }
            if (method.GetString() == "tasks/resubscribe") return Resubscribe(parameters, id.Value);
            var result = method.GetString() switch
            {
                "message/send" => await SendAsync(request, parameters, cancellationToken).ConfigureAwait(false),
                "tasks/get" => await GetTaskAsync(parameters, cancellationToken).ConfigureAwait(false),
                "tasks/cancel" => await CancelTaskAsync(parameters, cancellationToken).ConfigureAwait(false),
                _ => throw new A2ARpcException(-32601, "The JSON-RPC method is not supported.")
            };
            return JsonResponse(new { jsonrpc = "2.0", id, result });
        }
        catch (A2ARpcException error) { return ErrorResponse(id, error, streaming); }
        catch (ExtendedCardDeniedException) { return new(HttpStatusCode.Forbidden) { Content = new StringContent("") }; }
        catch (Exception error) when (error is A2AProtocolException or ArgumentException or NotSupportedException)
        { return ErrorResponse(id, new(-32602, "The request parameters are invalid or unsupported."), streaming); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ErrorResponse(id, new(-32603, "The server could not complete the operation."), streaming); }
    }
    private JsonElement Card() => AgentCard;
    private async Task<JsonElement> SendAsync(HttpRequestMessage request, JsonElement parameters, CancellationToken cancellationToken)
    {
        var execution = await StartAsync(request, parameters, cancellationToken).ConfigureAwait(false);
        var blocking = !(parameters.TryGetProperty("configuration", out var configuration) && configuration.TryGetProperty("blocking", out var option) && !option.GetBoolean());
        Task ready = blocking ? execution.Completion.Task : Task.WhenAny(execution.FirstUpdate.Task, execution.Completion.Task);
        await WaitAsync(ready, cancellationToken).ConfigureAwait(false);
        if (execution.Error is { } failure && !execution.Events.HasFinal) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return execution.Entry.Snapshot(HistoryLength(parameters, configuration: true));
    }
    private Execution Start(JsonElement parameters, JsonElement? pushConfiguration)
    {
        var message = A2AJson.RequiredObject(parameters, "message");
        var taskId = A2AJson.OptionalString(message, "taskId"); var contextId = A2AJson.OptionalString(message, "contextId");
        var configuration = OptionalObject(parameters, "configuration"); var metadata = OptionalObject(parameters, "metadata");
        if (configuration is { } settings && settings.TryGetProperty("blocking", out var blocking) && blocking.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new A2ARpcException(-32602, "blocking must be a boolean.");
        var outputModes = SelectOutputModes(configuration);
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) throw new A2ARpcException(-32602, "Message parts must be an array.");
        ValidatePartModes(parts, _options.InputModes);
        _ = HistoryLength(parameters, configuration: true);
        HostedTask entry; Execution execution;
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            if (taskId is not null)
            {
                entry = FindTask(taskId);
                if (contextId is not null && contextId != entry.ContextId) throw new A2ARpcException(-32602, "The task does not belong to the supplied context.");
                if (entry.Active || entry.Terminal) throw new A2ARpcException(-32602, "The task is active or has already reached a terminal state.");
            }
            else
            {
                MakeTaskRoom();
                taskId = Guid.NewGuid().ToString("N"); contextId ??= Guid.NewGuid().ToString("N");
                entry = new(taskId, contextId, _options.MaxTaskBytes); _tasks.Add(taskId, entry);
            }
            entry.Active = true; entry.LastUsed = ++_sequence;
            execution = new(entry, new(_options.MaxReplayEvents, _options.MaxReplayBytes), _shutdown.Token) { OutputModes = outputModes, PushConfiguration = pushConfiguration }; entry.Execution = execution;
        }
        try
        {
            var context = new A2ARequestContext(entry.Id, entry.ContextId, message, configuration: configuration, metadata: metadata)
            { CurrentTask = A2AJson.OptionalString(message, "taskId") is not null ? entry.Snapshot(null) : null };
            _ = RunAsync(execution, context, message);
            return execution;
        }
        catch { lock (_gate) { entry.Active = false; if (!entry.Started) _tasks.Remove(entry.Id); execution.Completion.TrySetResult(true); execution.Cancellation.Dispose(); } throw; }
    }
    private async Task RunAsync(Execution execution, A2ARequestContext context, JsonElement message)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        var entry = execution.Entry;
        try
        {
            await PrepareExecutionPushAsync(execution).ConfigureAwait(false);
            await _executor.ExecuteAsync(context, new TaskSink(this, entry, execution, message, _options.TaskStore), execution.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) { execution.Error = error; }
        finally
        {
            execution.Events.Complete(execution.Events.HasFinal ? null : execution.Error);
            lock (_gate)
            {
                entry.Active = false; entry.LastUsed = ++_sequence;
                if (!entry.Started)
                {
                    _tasks.Remove(entry.Id);
                    if (execution.PushConfigurationStored) _retiredPushTasks.Enqueue(entry.Id);
                }
                execution.Completion.TrySetResult(true);
            }
            execution.Cancellation.Dispose();
            Scope.Value = previous;
        }
    }
    private async Task<JsonElement> GetTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = A2AJson.RequiredString(parameters, "id"); var historyLength = HistoryLength(parameters, configuration: false);
        lock (_gate)
        {
            if (_tasks.TryGetValue(id, out var entry) && entry.Started) { entry.LastUsed = ++_sequence; return entry.Snapshot(historyLength); }
        }
        var stored = await LoadStoredTaskAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stored is null) throw new A2ARpcException(-32001, "The task was not found.");
        return stored.Snapshot(historyLength);
    }
    private HttpResponseMessage Resubscribe(JsonElement parameters, JsonElement id)
    {
        lock (_gate)
        {
            var entry = FindTask(A2AJson.RequiredString(parameters, "id"));
            if (!entry.Active) throw new A2ARpcException(-32004, "Only an active task can be resubscribed.");
            var execution = entry.Execution!; var start = entry.SnapshotWithCursor(execution.Events);
            return new(HttpStatusCode.OK) { Content = new SseContent(execution.Events, id, _options.MaxSubscribers, start.Cursor, start.Task) };
        }
    }
    private async Task<JsonElement> CancelTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        for (var scope = Scope.Value; scope is not null; scope = scope.Parent) if (ReferenceEquals(scope.Server, this)) throw new A2ARpcException(-32004, "An execution callback cannot await cancellation on its own server.");
        await RestoreTaskForOperationAsync(A2AJson.RequiredString(parameters, "id"), cancellationToken).ConfigureAwait(false);
        HostedTask entry; Execution execution; var cancelPending = false;
        lock (_gate)
        {
            entry = FindTask(A2AJson.RequiredString(parameters, "id"));
            if (entry.Terminal) throw new A2ARpcException(-32002, "The task is not in a cancelable state.");
            if (!entry.Active)
            {
                entry.Active = true; cancelPending = true;
                execution = new(entry, new(_options.MaxReplayEvents, _options.MaxReplayBytes), _shutdown.Token) { IsCancellation = true }; entry.Execution = execution;
            }
            else execution = entry.Execution!;
        }
        if (cancelPending) _ = RunCancellationAsync(execution);
        Exception? cancellationError = null;
        try { if (!execution.IsCancellation) execution.Cancellation.Cancel(); }
        catch (ObjectDisposedException) when (execution.Completion.Task.IsCompleted) { }
        catch (Exception error) { cancellationError = error; }
        await WaitAsync(execution.Completion.Task, cancellationToken).ConfigureAwait(false);
        if (execution.Error is { } failure && !execution.Events.HasFinal) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cancellationError is not null && execution.Error is { } primary && !ReferenceEquals(primary, cancellationError)) A2AHttp.AddCleanup(primary, cancellationError);
        var result = entry.Snapshot(null);
        if (result.GetProperty("status").GetProperty("state").GetString() != "canceled") throw new A2ARpcException(-32002, "The task completed before cancellation was accepted.");
        return result;
    }
    private async Task RunCancellationAsync(Execution execution)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        var entry = execution.Entry;
        try { await _executor.CancelRestoredAsync(entry.Id, entry.ContextId, entry.Snapshot(null), new TaskSink(this, entry, execution, default, _options.TaskStore), execution.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { execution.Error = error; }
        finally
        {
            execution.Events.Complete(execution.Events.HasFinal ? null : execution.Error);
            lock (_gate) { entry.Active = false; entry.LastUsed = ++_sequence; execution.Completion.TrySetResult(true); }
            execution.Cancellation.Dispose(); Scope.Value = previous;
        }
    }
    private HostedTask FindTask(string id) => _tasks.TryGetValue(id, out var value) && value.Started ? value : throw new A2ARpcException(-32001, "The task was not found.");
    private static JsonElement? OptionalObject(JsonElement parameters, string name)
        => parameters.TryGetProperty(name, out var value) ? A2AJson.RequiredObject(parameters, name) : null;
    private static int? HistoryLength(JsonElement parameters, bool configuration)
    {
        if (configuration && !parameters.TryGetProperty("configuration", out parameters)) return null;
        if (!parameters.TryGetProperty("historyLength", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var length) || length < 0) throw new A2ARpcException(-32602, "historyLength must be a non-negative integer.");
        return length;
    }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage ErrorResponse(JsonElement? id, A2ARpcException error, bool streaming)
    {
        if (!streaming || id is null) return JsonResponse(new { jsonrpc = "2.0", id, error = new { code = error.Code, message = error.Message } });
        var events = new EventBuffer(1, 4096); events.Complete(error);
        return new(HttpStatusCode.OK) { Content = new SseContent(events, id.Value, 1) };
    }
    private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = Task.Delay(Timeout.Infinite, canceled.Token);
        if (await Task.WhenAny(task, wait).ConfigureAwait(false) != task) cancellationToken.ThrowIfCancellationRequested();
        canceled.Cancel(); await task.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>Stops server-owned tasks and releases transferred resources.</summary>
    public ValueTask DisposeAsync()
    {
        for (var scope = Scope.Value; scope is not null; scope = scope.Parent) if (ReferenceEquals(scope.Server, this)) throw new InvalidOperationException("An A2A server cannot be disposed from its own execution callbacks.");
        TaskCompletionSource<bool>? completion = null; Task[] active = [];
        lock (_gate)
        {
            if (_disposeTask is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposeTask = completion.Task; active = _tasks.Values.Where(value => value.Execution is not null).Select(value => value.Execution!.Completion.Task).Concat(_storeReads).Concat(_cardReads).Concat(_pushOperations).ToArray(); }
        }
        if (completion is not null) _ = DisposeCoreAsync(active, completion);
        return new(_disposeTask!);
    }
    private async Task DisposeCoreAsync(Task[] active, TaskCompletionSource<bool> completion)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        Exception? primary = null;
        try
        {
            try { _shutdown.Cancel(); } catch (Exception error) { primary = error; }
            foreach (var task in active) try { await task.ConfigureAwait(false); } catch (Exception error) { if (primary is null) primary = error; else A2AHttp.AddCleanup(primary, error); }
            try { if (_options.OwnsExecutor) await _executor.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { if (primary is null) primary = error; else A2AHttp.AddCleanup(primary, error); }
            _shutdown.Dispose(); _pushConfigurationChanges.Dispose();
        }
        catch (Exception error) { if (primary is null) primary = error; else A2AHttp.AddCleanup(primary, error); }
        finally { Scope.Value = previous; }
        if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
    }
    private sealed class TaskSink(A2AServer server, HostedTask task, Execution execution, JsonElement message, IA2ATaskStore? store) : IA2AEventSink
    {
        public async ValueTask PublishAsync(JsonElement update, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (A2AJson.RequiredString(update, "kind") == "artifact-update") ValidatePartModes(update.GetProperty("artifact").GetProperty("parts"), execution.OutputModes);
            await task.PublishAsync(update, message, execution.Events, store, cancellationToken).ConfigureAwait(false); execution.FirstUpdate.TrySetResult(true);
            await server.PushAsync(task.Snapshot(null), cancellationToken).ConfigureAwait(false);
        }
    }
    private sealed class Execution(HostedTask entry, EventBuffer events, CancellationToken shutdown)
    {
        internal HostedTask Entry { get; } = entry;
        internal EventBuffer Events { get; } = events;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> FirstUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Error { get; set; }
        internal bool IsCancellation { get; init; }
        internal IReadOnlyList<string> OutputModes { get; init; } = [];
        internal JsonElement? PushConfiguration { get; init; }
        internal bool PushConfigurationStored { get; set; }
    }
    private sealed record HostScope(A2AServer Server, HostScope? Parent);
}
