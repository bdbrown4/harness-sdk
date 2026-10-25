using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Middleware;
using Strands.Agents.SchemaImplementation;
using Strands.Agents.Tasks;

namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly BackgroundTaskController? _backgroundTasks;
    private IReadOnlyDictionary<string, JsonElement> CopyChildApplicationState() => _backgroundTasks is null ? State.GetAll()
        : State.GetAll().Where(pair => pair.Key != BackgroundTaskController.StateKey).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static BackgroundTasksConfig? CopyBackgroundTasks(BackgroundTasksConfig? config) => config is null ? null : config with
    {
        Agentic = config.Agentic?.ToArray(),
        Always = config.Always?.ToArray() ?? throw new ArgumentException("Always cannot be null."),
        Never = config.Never?.ToArray() ?? throw new ArgumentException("Never cannot be null.")
    };

    private async Task<ToolResultBlock> ExecuteBackgroundToolAsync(BackgroundToolExecution execution)
    {
        var previous = ActiveLifecycle.Value; ActiveLifecycle.Value = new(this, previous);
        try
        {
            var call = execution.ToolUse;
            while (true)
            {
                var context = new ExecuteToolContext(this, execution.Tool, call, execution.InvocationState, execution.Execution.CancellationToken)
                { InterruptHandler = (name, reason, response) => execution.Interrupts.Resolve(name, reason, response, InterruptSource.Middleware) };
                ToolResultBlock? result = null;
                await foreach (var item in Middleware.InvokeAsync(MiddlewareStages.ExecuteTool, context, Terminal, execution.Execution.CancellationToken).ConfigureAwait(false))
                {
                    if (item is MiddlewareStreamEvent<ExecuteToolResult> { Event: HookEvent hook }) await Hooks.InvokeAsync(hook, execution.Execution.CancellationToken).ConfigureAwait(false);
                    else if (item is MiddlewareResultEvent<ExecuteToolResult> completed) result = completed.Result.Result;
                }
                result = NormalizeToolResult(result!, call.ToolUseId) with { IteratorCleanupFailed = execution.Interrupts.IteratorCleanupFailed || result!.IteratorCleanupFailed };
                var after = new AfterToolCallEvent(this, call, result, execution.InvocationState);
                await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false);
                if (ShouldRetryTool(after, result, execution.Execution.CancellationToken))
                {
                    execution.Interrupts.ResetAttempt();
                    continue;
                }
                return NormalizeToolResult(after.Result, call.ToolUseId);
            }

            async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Terminal(ExecuteToolContext effective, [EnumeratorCancellation] CancellationToken token)
            {
                var selected = effective.Tool;
                var admitted = ReferenceEquals(selected, execution.Tool) ? execution.AdmittedSpec : null;
                var spec = selected is null ? null : admitted ?? RegisteredToolSpec(selected);
                if (selected is null || !BackgroundTaskPolicy.Eligible(selected, spec!) || _backgroundTasks!.Policy.For(spec!.Name) is not { Mode: not BackgroundTaskMode.Never })
                    throw new InvalidOperationException("Tool cannot run in the background.");
                var effectiveCall = (ToolUseBlock)Message.CloneBlock(effective.ToolUse with { ToolUseId = call.ToolUseId });
                var schema = admitted is not null ? execution.AdmittedSchema! : RegisteredToolSchema(selected);
                if (!schema.Evaluate(effectiveCall.Input).IsValid)
                { yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(call.ToolUseId, "Input does not match the schema for tool " + spec!.Name + ".", ToolResultStatus.Error))); yield break; }
                var body = execution.Interrupts.ReuseBody(effectiveCall, spec!.Name);
                if (body is null)
                {
                    body = await ObserveBackgroundToolBodyAsync(effectiveCall, () => InvokeToolBodyAsync(selected, effectiveCall, effective.InvocationState, token,
                        async (update, ct) => { if (update is HookEvent hook) await Hooks.InvokeAsync(hook, ct).ConfigureAwait(false); },
                        (name, reason, response) => execution.Interrupts.Resolve(name, reason, response, InterruptSource.Tool), execution.Interrupts.AgentToolContext(), execution.Execution.CancellationToken)).ConfigureAwait(false);
                    execution.Interrupts.RecordBody(effectiveCall, spec.Name, body);
                }
                yield return new MiddlewareResultEvent<ExecuteToolResult>(new(body));
            }
        }
        finally { ActiveLifecycle.Value = previous; }
    }

    private sealed class BackgroundTaskController
    {
        internal const string StateKey = "strands.backgroundTasks";
        private static readonly JsonSerializerOptions Json = CreateJson();
        private readonly Agent _agent;
        private readonly BackgroundTasksConfig _config;
        private readonly BackgroundTaskManager _manager;
        private readonly object _gate = new();
        private readonly Dictionary<string, BackgroundTaskSnapshot> _restored = new(StringComparer.Ordinal);
        internal BackgroundTaskPolicy Policy { get; }
        internal BackgroundTaskController(Agent agent, BackgroundTasksConfig config)
        {
            _agent = agent; _config = config;
            Policy = new(agent._options.BackgroundTasksPolicyNormalizer is null ? config : new BackgroundTasksConfig { Agentic = [] });
            _manager = new(config, agent.ExecuteBackgroundToolAsync, _ => Persist());
        }
        internal void Initialize()
        {
            if (_agent._options.BackgroundTasksPolicyNormalizer is { } normalize)
                Policy.Normalize(CopyBackgroundTasks(normalize(_agent.Tools, CopyBackgroundTasks(_config)!, _agent.RegisteredToolSpec)) ?? throw new InvalidOperationException("Background task policy normalization cannot return null."));
            var tool = new ManageTool(this);
            Policy.Validate(_agent.Tools.Concat(new[] { tool }).ToArray(), _agent.RegisteredToolSpec);
            if (_agent._options.ToolFilter?.Invoke(tool) != false)
            {
                if (_agent._tools.ContainsKey(tool.Spec.Name)) throw new InvalidOperationException("Duplicate tool name: " + tool.Spec.Name + ".");
                _agent.ValidateNormalizedToolName(tool.Spec.Name, _agent._tools.Keys, initialization: true);
                _agent._tools.Add(tool.Spec.Name, (tool, ToolSchema.Compile(tool.Spec.InputSchema)));
                Volatile.Write(ref _agent._toolSnapshot, Array.AsReadOnly(_agent._tools.Values.Select(value => value.Tool).ToArray()));
            }
            _agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new ValueTask<InvokeModelContext>(context with
            {
                ToolSpecs = context.ToolSpecs.Select(spec => _agent.Tools.FirstOrDefault(value => _agent.RegisteredToolSpec(value).Name == spec.Name) is { } registered &&
                    BackgroundTaskPolicy.Eligible(registered, _agent.RegisteredToolSpec(registered)) && Policy.For(spec.Name)?.Mode == BackgroundTaskMode.Agentic ? Policy.AddSelection(spec) ?? spec : spec).ToArray()
            }));
            _agent.Hooks.Register<InitializedEvent>((_, _) => { LoadState(); return default; }, int.MaxValue);
            _agent.Hooks.Register<BeforeModelCallEvent>((boundary, _) =>
            {
                var questions = Questions();
                if (questions.Count > 0) throw new BackgroundModelInterruptException(questions);
                QueueDelivery(boundary); return default;
            });
            _agent.Hooks.Register<AfterInvocationEvent>(AfterInvocationAsync);
            _agent.Hooks.Register<ShutdownEvent>(async (_, _) => { try { await _manager.StopAsync().ConfigureAwait(false); } finally { Persist(); } });
        }
        private async ValueTask AfterInvocationAsync(AfterInvocationEvent boundary, CancellationToken cancellationToken)
        {
            if (!boundary.ContinuationAllowed || boundary.Error is not null || boundary.Result?.StopReason == StopReasons.Interrupt ||
                _agent.InterruptState is not null) return;
            if (boundary.InvocationCancellationToken.IsCancellationRequested)
            { boundary.AutomaticWaitCancelled = List().Any(task => !task.IsTerminal); return; }
            try { await WaitForCompletionAsync(boundary.InvocationCancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (boundary.InvocationCancellationToken.IsCancellationRequested)
            { boundary.AutomaticWaitCancelled = true; return; }
            if (Questions().Count > 0) { boundary.RequestAutomaticResume(); return; }
            QueueDelivery(boundary);
        }

        private void QueueDelivery(HookEvent boundary)
        {
            lock (_gate)
            {
                var terminal = List().Where(task => task.IsTerminal).ToArray();
                if (terminal.Length == 0) return;
                var messages = terminal.SelectMany(task => new[]
                {
                    new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock(task.TaskId, BackgroundTaskPolicy.ManageToolName, JsonSerializer.SerializeToElement(new { mode = "get", taskId = task.TaskId })) }),
                    new Message(Role.User, new ContentBlock[] { new ToolResultBlock(task.TaskId, ResultContent(task)) })
                }).ToArray();
                _agent.AddContinuationInput(boundary, messages, () =>
                {
                    lock (_gate)
                    {
                        var live = new HashSet<string>(_manager.List().Select(task => task.TaskId), StringComparer.Ordinal);
                        _manager.Remove(terminal.Where(task => live.Contains(task.TaskId)).Select(task => task.TaskId).ToArray());
                        foreach (var task in terminal) _restored.Remove(task.TaskId);
                        Persist();
                    }
                    return default;
                });
            }
        }
        internal ToolResultBlock Dispatch(ToolUseBlock call, ITool selected, IReadOnlyDictionary<string, object?> state, string passId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Physical executions retain the accepted metadata even after this owner's table is replaced.
            var admitted = Volatile.Read(ref _agent._providerRegistrations).TryGetValue(selected, out var entry) && entry.Updates is not null ? entry : null;
            var task = _manager.Submit(call, state, passId, selected, admitted?.Spec, admitted?.Schema); Persist();
            return new(call.ToolUseId, "Background task dispatched.\n\nTask ID: " + task.TaskId + "\nTool: " + task.ToolName + "\nStatus: " + Status(task.Status) +
                "\n\nThe task is running in the background. Continue without waiting or polling.\nThe final result will be delivered automatically when the task completes.");
        }
        internal IReadOnlyList<BackgroundTaskSnapshot> List() { lock (_gate) return _manager.List().Concat(_restored.Values.Select(task => task.Copy())).ToArray(); }
        internal IReadOnlyList<Interrupt> Questions() => List().Where(task => task.Status == BackgroundTaskStatus.InputRequired).SelectMany(Unanswered).ToArray();
        private static IEnumerable<Interrupt> Unanswered(BackgroundTaskSnapshot task) => task.InterruptState?.Interrupts.Where(question => !task.InterruptState.Responses.Any(answer => answer.InterruptId == question.Id)) ?? [];
        internal InvocationOptions ForegroundOptions(InvocationOptions options, string? prompt, bool apply)
        {
            var tasks = List().Where(task => task.Status == BackgroundTaskStatus.InputRequired).ToArray();
            if (tasks.Length == 0) return options;
            if (prompt is not null || options.InputMessage is not null || options.InputMessages.Count > 0) throw new ArgumentException("A background task waiting for human input must be resumed without a new prompt.", nameof(prompt));
            var ids = new HashSet<string>(tasks.SelectMany(Unanswered).Select(question => question.Id), StringComparer.Ordinal);
            if (apply)
                foreach (var task in tasks)
                {
                    var known = new HashSet<string>(Unanswered(task).Select(question => question.Id), StringComparer.Ordinal);
                    var answers = options.InterruptResponses.Where(answer => known.Contains(answer.InterruptId)).ToArray();
                    if (answers.Length > 0) _manager.Resume(task.TaskId, answers);
                }
            if (apply) Persist();
            return options with { InterruptResponses = options.InterruptResponses.Where(answer => !ids.Contains(answer.InterruptId)).ToArray() };
        }
        internal async Task WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            if (!_config.WaitForCompletion) return;
            while (true)
            {
                var tasks = _manager.List();
                if (tasks.Any(task => task.Status == BackgroundTaskStatus.InputRequired)) return;
                var pending = tasks.Where(task => !task.IsTerminal).ToArray();
                if (pending.Length == 0) return;
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waits = pending.Select(task => _manager.WaitAsync(task.TaskId, waiting.Token)).ToArray();
                try { await Task.WhenAny(waits).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
                finally { waiting.Cancel(); foreach (var wait in waits) try { await wait.ConfigureAwait(false); } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { } }
            }
        }
        internal void AssertCanLoadSnapshot() { if (_manager.List().Count > 0) throw new InvalidOperationException("Cannot load a snapshot while background tasks are still tracked."); }
        internal void ValidateStoredState(IReadOnlyDictionary<string, JsonElement>? state) { if (state is not null && state.TryGetValue(StateKey, out var value)) _ = ParseStored(value); }
        internal void LoadState()
        {
            var stored = ParseStored(_agent.State.Get(StateKey));
            lock (_gate)
            {
                _restored.Clear();
                foreach (var task in stored)
                {
                    _restored.Add(task.TaskId, task.IsTerminal ? task.Copy() : task with
                    {
                        Status = BackgroundTaskStatus.Failed,
                        InterruptState = null,
                        LastUpdatedAt = DateTimeOffset.UtcNow,
                        Error = new(BackgroundTaskFailureType.ExecutionError, "Background task cannot resume after restoring persisted state")
                    });
                }
                Persist();
            }
        }
        private static BackgroundTaskSnapshot[] ParseStored(JsonElement? value)
        {
            try
            {
                var tasks = value?.Deserialize<BackgroundTaskSnapshot[]>(Json) ?? [];
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var task in tasks)
                {
                    if (task is null || string.IsNullOrWhiteSpace(task.TaskId) || string.IsNullOrWhiteSpace(task.ToolUseId) || string.IsNullOrWhiteSpace(task.ToolName) ||
                        !Enum.IsDefined(typeof(BackgroundTaskStatus), task.Status) || !ids.Add(task.TaskId) || task.Result is { } result && (result.ToolUseId != task.ToolUseId || !Enum.IsDefined(typeof(ToolResultStatus), result.Status)))
                        throw new InvalidDataException("Invalid persisted background task.");
                    if (task.Error is { } failure && (!Enum.IsDefined(typeof(BackgroundTaskFailureType), failure.Type) || string.IsNullOrEmpty(failure.Message)) ||
                        task.Status == BackgroundTaskStatus.Completed && (task.Result is null || task.Result.Status != ToolResultStatus.Success || task.Error is not null || task.InterruptState is not null) ||
                        task.Status == BackgroundTaskStatus.Failed && (task.Error is null || task.InterruptState is not null || task.Result is { Status: not ToolResultStatus.Error }) ||
                        task.Status == BackgroundTaskStatus.Cancelled && (task.Result is not null || task.Error is not null || task.InterruptState is not null) ||
                        !task.IsTerminal && (task.Result is not null || task.Error is not null) ||
                        task.Status == BackgroundTaskStatus.InputRequired && (task.InterruptState is null || !Unanswered(task).Any()))
                        throw new InvalidDataException("Persisted background task status is inconsistent with its result or failure.");
                }
                return tasks.Select(task => task.Copy()).ToArray();
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("Invalid persisted background task state.", error); }
        }
        private void Persist()
        {
            lock (_gate)
            {
                var tasks = List();
                if (tasks.Count == 0) _agent.State.Delete(StateKey);
                else _agent.State.Set(StateKey, JsonSerializer.SerializeToElement(tasks, Json));
            }
        }
        private BackgroundTaskSnapshot Require(string id) => List().FirstOrDefault(task => task.TaskId == id) ?? throw new KeyNotFoundException("Background task '" + id + "' was not found.");
        private static IReadOnlyList<ContentBlock> ResultContent(BackgroundTaskSnapshot task)
        {
            var data = JsonSerializer.SerializeToElement(task with { Result = null }, Json);
            var fields = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject(); fields.Remove("result"); fields.Remove("interruptState");
            if (task.Status == BackgroundTaskStatus.InputRequired) fields["interrupts"] = JsonSerializer.SerializeToNode(Unanswered(task), Json);
            return new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(fields)) }.Concat(task.Result is { } result
                ? result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) } : []).ToArray();
        }
        private static string Status(BackgroundTaskStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
        private static JsonSerializerOptions CreateJson()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)); return options;
        }
        private sealed class ManageTool(BackgroundTaskController owner) : IRichTool
        {
            public ToolSpec Spec { get; } = new(BackgroundTaskPolicy.ManageToolName, "List, get, or cancel background tasks. Results are delivered automatically as synthetic get calls. Do not poll for results.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { mode = new { type = "string", @enum = new[] { "list", "get", "cancel" } }, taskId = new { type = "string", minLength = 1 } }, required = new[] { "mode" } }));
            public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
            public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mode = input.GetProperty("mode").GetString();
                if (mode == "list") return new(new ToolResultBlock(context.ToolUse.ToolUseId, new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(new { tasks = owner.List().Select(task => new { taskId = task.TaskId, toolName = task.ToolName, status = Status(task.Status) }) })) }));
                if (!input.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw new ArgumentException("taskId is required for get or cancel.");
                var task = owner.Require(id.GetString()!);
                if (mode == "get") return new(new ToolResultBlock(context.ToolUse.ToolUseId, ResultContent(task)));
                if (mode != "cancel") throw new ArgumentException("Unknown background task mode.");
                if (!task.IsTerminal) task = owner._manager.Cancel(task.TaskId);
                owner.Persist();
                return new(new ToolResultBlock(context.ToolUse.ToolUseId, new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(new { taskId = task.TaskId, status = Status(task.Status) })) }));
            }
        }
    }

    private sealed class BackgroundModelInterruptException(IReadOnlyList<Interrupt> interrupts) : Exception("Background work requires human input.")
    {
        internal IReadOnlyList<Interrupt> Interrupts { get; } = Array.AsReadOnly(interrupts.ToArray());
    }
}
