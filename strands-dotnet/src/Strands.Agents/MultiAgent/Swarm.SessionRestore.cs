namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    private bool _sessionRestored;
    private bool _sessionRestoreHookWired;
    private BeforeMultiAgentInvocationEvent? _sessionRestoreInvocation;
    private bool DefersSessionRestore => _options.SessionStore is not null &&
        _options.SessionRestoreProfile is MultiAgentSessionRestoreProfile.TypeScriptSnapshot or MultiAgentSessionRestoreProfile.PythonSnapshot;

    private void ConfigureSessionRestoration()
    {
        if (_options.SessionStore is null) return;
        if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.PythonRepository)
            _hooks.RegisterSessionInitialized((_, token) => RestoreSessionDuringInitializationAsync(token), beforeCaller: false);
        else if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.PythonSnapshot)
            _hooks.RegisterSessionInitialized((_, _) => { WireSessionRestoreHook(); return default; }, beforeCaller: false);
    }

    private async ValueTask RestoreSessionDuringInitializationAsync(CancellationToken cancellationToken)
    {
        if (_snapshotLoaded || _sessionRestored || _options.SessionStore is not { } store) return;
        var snapshot = await store.LoadAsync(_options.SessionId!, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null) ApplySnapshot(PrepareSessionSnapshot(snapshot), cancellationToken);
        else if (_options.SessionRestoreProfile != MultiAgentSessionRestoreProfile.Native) cancellationToken.ThrowIfCancellationRequested();
        _sessionRestored = true;
    }

    private PreparedSnapshot PrepareSessionSnapshot(MultiAgentSnapshot snapshot)
    {
        var prepared = PrepareSnapshot(snapshot);
        if (prepared.PendingInput is not null || _options.SessionRestoreProfile is not
            (MultiAgentSessionRestoreProfile.PythonRepository or MultiAgentSessionRestoreProfile.PythonSnapshot))
            return prepared;

        // Python's terminal reset clears run state and knowledge, not this instance's reset baselines.
        return prepared with
        {
            State = new MultiAgentState(Nodes.Keys),
            Baselines = new Dictionary<string, AgentSnapshot>(_baselines, StringComparer.Ordinal),
            Shared = new SharedContext().GetAll(),
            ToolTurns = new Dictionary<string, ToolTurn>(StringComparer.Ordinal),
            ElapsedExecutionTicks = 0
        };
    }

    private void WireSessionRestoreHook()
    {
        if (_sessionRestoreHookWired || _options.SessionStore is null) return;
        _hooks.Register<BeforeMultiAgentInvocationEvent>(RestoreSessionBeforeInvocationAsync);
        _sessionRestoreHookWired = true;
    }

    private async ValueTask RestoreSessionBeforeInvocationAsync(BeforeMultiAgentInvocationEvent invocation, CancellationToken cancellationToken)
    {
        if (_sessionRestored) return;
        if (!ReferenceEquals(invocation, _sessionRestoreInvocation) || !ReferenceEquals(invocation.Orchestrator, this)
            || !ReferenceEquals(invocation.State, State) || Volatile.Read(ref _active) != 1)
            throw new InvalidOperationException("Session restoration requires the originating active invocation boundary.");
        if (_snapshotLoaded) { _sessionRestored = true; return; }
        var snapshot = await _options.SessionStore!.LoadAsync(_options.SessionId!, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not null)
        {
            // Parsing, topology, baselines, knowledge and pending frontier validation finish before commit.
            var prepared = PrepareSessionSnapshot(snapshot);
            ApplySnapshot(prepared, cancellationToken);
            invocation.State.AdoptSessionState(State ?? new MultiAgentState(Nodes.Keys));
            State = invocation.State;
            if (_pendingState is not null) _pendingState = invocation.State;
        }
        _sessionRestored = true;
    }

    private async ValueTask CaptureResetBaselineAsync(AgentNode node, CancellationToken cancellationToken)
    {
        if ((node.Options.ContextPolicy ?? _options.AgentContext) == AgentContextPolicy.ResetOnRevisit && !_baselines.ContainsKey(node.Id))
            _baselines[node.Id] = await node.Agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }, cancellationToken).ConfigureAwait(false);
    }
}
