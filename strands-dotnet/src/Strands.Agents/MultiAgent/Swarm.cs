using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.MultiAgent;

/// <summary>A sequential agent swarm whose agents choose a handoff target or a final answer.</summary>
public sealed partial class Swarm : IMultiAgent
{
    private readonly SwarmOptions _options;
    private readonly MultiAgentHookRegistry _hooks;
    private readonly MultiAgentTelemetry _telemetry;
    private readonly AgentNode _start;
    private int _active;
    private Task? _initialization;
    private readonly Dictionary<string, AgentSnapshot> _baselines = new(StringComparer.Ordinal);
    private Stopwatch? _executionWatch;
    /// <summary>Creates and initializes a swarm, capturing reset baselines before returning it to the caller.</summary>
    public static async Task<Swarm> CreateAsync(SwarmOptions options, CancellationToken cancellationToken = default)
    {
        var swarm = new Swarm(options);
        await swarm.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return swarm;
    }
    /// <summary>Initializes plugins, the configured session phase and reset baselines once. Snapshot profiles defer reading to BeforeInvocation. Requires an inactive swarm and memoizes success or failure.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Swarm initialization requires an inactive orchestrator.");
        try { await (_initialization ??= InitializeImplementationAsync(cancellationToken)).ConfigureAwait(false); }
        finally { Volatile.Write(ref _active, 0); }
    }
    /// <summary>Copies and validates borrowed nodes. Reset baselines are captured during first initialization; CreateAsync captures them before returning.</summary>
    public Swarm(SwarmOptions options)
    {
        Guard.NotNull(options, nameof(options)); Guard.NotNullOrWhiteSpace(options.Id, nameof(options.Id));
        Guard.NotNull(options.Nodes, nameof(options.Nodes)); Guard.NotNull(options.Hooks, nameof(options.Hooks));
        Guard.NotNull(options.Plugins, nameof(options.Plugins));
        if (!Enum.IsDefined(typeof(MultiAgentSessionRestoreProfile), options.SessionRestoreProfile) || !Enum.IsDefined(typeof(SwarmBaselineCapturePhase), options.BaselineCapturePhase)) throw new ArgumentOutOfRangeException(nameof(options));
        var plugins = options.Plugins.ToArray();
        if (plugins.Any(plugin => plugin is null)) throw new ArgumentException("Plugins cannot contain null.", nameof(options));
        if (!Enum.IsDefined(typeof(AgentContextPolicy), options.AgentContext) || options.AgentContext == AgentContextPolicy.ResetToConstructionBaselineOnRevisit)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(typeof(SwarmRoutingMode), options.Routing)) throw new ArgumentOutOfRangeException(nameof(options.Routing));
        if (!Enum.IsDefined(typeof(SwarmRepetitionScope), options.RepetitionScope)) throw new ArgumentOutOfRangeException(nameof(options.RepetitionScope));
        if (!Enum.IsDefined(typeof(SwarmTimeoutScope), options.TimeoutScope)) throw new ArgumentOutOfRangeException(nameof(options.TimeoutScope));
        if (!Enum.IsDefined(typeof(SwarmContextCommit), options.ContextCommit)) throw new ArgumentOutOfRangeException(nameof(options.ContextCommit));
        if (!Enum.IsDefined(typeof(SwarmContextTextProfile), options.ContextTextProfile)) throw new ArgumentOutOfRangeException(nameof(options.ContextTextProfile));
        if (!Enum.IsDefined(typeof(LimitOutcome), options.LimitOutcome) || !Enum.IsDefined(typeof(SwarmFailurePolicy), options.FailurePolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(typeof(MultiAgentSaveLatestStrategy), options.SaveLatestOn) || !Enum.IsDefined(typeof(GraphRestoredInputPolicy), options.RestoredInput)) throw new ArgumentOutOfRangeException(nameof(options));
        if ((options.SessionId is null) != (options.SessionStore is null)) throw new ArgumentException("Swarm session storage requires both SessionId and SessionStore.", nameof(options));
        if (options.SessionId is { } sessionId) Guard.NotNullOrWhiteSpace(sessionId, nameof(options.SessionId));
        if (options.MaxSteps is <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxSteps));
        if (options.MaxHandoffs is <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxHandoffs));
        ValidateTimeout(options.Timeout, nameof(options.Timeout)); ValidateTimeout(options.NodeTimeout, nameof(options.NodeTimeout));
        var nodes = new Dictionary<string, AgentNode>(StringComparer.Ordinal);
        foreach (var node in options.Nodes)
        {
            if (node is null || nodes.ContainsKey(node.Id)) throw new ArgumentException("Swarm nodes require distinct nonnull identities.", nameof(options));
            if (node.Options.ContextPolicy == AgentContextPolicy.ResetToConstructionBaselineOnRevisit)
                throw new ArgumentException("Construction-baseline reset is supported only by explicitly configured graph agent nodes.", nameof(options));
            nodes.Add(node.Id, node);
        }
        if (nodes.Count == 0) throw new ArgumentException("A swarm requires at least one agent.", nameof(options));
        if (options.RepetitiveHandoffDetectionWindow < 0 || options.RepetitiveHandoffMinUniqueAgents < 0 ||
            (options.RepetitiveHandoffDetectionWindow == 0) != (options.RepetitiveHandoffMinUniqueAgents == 0) ||
            options.RepetitiveHandoffMinUniqueAgents > options.RepetitiveHandoffDetectionWindow || options.RepetitiveHandoffMinUniqueAgents > nodes.Count)
            throw new ArgumentOutOfRangeException(nameof(options), "Repetitive handoff detection requires both settings zero or a positive window and a distinct-agent threshold bounded by the window and node count.");
        if (options.Start is { } start && !nodes.ContainsKey(start)) throw new ArgumentException("The swarm start must identify a declared agent.", nameof(options));
        _telemetry = new(options.Telemetry);
        _hooks = new(options.Hooks);
        _options = options with { Nodes = Array.AsReadOnly(options.Nodes.ToArray()), Telemetry = _telemetry.Options, Plugins = Array.AsReadOnly(plugins) };
        Nodes = new ReadOnlyDictionary<string, AgentNode>(nodes);
        _start = options.Start is { } id ? nodes[id] : options.Nodes[0];
        Id = options.Id;
        ConfigureSessionRestoration();
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>The borrowed agents keyed by their declared node identities.</summary>
    public IReadOnlyDictionary<string, AgentNode> Nodes { get; }
    /// <summary>The copied swarm configuration.</summary>
    public SwarmOptions Options => _options;
    /// <summary>The awaited callback registry.</summary>
    public MultiAgentHookRegistry Hooks => _options.Hooks;
    /// <summary>The current or most recently completed execution state.</summary>
    public MultiAgentState? State { get; private set; }
    /// <summary>JSON knowledge from accepted tool-mode coordination results, committed according to ContextCommit and scoped to this swarm.</summary>
    public SharedContext SharedContext { get; } = new();
    /// <summary>Invokes the swarm with text input.</summary>
    public Task<MultiAgentResult> InvokeAsync(string input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default) => InvokeAsync(new MultiAgentInput(input), options, cancellationToken);
    /// <inheritdoc />
    public async Task<MultiAgentResult> InvokeAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default)
    {
        MultiAgentResult? result = null;
        await foreach (var item in StreamAsync(input, options, cancellationToken).ConfigureAwait(false)) if (item is MultiAgentResultEvent terminal) result = terminal.Result;
        return result ?? throw new InvalidOperationException("The swarm completed without a terminal result.");
    }
    private async IAsyncEnumerable<IMultiAgentStreamEvent> StreamObservedAsync(MultiAgentInput input, MultiAgentInvokeOptions? options, MultiAgentTelemetry.Invocation trace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var run = new InvocationRun();
        var iterator = StreamCoreAsync(input, options, run, trace, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        MultiAgentResult? failed = null;
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    primary = error;
                    var soft = error is SwarmLimitException ? _options.LimitOutcome == LimitOutcome.FailedResult : _options.FailurePolicy == SwarmFailurePolicy.FailedResult;
                    if (!run.Entered || !soft || cancellationToken.IsCancellationRequested) throw;
                    failed = error is SwarmLimitException { Result: { } partial } ? partial : BuildResult(State!, run.Elapsed, error);
                    break;
                }
                if (!moved) break;
                yield return iterator.Current;
            }
        }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { if (primary is null) throw; AttachCleanup(primary, cleanup); }
        }
        if (failed is not null)
        {
            var terminal = new MultiAgentResultEvent(failed, options?.InvocationState ?? new Dictionary<string, object?>());
            await _hooks.InvokeAsync(terminal, cancellationToken).ConfigureAwait(false);
            yield return terminal;
        }
    }

    private async IAsyncEnumerable<IMultiAgentStreamEvent> StreamCoreAsync(MultiAgentInput input, MultiAgentInvokeOptions? options, InvocationRun run, MultiAgentTelemetry.Invocation trace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guard.NotNull(input, nameof(input));
        var caller = options?.InvocationState ?? new Dictionary<string, object?>(); Guard.NotNull(caller, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("The swarm already has an active invocation.");
        var timer = Stopwatch.StartNew();
        Exception? primary = null;
        Exception? softStop = null;
        MultiAgentState? state = null;
        AfterMultiAgentInvocationEvent? after = null;
        var completed = false;
        try
        {
            trace.Begin();
            await (_initialization ??= InitializeImplementationAsync(cancellationToken)).ConfigureAwait(false);
            var deferred = DefersSessionRestore;
            var prepared = deferred
                ? (State: _pendingState ?? new MultiAgentState(Nodes.Keys), Original: input, Node: _start, NodeInput: input, AcceptedResponses: false)
                : PrepareRun(input);
            state = State = prepared.State;
            trace.State = state;
            input = prepared.Original;
            var node = prepared.Node;
            var nextInput = prepared.NodeInput;
            if (!deferred)
            {
                _pendingInput = input;
                _pendingNode = node.Id;
                if (!nextInput.IsResume) _pendingNodeInput = nextInput;
                if (_options.SessionStore is not null) _pendingState = state;
                run.Entered = true;
            }
            try
            {
                var before = new BeforeMultiAgentInvocationEvent(this, state, caller);
                try
                {
                    foreach (var baseline in _baselines) state.Nodes[baseline.Key].BaselineSnapshot = baseline.Value;
                    if (!deferred && prepared.AcceptedResponses) await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
                    if (deferred) _sessionRestoreInvocation = before;
                    await _hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false);
                    _sessionRestoreInvocation = null;
                    if (deferred)
                    {
                        prepared = PrepareRun(input, _options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot ? state : null);
                        if (!ReferenceEquals(prepared.State, state)) state.AdoptSessionState(prepared.State);
                        State = state;
                        input = prepared.Original;
                        node = prepared.Node;
                        nextInput = prepared.NodeInput;
                        _pendingInput = input;
                        _pendingNode = node.Id;
                        if (!nextInput.IsResume) _pendingNodeInput = nextInput;
                        if (_options.SessionStore is not null) _pendingState = state;
                        foreach (var baseline in _baselines) state.Nodes[baseline.Key].BaselineSnapshot = baseline.Value;
                        if (prepared.AcceptedResponses) await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
                        run.Entered = true;
                    }
                    trace.Begin(afterBeforeHook: true);
                }
                catch (Exception error) { primary = error; throw; }
                yield return before;
                var remaining = _options.Timeout;
                if (_options.TimeoutScope == SwarmTimeoutScope.Execution && remaining is { } configured)
                {
                    if (_elapsedExecutionTicks >= configured.Ticks) { primary = new SwarmLimitException("timeout", "The swarm exhausted its accumulated execution-time budget."); throw primary; }
                    remaining = TimeSpan.FromTicks(configured.Ticks - _elapsedExecutionTicks);
                }
                _executionWatch = Stopwatch.StartNew();
                using var deadline = StartDeadline(remaining);
                using var execution = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                var executionToken = execution?.Token ?? cancellationToken;
                var completedHistory = _options.RepetitionScope == SwarmRepetitionScope.Execution
                    ? state.Results.Where(result => result.Status == Status.Completed).Select(result => result.NodeId).ToList() : [];
                var completedVisits = state.Results.Count(result => result.Status == Status.Completed);
                while (true)
                {
                    if (executionToken.IsCancellationRequested)
                    { primary = ClassifyDeadline(new OperationCanceledException(executionToken), cancellationToken, deadline, "timeout"); throw primary; }
                    if (!state.Nodes[node.Id].IsResuming && _options.MaxSteps is { } maximum && state.Steps >= maximum)
                    {
                        primary = new SwarmLimitException("steps", $"The swarm exhausted its {maximum} node-visit budget while a handoff was pending.");
                        throw primary;
                    }
                    if (_options.MaxHandoffs is { } maxHandoffs && completedVisits >= maxHandoffs)
                    { primary = new SwarmLimitException("handoffs", $"The swarm exhausted its {maxHandoffs} completed-visit budget while a handoff was pending."); throw primary; }
                    var window = _options.RepetitiveHandoffDetectionWindow;
                    if (window > 0 && completedHistory.Count >= window)
                    {
                        var unique = completedHistory.Skip(completedHistory.Count - window).Distinct(StringComparer.Ordinal).Count();
                        if (unique < _options.RepetitiveHandoffMinUniqueAgents)
                        { softStop = new InvalidOperationException($"Repetitive handoff: {unique} unique nodes out of {window} recent iterations."); break; }
                    }
                    if (!state.Nodes[node.Id].IsResuming) state.Steps = checked(state.Steps + 1);
                    _pendingInput = input;
                    _pendingNode = node.Id;
                    if (!nextInput.IsResume) _pendingNodeInput = nextInput;
                    NodeResult? result = null;
                    var iterator = StreamNodeAsync(node, nextInput, state, caller, trace, executionToken).GetAsyncEnumerator(executionToken);
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                            catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "timeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                            if (!moved) break;
                            if (iterator.Current is NodeResultEvent terminal) result = terminal.Result;
                            yield return iterator.Current;
                        }
                    }
                    finally
                    {
                        try { await iterator.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) { if (primary is null) { primary = cleanup; throw; } AttachCleanup(primary, cleanup); }
                    }
                    if (result is null) throw new InvalidOperationException("A swarm visit completed without a node result.");
                    if (result.Status == Status.Completed) { completedHistory.Add(result.NodeId); completedVisits = checked(completedVisits + 1); }
                    if (result.Status != Status.Completed || result.StructuredOutput is not { } output || !output.TryGetProperty("agentId", out var target)) break;
                    node = Nodes[target.GetString()!];
                    nextInput = _options.Routing == SwarmRoutingMode.Tool ? ToolInput(input, node.Id, state, output.GetProperty("message").GetString()) : HandoffInput(output);
                    var handoff = new MultiAgentHandoffEvent([result.NodeId], [node.Id], state, caller);
                    try { await _hooks.InvokeAsync(handoff, executionToken).ConfigureAwait(false); }
                    catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "timeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                    yield return handoff;
                }
                completed = true;
            }
            finally
            {
                if (run.Entered)
                {
                    _executionWatch?.Stop(); _elapsedExecutionTicks = CurrentExecutionTicks(); _executionWatch = null;
                    if (state.Nodes.Values.Any(value => value.HasPendingExecution)) _pendingState = state;
                    else
                    {
                        if (_options.SessionStore is null) ClearPending();
                        else foreach (var key in _toolTurns.Keys.Where(key => !state.Nodes[key].HasPendingExecution).ToArray()) _toolTurns.Remove(key);
                    }
                    var failure = primary;
                    trace.EndBeforeAfter(failure, completed);
                    after = new(this, state, caller, primary);
                    try { await _hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    if (_restoreInitialized)
                        try { await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    if (primary is SwarmLimitException limit) limit.Result = BuildResult(state, timer.Elapsed, limit);
                    if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            yield return after ?? throw new InvalidOperationException("The swarm invocation did not produce its after event.");
            var terminalEvent = new MultiAgentResultEvent(BuildResult(state, timer.Elapsed, softStop), caller);
            await _hooks.InvokeAsync(terminalEvent, cancellationToken).ConfigureAwait(false);
            yield return terminalEvent;
        }
        finally { _sessionRestoreInvocation = null; run.Elapsed = timer.Elapsed; Volatile.Write(ref _active, 0); }
    }

    private sealed class InvocationRun { internal bool Entered { get; set; } internal TimeSpan Elapsed { get; set; } }

    private MultiAgentResult BuildResult(MultiAgentState state, TimeSpan elapsed, Exception? failure = null)
    {
        var last = state.Results.LastOrDefault();
        foreach (var node in state.Nodes.Values) node.Terminus = false;
        if (last is not null) state.Nodes[last.NodeId].Terminus = true;
        var content = _options.Routing == SwarmRoutingMode.Structured && last?.StructuredOutput is { } structured && structured.GetProperty("message").GetString() is { Length: > 0 } message ? new ContentBlock[] { new TextBlock(message) } : last?.Content ?? [];
        return new(state.Results, content, _options.TimeoutScope == SwarmTimeoutScope.Execution ? TimeSpan.FromTicks(CurrentExecutionTicks()) : elapsed, failure is null ? null : Status.Failed, failure, state.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray());
    }

    private long CurrentExecutionTicks()
    {
        var current = _executionWatch?.Elapsed.Ticks ?? 0;
        return _elapsedExecutionTicks > long.MaxValue - current ? long.MaxValue : _elapsedExecutionTicks + current;
    }

    private async Task InitializeImplementationAsync(CancellationToken cancellationToken)
    {
        if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.Native)
            await RestoreSessionDuringInitializationAsync(cancellationToken).ConfigureAwait(false);
        foreach (var node in _options.Nodes)
        {
            if (_options.Routing == SwarmRoutingMode.Tool)
            {
                await node.Agent.InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (node.Agent.Tools.Any(tool => node.Agent.RegisteredToolSpec(tool).Name == HandoffTool.Name)) throw new ArgumentException($"Swarm agent '{node.Id}' already registers reserved tool '{HandoffTool.Name}'.");
            }
            if (_options.BaselineCapturePhase == SwarmBaselineCapturePhase.BeforePlugins)
                await CaptureResetBaselineAsync(node, cancellationToken).ConfigureAwait(false);
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in _options.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(plugin.Name)) throw new ArgumentException($"Duplicate multi-agent plugin name: {plugin.Name}.", nameof(SwarmOptions.Plugins));
            await plugin.InitializeAsync(this, _hooks, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (_options.BaselineCapturePhase == SwarmBaselineCapturePhase.AfterPlugins)
            foreach (var node in _options.Nodes) await CaptureResetBaselineAsync(node, cancellationToken).ConfigureAwait(false);
        if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot) WireSessionRestoreHook();
        await _hooks.InvokeAsync(new MultiAgentInitializedEvent(this), cancellationToken).ConfigureAwait(false);
        _restoreInitialized = true;
    }

    private static MultiAgentInput HandoffInput(JsonElement handoff)
    {
        var content = new List<ContentBlock> { new TextBlock(handoff.GetProperty("message").GetString()!) };
        if (handoff.TryGetProperty("context", out var context)) content.Add(new TextBlock("Context:\n" + JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true })));
        return new(content);
    }

    private StructuredOutputOptions<JsonElement> HandoffSchema(string current)
    {
        var targets = _options.Nodes.Where(node => node.Id != current).ToArray();
        var properties = new Dictionary<string, object?>
        {
            ["message"] = new { type = "string", description = "Instructions for the next agent, or the final response when no handoff is requested." },
            ["context"] = new { type = "object", description = "Structured data to pass to the next agent." },
            ["agentId"] = targets.Length == 0 ? false : new { type = "string", @enum = targets.Select(node => node.Id).ToArray(), description = "Target agent. Omit to finish.\nAvailable agents:\n" + string.Join("\n", targets.Select(node => "- " + node.Id + (node.Description is { } description ? ": " + description : string.Empty))) }
        };
        return new() { Schema = JsonSerializer.SerializeToElement(new { type = "object", properties, required = new[] { "message" }, additionalProperties = false }) };
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }

    private static void ValidateTimeout(TimeSpan? timeout, string name)
    { if (timeout is { } value && (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(name); }

    private static CancellationTokenSource? StartDeadline(TimeSpan? timeout)
    {
        if (timeout is not { } value) return null;
        var deadline = new CancellationTokenSource(); deadline.CancelAfter(value); return deadline;
    }

    private static Exception ClassifyDeadline(Exception error, CancellationToken external, CancellationTokenSource? deadline, string budget)
    {
        Exception classified;
        if (external.IsCancellationRequested && error is OperationCanceledException)
            classified = error is OperationCanceledException cancellation && cancellation.CancellationToken == external ? error : new OperationCanceledException(error.Message, error, external);
        else if (!external.IsCancellationRequested && deadline?.IsCancellationRequested == true)
            classified = new SwarmLimitException(budget, $"The swarm exceeded its {budget} wall-clock budget.");
        else return error;
        if (!ReferenceEquals(classified, error))
        {
            if (error is not OperationCanceledException) AttachCleanup(classified, error);
            if (error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> cleanups)
                foreach (var cleanup in cleanups) AttachCleanup(classified, cleanup);
        }
        return classified;
    }
}
