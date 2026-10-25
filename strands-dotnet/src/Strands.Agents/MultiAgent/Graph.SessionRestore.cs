namespace Strands.Agents.MultiAgent;

public sealed partial class Graph
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
            _hooks.RegisterSessionInitialized((_, token) => RestoreSessionDuringInitializationAsync(token), beforeCaller: true);
        else if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.PythonSnapshot)
            _hooks.RegisterSessionInitialized((_, _) => { WireSessionRestoreHook(); return default; }, beforeCaller: true);
    }

    private async ValueTask RestoreSessionDuringInitializationAsync(CancellationToken cancellationToken)
    {
        if (_snapshotLoaded || _sessionRestored || _options.SessionStore is not { } store) return;
        var snapshot = await store.LoadAsync(_options.SessionId!, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null) ApplySnapshot(PrepareSessionSnapshot(snapshot), cancellationToken);
        else if (_options.SessionRestoreProfile != MultiAgentSessionRestoreProfile.Native) cancellationToken.ThrowIfCancellationRequested();
        _sessionRestored = true;
    }

    private PreparedCheckpoint PrepareSessionSnapshot(MultiAgentSnapshot snapshot)
    {
        var prepared = PrepareGraphSnapshot(snapshot);
        // Python deserialization resets terminal state before later session callbacks observe it.
        return prepared.Input is null && _options.SessionRestoreProfile is
            MultiAgentSessionRestoreProfile.PythonRepository or MultiAgentSessionRestoreProfile.PythonSnapshot
            ? prepared with { State = new MultiAgentState(Nodes.Keys) }
            : prepared;
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
            // The existing private path validates the entire snapshot before publishing any state.
            // Public inactive-only LoadSnapshotAsync admission remains unchanged.
            ApplySnapshot(PrepareSessionSnapshot(snapshot), cancellationToken);
            invocation.State.AdoptSessionState(State ?? new MultiAgentState(Nodes.Keys));
            State = invocation.State;
            if (_pendingState is not null) _pendingState = invocation.State;
        }
        _sessionRestored = true;
    }
}
