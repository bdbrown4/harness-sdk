using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Context;
using Strands.Agents.MultiAgent;
using Strands.Harness.Mcp;
using Strands.Harness.Models;

namespace Strands.Harness.Tools;

/// <summary>Creates an optional tool for model-defined handoff teams.</summary>
public static partial class SwarmTool
{
    /// <summary>The pinned Python helper description.</summary>
    public const string DefaultDescription = "Spin up a team of AI agents that solve a task together by handing off to one another. Each agent has its own name, system prompt, and tools (drawn from your own). Agents get their own handoff tool and cannot start another swarm; your other tools are passed through. The `entry_point` agent (default: the first in `agents`) receives the task and starts the work. The run ends when an agent finishes without handing off. Returns each participating agent's final output, prefixed with its name. If the team does not complete (an agent fails, or a handoff, iteration, or time limit is hit), the call fails with the partial output.";

    /// <summary>Creates the helper using detached developer configuration.</summary>
    public static IRichTool Create(SwarmToolOptions? options = null) => new TeamTool(Capture(options ?? new()));

    private sealed class TeamTool : IRichTool, IStreamingTool
    {
        private readonly SwarmToolOptions _options;
        private readonly ConditionalWeakTable<Agent, Owner> _owners = new();
        private readonly object _ownersGate = new();

        internal TeamTool(SwarmToolOptions options)
        {
            _options = options;
            Spec = new(options.Name, options.Description ?? DefaultDescription, BuildSchema(options));
        }
        public ToolSpec Spec { get; }
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            ToolStreamRunner.InvokeAsync(this, input, context, null, cancellationToken);

        public async IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = OwnerFor(context.Agent);
            var shutdownToken = owner.Token;
            var call = owner.Enter(context.Agent.ActiveInterruptBatchId, context.ToolUse, context.Agent.InterruptState, cancellationToken);
            CancellationTokenSource? linked = null;
            var token = cancellationToken;
            IAsyncEnumerator<IMultiAgentStreamEvent>? iterator = null;
            MultiAgentResult? result = null;
            Exception? primary = null;
            var pause = false;
            var reachedBoundary = false;
            try
            {
                try
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
                    token = linked.Token;
                    token.ThrowIfCancellationRequested();
                    MultiAgentInput swarmInput;
                    if (call.Swarm is not null)
                    {
                        // Collect every outer answer before allowing a child to resume.
                        pause = true;
                        var answers = call.Questions.Select(question => new InterruptResponse(question.Id,
                            context.Interrupt(question.Id, Reason(question)))).ToArray();
                        swarmInput = MultiAgentInput.Resume(answers);
                        pause = false;
                    }
                    else
                    {
                        if (context.Agent.InterruptState?.Interrupts.Any(question => question.ToolUseId == context.ToolUse.ToolUseId) == true)
                            throw new InvalidOperationException("The interrupted swarm is no longer in memory. No child was rebuilt or resumed.");
                        var resolved = Resolve(input, _options);
                        foreach (var specification in resolved.Specs)
                        {
                            token.ThrowIfCancellationRequested();
                            var child = _options.Builder is { } builder
                                ? await builder(specification, context, token).ConfigureAwait(false)
                                : await BuildDefaultAsync(specification, context.Agent, token).ConfigureAwait(false);
                            if (child is null) throw new InvalidOperationException("The swarm builder returned null.");
                            if (ReferenceEquals(child, context.Agent)) throw new InvalidOperationException("The swarm builder must return a fresh child, not its calling parent.");
                            if (call.Children.Any(value => ReferenceEquals(value, child))) throw new InvalidOperationException("The swarm builder returned the same child more than once.");
                            call.Children.Add(child);
                            await child.InitializeAsync(token).ConfigureAwait(false);
                            FilterAndValidate(child, context.Agent, specification);
                        }
                        var limits = SwarmOptions.Python;
                        call.Swarm = new(limits with
                        {
                            Nodes = call.Children.Select((child, index) => new AgentNode(child, resolved.Specs[index].Name)).ToArray(),
                            Start = resolved.Entry,
                            MaxHandoffs = _options.MaxHandoffs ?? limits.MaxHandoffs,
                            MaxSteps = _options.MaxIterations ?? limits.MaxSteps,
                            Timeout = _options.ExecutionTimeout ?? limits.Timeout,
                            NodeTimeout = _options.NodeTimeout ?? limits.NodeTimeout,
                            RepetitiveHandoffDetectionWindow = _options.RepetitiveHandoffDetectionWindow ?? limits.RepetitiveHandoffDetectionWindow,
                            RepetitiveHandoffMinUniqueAgents = _options.RepetitiveHandoffMinUniqueAgents ?? limits.RepetitiveHandoffMinUniqueAgents
                        });
                        swarmInput = new MultiAgentInput(resolved.Task);
                    }
                    var childState = context.InvocationState.Where(pair => pair.Key != "request_state").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    iterator = call.Swarm.StreamAsync(swarmInput, new() { InvocationState = childState }, token).GetAsyncEnumerator(token);
                }
                catch (Exception error) { pause = error is InterruptException; primary = error; }

                if (iterator is not null)
                    while (true)
                    {
                        bool moved;
                        try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception error) { primary = error; break; }
                        if (!moved) break;
                        if (iterator.Current is MultiAgentResultEvent terminal)
                        {
                            if (result is not null) { primary = new InvalidOperationException("The swarm emitted more than one result."); break; }
                            result = terminal.Result;
                        }
                        else
                        {
                            if (result is not null) { primary = new InvalidOperationException("The swarm emitted progress after its result."); break; }
                            yield return new ToolStreamEvent(iterator.Current);
                        }
                    }
                if (primary is null)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (result is null) throw new InvalidOperationException("Swarm stream ended without producing a result.");
                        if (result.Status == Status.Interrupted)
                        {
                            call.Questions = result.Interrupts.ToArray();
                            pause = true;
                            if (call.Questions.Count == 0) throw new InvalidOperationException("The interrupted swarm has no human requests.");
                            var first = call.Questions[0];
                            _ = context.Interrupt(first.Id, Reason(first));
                            throw new InvalidOperationException("A new swarm interrupt unexpectedly had an existing answer.");
                        }
                        if (result.Status != Status.Completed)
                            primary = result.Error ?? result.Results.FirstOrDefault(value => value.Error is not null)?.Error ?? new InvalidOperationException("The swarm did not complete.");
                    }
                    catch (Exception error) { pause = error is InterruptException; primary = error; }
                }
                reachedBoundary = true;
            }
            finally
            {
                if (iterator is not null)
                    try { await iterator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { Retain(ref primary, cleanup); }
                if (!pause)
                    try { await call.CloseAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { Retain(ref primary, cleanup); }
                try { linked?.Dispose(); }
                catch (Exception cleanup) { Retain(ref primary, cleanup); }
                IReadOnlyList<string> outerIds = pause && primary is InterruptException interrupted
                    ? Array.AsReadOnly(interrupted.Interrupts.Select(question => question.Id).ToArray()) : Array.Empty<string>();
                owner.Leave(call, pause, outerIds);
                if (!reachedBoundary && primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
            }
            if (primary is InterruptException) ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary is OperationCanceledException cancellation && cancellationToken.IsCancellationRequested)
            {
                if (cancellation.CancellationToken == cancellationToken) ExceptionDispatchInfo.Capture(primary).Throw();
                var mapped = new OperationCanceledException(cancellation.Message, cancellation, cancellationToken);
                if (cancellation.Data["Strands.CleanupErrors"] is List<Exception> cleanup) mapped.Data["Strands.CleanupErrors"] = cleanup;
                throw mapped;
            }
            if (primary is OperationCanceledException && shutdownToken.IsCancellationRequested) ExceptionDispatchInfo.Capture(primary).Throw();
            var text = result is null ? string.Empty : Display(result);
            if (primary is not null)
            {
                var heading = result is { Status: not Status.Completed }
                    ? $"Swarm stopped before completing (status={result.Status.ToString().ToLowerInvariant()}, {result.Results.Count(value => value.Status == Status.Completed)} iterations)."
                    : "Swarm error: " + primary.Message;
                yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, heading + (text.Length == 0 ? string.Empty : " Partial output:\n" + text), ToolResultStatus.Error) { Error = primary });
                yield break;
            }
            yield return new ToolStreamEvent(text);
            yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, text));
        }

        private Owner OwnerFor(Agent parent)
        {
            // The internal shutdown admission is atomic and does not invoke borrowed code.
            lock (_ownersGate)
            {
                if (_owners.TryGetValue(parent, out var value)) return value;
                var created = new Owner(parent);
                _owners.Add(parent, created);
                return created;
            }
        }

        private static bool Forbidden(ITool tool, string name) => name == "handoff_to_agent" || tool is TeamTool;

        private static void FilterAndValidate(Agent child, Agent parent, SwarmAgentSpec specification)
        {
            foreach (var tool in child.Tools.ToArray())
            {
                var name = child.RegisteredToolSpec(tool).Name;
                if (Forbidden(tool, name) && !child.RemoveApplicationTool(name)) throw new InvalidOperationException("A child provider/plugin exposes a reserved swarm capability: " + name);
            }
            var parentContext = parent.ContextManager?.GetTools() ?? [];
            var available = parent.Tools.Where(tool => !parentContext.Any(value => ReferenceEquals(value, tool)))
                .Select(tool => parent.RegisteredToolSpec(tool).Name).ToArray();
            var allowed = new HashSet<string>(specification.Tools is null ? available : specification.Tools.Where(name => available.Contains(name, StringComparer.Ordinal)), StringComparer.Ordinal);
            var ownContext = child.ContextManager?.GetTools() ?? [];
            foreach (var tool in child.Tools)
                if (!ownContext.Any(value => ReferenceEquals(value, tool)) && !allowed.Contains(child.RegisteredToolSpec(tool).Name))
                    throw new InvalidOperationException("Child tool exceeds its delegated parent scope: " + child.RegisteredToolSpec(tool).Name);
        }

        private static async ValueTask<Agent> BuildDefaultAsync(SwarmAgentSpec specification, Agent parent, CancellationToken cancellationToken)
        {
            var contextTools = parent.ContextManager?.GetTools() ?? [];
            var captured = parent.Tools.Where(tool => !contextTools.Any(value => ReferenceEquals(value, tool)))
                .Select(tool => new KeyValuePair<ITool, string>(tool, parent.RegisteredToolSpec(tool).Name)).ToArray();
            var allowed = new HashSet<string>(specification.Tools is null ? captured.Select(value => value.Value) : specification.Tools.Where(name => captured.Any(value => value.Value == name)), StringComparer.Ordinal);
            var providerScope = McpProviderBindings.CaptureProviderScope(parent, allowed);
            var resources = new List<IAsyncDisposable>(); Agent? child = null; AgentOptions? childOptions = null;
            try
            {
                var model = specification.Model switch
                {
                    IModel instance => new ModelResolution.Resolved(instance, false, null, false),
                    string name => ModelResolution.Identifier(new HarnessOptions { BuiltinTools = [] }, name, HarnessEffort.Auto, true, false, false, resources),
                    _ => new ModelResolution.Resolved(parent.Model, false, null, false)
                };
                var manager = model.Model is IStatefulModel { Stateful: true } ? null : ContextManager.From("auto");
                child = parent.CreateChild(options =>
                {
                    var inheritedFilter = options.ToolFilter; var inheritedSpec = options.ProviderSpecFilter;
                    childOptions = options with
                    {
                        CheckpointBehavior = CheckpointBehavior.Disabled,
                        Name = specification.Name,
                        Description = null,
                        Model = model.Model,
                        OwnsModel = model.Owned,
                        OwnedResources = resources.ToArray(),
                        SystemPrompt = specification.Instructions ?? string.Empty,
                        SystemPromptContent = null,
                        Messages = [],
                        State = new Dictionary<string, JsonElement>(),
                        ModelState = new Dictionary<string, JsonElement>(),
                        SessionId = null,
                        SessionStore = null,
                        BackgroundTasks = null,
                        ContextManager = manager,
                        Plugins = options.Plugins.Where(plugin => plugin.Name != "strands:context-manager").ToArray(),
                        Tools = options.Tools.Where(tool => captured.Any(entry => ReferenceEquals(entry.Key, tool) && allowed.Contains(entry.Value)) && !Forbidden(tool, captured.First(entry => ReferenceEquals(entry.Key, tool)).Value)).ToArray(),
                        ToolFilter = tool => (inheritedFilter?.Invoke(tool) ?? true) &&
                            (Swarm.IsHandoffTool(tool) || !Forbidden(tool, providerScope.Name(tool)) &&
                                (manager?.GetTools().Any(value => ReferenceEquals(value, tool)) == true || allowed.Contains(providerScope.Name(tool)))),
                        ProviderSpecFilter = (tool, spec) => !Forbidden(tool, spec.Name) && (inheritedSpec?.Invoke(tool, spec) ?? true) && providerScope.Admit(tool, spec)
                    };
                    return childOptions;
                });
                await child.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return child;
            }
            catch (Exception failure)
            {
                await Harness.CleanupFailedConstructionAsync(child, childOptions, resources, failure).ConfigureAwait(false);
                throw;
            }
        }

        private static JsonElement Reason(Interrupt question) => JsonSerializer.SerializeToElement(new { name = question.Name, reason = question.Reason });

        private static string Display(MultiAgentResult result)
        {
            var latest = new List<NodeResult>(); var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in result.Results)
                if (positions.TryGetValue(value.NodeId, out var index)) latest[index] = value;
                else { positions.Add(value.NodeId, latest.Count); latest.Add(value); }
            return string.Join("\n", latest.Select(value =>
            {
                var text = new Message(Role.Assistant, value.Content).Text;
                if (text.Length == 0 && value.Error is { } error) text = error.Message;
                return text.Length == 0 ? null : value.NodeId + ": " + text;
            }).Where(value => value is not null));
        }
    }

    private static void Retain(ref Exception? primary, Exception cleanup)
    {
        if (primary is null) { primary = cleanup; return; }
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        if (!errors.Any(value => ReferenceEquals(value, cleanup))) errors.Add(cleanup);
    }

    private sealed class Run((string? Batch, string Id) key, ToolUseBlock call)
    {
        internal (string? Batch, string Id) Key { get; } = key;
        internal ToolUseBlock Call { get; } = new(call.ToolUseId, call.Name, call.Input.Clone());
        internal IReadOnlyList<string> OuterInterruptIds { get; set; } = [];
        internal List<Agent> Children { get; } = [];
        internal Swarm? Swarm { get; set; }
        internal IReadOnlyList<Interrupt> Questions { get; set; } = [];
        internal TaskCompletionSource<bool>? Active { get; set; }
        private readonly object _gate = new();
        private Task? _close;
        internal Task CloseAsync()
        {
            TaskCompletionSource<bool> start;
            lock (_gate)
            {
                if (_close is not null) return _close;
                start = new(TaskCreationOptions.RunContinuationsAsynchronously); _close = start.Task;
            }
            _ = CloseCoreAsync(start);
            return start.Task;
        }
        private async Task CloseCoreAsync(TaskCompletionSource<bool> completion)
        {
            Exception? failure = null;
            foreach (var child in Children)
                try { await child.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { Retain(ref failure, error); }
            if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
        }
    }

    private sealed class Owner
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string? Batch, string Id), Run> _runs = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly TaskCompletionSource<bool> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _registration;
        private bool _closed;
        private Task? _close;
        internal Owner(Agent parent)
        {
            try { _registration = parent.RegisterOwnedShutdownCallback((_, _) => new ValueTask(CloseAsync())); }
            catch { _shutdown.Dispose(); throw; }
            finally { _registered.TrySetResult(true); }
        }
        internal CancellationToken Token => _shutdown.Token;
        internal Run Enter(string? batch, ToolUseBlock call, InterruptState? pending, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_closed) throw new ObjectDisposedException(nameof(SwarmTool));
                var key = (batch, call.ToolUseId);
                if (!_runs.TryGetValue(key, out var run)) run = _runs.Values.FirstOrDefault(value => value.Key.Id == call.ToolUseId);
                if (run is not null)
                {
                    if (run.Active is not null) throw new InvalidOperationException("The swarm tool call is already executing.");
                    if (pending is null || !pending.Interrupts.Any(question => question.ToolUseId == call.ToolUseId && run.OuterInterruptIds.Contains(question.Id, StringComparer.Ordinal)))
                        throw new InvalidOperationException("The retained swarm belongs to a different interrupted tool call.");
                }
                else { run = new(key, call); _runs.Add(key, run); }
                if (run.Call.Name != call.Name || !JsonElement.DeepEquals(run.Call.Input, call.Input))
                    throw new InvalidOperationException("A retained swarm cannot change its original tool name or input.");
                run.Active = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return run;
            }
        }
        internal void Leave(Run run, bool pending, IReadOnlyList<string> emittedOuterIds)
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                run.OuterInterruptIds = emittedOuterIds;
                completion = run.Active!; run.Active = null;
                if (!pending) _runs.Remove(run.Key);
            }
            completion.TrySetResult(true);
        }
        private Task CloseAsync()
        {
            TaskCompletionSource<bool> start; Task[] active;
            lock (_gate)
            {
                if (_close is not null) return _close;
                _closed = true; start = new(TaskCreationOptions.RunContinuationsAsynchronously); _close = start.Task;
                active = _runs.Values.Where(run => run.Active is not null).Select(run => run.Active!.Task).ToArray();
            }
            _ = CloseCoreAsync(active, start);
            return start.Task;
        }
        private async Task CloseCoreAsync(Task[] active, TaskCompletionSource<bool> completion)
        {
            Exception? failure = null;
            await _registered.Task.ConfigureAwait(false);
            try { _shutdown.Cancel(); } catch (Exception error) { Retain(ref failure, error); }
            foreach (var task in active)
                try { await task.ConfigureAwait(false); } catch (Exception error) { Retain(ref failure, error); }
            Run[] pending;
            lock (_gate) { pending = _runs.Values.ToArray(); _runs.Clear(); }
            foreach (var run in pending)
                try { await run.CloseAsync().ConfigureAwait(false); } catch (Exception error) { Retain(ref failure, error); }
            try { _registration.Dispose(); } catch (Exception error) { Retain(ref failure, error); }
            _shutdown.Dispose();
            if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
        }
    }
}
