using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.MultiAgent;

/// <summary>A directed orchestration graph with explicit TypeScript and Python compatibility policies.</summary>
public sealed partial class Graph : IMultiAgent
{
    private readonly GraphOptions _options;
    private readonly MultiAgentHookRegistry _hooks;
    private readonly MultiAgentTelemetry _telemetry;
    private readonly Node[] _sources;
    private int _active;
    /// <summary>Copies and validates topology and scheduling options.</summary>
    public Graph(GraphOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.Id, nameof(options.Id));
        Guard.NotNull(options.Nodes, nameof(options.Nodes));
        Guard.NotNull(options.Edges, nameof(options.Edges));
        Guard.NotNull(options.Hooks, nameof(options.Hooks));
        Guard.NotNull(options.Plugins, nameof(options.Plugins));
        if (!Enum.IsDefined(typeof(MultiAgentSessionRestoreProfile), options.SessionRestoreProfile)) throw new ArgumentOutOfRangeException(nameof(options));
        var plugins = options.Plugins.ToArray();
        if (plugins.Any(plugin => plugin is null)) throw new ArgumentException("Plugins cannot contain null.", nameof(options));
        if ((options.SessionId is null) != (options.SessionStore is null)) throw new ArgumentException("Graph sessions require both an explicit SessionId and SessionStore.", nameof(options));
        if (options.SessionId is { } sessionId) Guard.NotNullOrWhiteSpace(sessionId, nameof(options.SessionId));
        if (options.MaxConcurrency < 1 || options.MaxSteps is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Graph execution bounds must be positive.");
        ValidateTimeout(options.Timeout, nameof(options.Timeout));
        ValidateTimeout(options.NodeTimeout, nameof(options.NodeTimeout));
        if (!Enum.IsDefined(typeof(DependencyPolicy), options.Dependencies) || !Enum.IsDefined(typeof(GraphScheduling), options.Scheduling)
            || !Enum.IsDefined(typeof(NodeFailurePolicy), options.NodeFailure) || !Enum.IsDefined(typeof(LimitOutcome), options.LimitOutcome)
            || !Enum.IsDefined(typeof(GraphInputFormat), options.InputFormat) || !Enum.IsDefined(typeof(AgentContextPolicy), options.AgentContext)
            || !Enum.IsDefined(typeof(MultiAgentSaveLatestStrategy), options.SaveLatestOn) || !Enum.IsDefined(typeof(GraphRestoredInputPolicy), options.RestoredInput)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown graph compatibility policy.");
        if (options.AgentContext == AgentContextPolicy.ResetToConstructionBaselineOnRevisit)
            throw new ArgumentOutOfRangeException(nameof(options), "Construction-baseline reset requires an explicit agent-node policy at GraphBuilder.AddNode.");
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in options.Nodes)
        {
            if (node is null) throw new ArgumentException("Graph nodes cannot contain null.", nameof(options));
            if (nodes.ContainsKey(node.Id)) throw new ArgumentException($"Duplicate graph node: {node.Id}.", nameof(options));
            if (node is AgentNode { Options.ContextPolicy: AgentContextPolicy.ResetToConstructionBaselineOnRevisit } agent && !agent.HasConstructionBaseline)
                throw new ArgumentException("Construction-baseline agent nodes must be added through GraphBuilder.AddNode(Agent).", nameof(options));
            nodes.Add(node.Id, node);
        }
        var edges = options.Edges.ToArray();
        foreach (var edge in edges)
            if (edge is null || !nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target)) throw new ArgumentException("Graph edges must reference known nodes.", nameof(options));
        var sourceIds = options.Sources?.ToArray() ?? nodes.Keys.Where(id => edges.All(edge => edge.Target != id)).ToArray();
        if (sourceIds.Length == 0 || sourceIds.Any(id => id is null || !nodes.ContainsKey(id)) || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length)
            throw new ArgumentException("Graph sources must be nonempty, distinct known node identifiers.", nameof(options));
        _sources = sourceIds.Select(id => nodes[id]).ToArray();
        var reachable = new HashSet<string>(sourceIds, StringComparer.Ordinal);
        var pending = new Queue<string>(sourceIds);
        while (pending.Count > 0)
        {
            var source = pending.Dequeue();
            foreach (var edge in edges.Where(edge => edge.Source == source))
                if (reachable.Add(edge.Target)) pending.Enqueue(edge.Target);
        }
        if (reachable.Count != nodes.Count) throw new ArgumentException("Every graph node must be reachable from a source.", nameof(options));
        Nodes = new ReadOnlyDictionary<string, Node>(nodes);
        _telemetry = new(options.Telemetry);
        _hooks = new(options.Hooks);
        _options = options with { Nodes = Array.AsReadOnly(options.Nodes.ToArray()), Edges = Array.AsReadOnly(edges), Sources = Array.AsReadOnly(sourceIds), Telemetry = _telemetry.Options, Plugins = Array.AsReadOnly(plugins) };
        Id = options.Id;
        ConfigureSessionRestoration();
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>The immutable node mapping.</summary>
    public IReadOnlyDictionary<string, Node> Nodes { get; }
    /// <summary>The copied edges in declaration order.</summary>
    public IReadOnlyList<Edge> Edges => _options.Edges;
    /// <summary>The copied graph configuration.</summary>
    public GraphOptions Options => _options;
    /// <summary>The registry used by this graph's awaited callbacks.</summary>
    public MultiAgentHookRegistry Hooks => _options.Hooks;
    /// <summary>The current or most recently completed invocation state.</summary>
    public MultiAgentState? State { get; private set; }
    /// <summary>Invokes the graph with text input.</summary>
    public Task<MultiAgentResult> InvokeAsync(string input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default) => InvokeAsync(new MultiAgentInput(input), options, cancellationToken);
    /// <inheritdoc />
    public async Task<MultiAgentResult> InvokeAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default)
    {
        MultiAgentResult? result = null;
        await foreach (var item in StreamAsync(input, options, cancellationToken).ConfigureAwait(false))
            if (item is MultiAgentResultEvent terminal) result = terminal.Result;
        return result ?? throw new InvalidOperationException("The graph stream did not produce a terminal result.");
    }
    private async IAsyncEnumerable<IMultiAgentStreamEvent> StreamCoreAsync(MultiAgentInput input, MultiAgentInvokeOptions? options, MultiAgentTelemetry.Invocation trace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guard.NotNull(input, nameof(input));
        var caller = options?.InvocationState ?? new Dictionary<string, object?>();
        Guard.NotNull(caller, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("The graph already has an active invocation.");
        var timer = Stopwatch.StartNew();
        Execution? execution = null;
        try
        {
            trace.Begin();
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            var deferred = DefersSessionRestore;
            var prepared = deferred ? null : PrepareRun(input);
            var state = State = prepared?.State ?? _pendingState ?? new MultiAgentState(Nodes.Keys);
            trace.State = state;
            if (prepared is not null && (prepared.AcceptedHookResponses || prepared.Ready.Any(node => state.Nodes[node.Id].DeferredResponses.Count > 0))) await SaveSessionAsync().ConfigureAwait(false);
            if (prepared is not null) execution = new(this, prepared.Input, state, caller, cancellationToken, prepared.Ready, prepared.Responses, prepared.Transitions ?? [], trace);
            Exception? primary = null;
            AfterMultiAgentInvocationEvent? after = null;
            var completed = false;
            try
            {
                var before = new BeforeMultiAgentInvocationEvent(this, state, caller);
                try
                {
                    if (deferred) _sessionRestoreInvocation = before;
                    await _hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false);
                    _sessionRestoreInvocation = null;
                    if (deferred)
                    {
                        prepared = PrepareRun(input, _options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot ? state : null);
                        if (!ReferenceEquals(prepared.State, state)) state.AdoptSessionState(prepared.State);
                        State = state;
                        if (_pendingState is not null) _pendingState = state;
                        prepared = prepared with { State = state };
                        if (prepared.AcceptedHookResponses || prepared.Ready.Any(node => state.Nodes[node.Id].DeferredResponses.Count > 0)) await SaveSessionAsync().ConfigureAwait(false);
                        execution = new(this, prepared.Input, state, caller, cancellationToken, prepared.Ready, prepared.Responses, prepared.Transitions ?? [], trace);
                    }
                    trace.Begin(afterBeforeHook: true);
                }
                catch (Exception error) { primary = error; throw; }
                var activeExecution = execution ?? throw new InvalidOperationException("The graph invocation was not prepared.");
                yield return before;
                while (true)
                {
                    EventQueue.Pending? update;
                    try { update = await activeExecution.NextAsync().ConfigureAwait(false); }
                    catch (GraphLimitException error) when (_options.LimitOutcome == LimitOutcome.FailedResult) { primary = error; update = null; }
                    catch (Exception error) { primary = error; throw; }
                    if (update is null) { completed = primary is null; break; }
                    try { if (!update.HooksInvoked) await _hooks.InvokeAsync(update.Event!, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) { primary = error; throw; }
                    yield return update.Event!;
                    update.Acknowledge();
                }
            }
            finally
            {
                if (execution is not null)
                {
                    var failure = primary;
                    try { await execution.CloseAsync(primary).ConfigureAwait(false); }
                    catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    RetainPending(state, prepared!.Input, execution.PendingTargets, execution.PendingTransitions);
                    trace.EndBeforeAfter(failure, completed);
                    after = new(this, state, caller, failure);
                    try { await _hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    try
                    {
                        if (_pendingState is null && execution.RecoveryCheckpoint is { } recovery)
                        {
                            var checkpoint = PrepareGraphSnapshot(recovery);
                            _pendingState = checkpoint.State; _pendingInput = checkpoint.Input; _pendingTargets = checkpoint.Targets; _pendingTransitions = checkpoint.Transitions;
                        }
                    }
                    catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    try { await SaveSessionAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    if (primary is GraphLimitException limit) limit.Result = BuildResult(state, timer.Elapsed, limit);
                    if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            yield return after!;
            var terminal = new MultiAgentResultEvent(BuildResult(state, timer.Elapsed, primary), caller);
            await _hooks.InvokeAsync(terminal, cancellationToken).ConfigureAwait(false);
            yield return terminal;
        }
        finally
        {
            try { execution?.Dispose(); }
            finally { _sessionRestoreInvocation = null; Volatile.Write(ref _active, 0); }
        }
    }

    private MultiAgentResult BuildResult(MultiAgentState state, TimeSpan duration, Exception? error)
    {
        foreach (var pair in state.Nodes) pair.Value.Terminus = pair.Value.Status is Status.Failed or Status.Cancelled or Status.Interrupted || pair.Value.Status == Status.Completed && !_options.Edges.Any(edge => edge.Source == pair.Key && state.Nodes[edge.Target].Status != Status.Pending);
        return new(state.Results, _options.Nodes.Select(node => state.Nodes[node.Id]).Where(node => node.Terminus).SelectMany(node => node.Content).ToArray(), duration, error is null ? null : Status.Failed, error, state.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray());
    }

    private static void ValidateTimeout(TimeSpan? timeout, string parameterName)
    {
        if (timeout is { } value && (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(parameterName, "A deadline must be positive and at most Int32.MaxValue milliseconds.");
    }

    private async ValueTask<MultiAgentInput> ResolveInputAsync(Node node, MultiAgentInput original, MultiAgentState state, IReadOnlyDictionary<string, object?> caller, CancellationToken cancellationToken)
    {
        var dependencies = new List<(string Id, IReadOnlyList<ContentBlock> Content, NodeResult? Result)>();
        foreach (var edge in _options.Edges.Where(edge => edge.Target == node.Id))
        {
            if (_options.Dependencies == DependencyPolicy.AnySatisfied || _options.InputFormat == GraphInputFormat.Python)
            {
                if (state.Nodes[edge.Source].Status != Status.Completed || edge.Condition is { } condition && !await condition(state, caller, cancellationToken).ConfigureAwait(false)) continue;
            }
            if (_options.InputFormat == GraphInputFormat.Python && dependencies.Any(value => value.Id == edge.Source)) continue;
            var source = state.Nodes[edge.Source].Content.Select(ProjectDependencyBlock).Where(block => block is not null).Cast<ContentBlock>().ToArray();
            var result = _options.InputFormat == GraphInputFormat.Python ? state.Nodes[edge.Source].Results.LastOrDefault() : null;
            if (source.Length == 0 && result?.AgentResult is null && result?.MultiAgentResult is null) continue;
            dependencies.Add((edge.Source, source, result));
        }
        if (dependencies.Count == 0) return original;
        var content = new List<ContentBlock>();
        if (_options.InputFormat == GraphInputFormat.Typed)
        {
            content.AddRange(original.Content);
            foreach (var dependency in dependencies) { content.Add(new TextBlock($"[node: {dependency.Id}]")); content.AddRange(dependency.Content); }
        }
        else
        {
            content.Add(new TextBlock(original.IsTextInput ? $"Original Task: {original.Text}" : "Original Task:"));
            if (!original.IsTextInput) content.AddRange(original.Content);
            content.Add(new TextBlock("\nInputs from previous nodes:"));
            foreach (var dependency in dependencies)
            {
                content.Add(new TextBlock($"\nFrom {dependency.Id}:"));
                if (dependency.Result is { AgentResult: not null } or { MultiAgentResult: not null })
                {
                    foreach (var result in PythonAgentResults(dependency.Result))
                        content.Add(new TextBlock($"  - Agent: {AgentResultRendering.Render(result, AgentResultOutputProfile.Python)}"));
                }
                else
                {
                    var name = Nodes[dependency.Id] is AgentNode agent ? agent.Agent.Name : "Agent";
                    var text = new Message(Role.Assistant, dependency.Content).Text;
                    content.Add(new TextBlock($"  - {name}: {text}"));
                }
            }
        }
        return new(content);
    }

    private static IEnumerable<AgentResult> PythonAgentResults(NodeResult result)
        => result.GetAgentResults();

    private static bool IsReasoning(ContentBlock block) => block is ReasoningBlock or RedactedReasoningBlock or Models.ResponsesReasoningBlock
        || block is Models.GoogleContentBlock google && google.Part.TryGetProperty("thought", out var thought) && thought.ValueKind == System.Text.Json.JsonValueKind.True;

    private static ContentBlock? ProjectDependencyBlock(ContentBlock block) => block switch
    {
        _ when IsReasoning(block) => null,
        Models.AnthropicContentBlock anthropic => anthropic.Content.GetProperty("type").GetString() == "text" ? new TextBlock(anthropic.Text) : null,
        CitationsBlock citations => new TextBlock(citations.Text),
        _ => block
    };
}
