namespace Strands.Agents.MultiAgent;

public sealed partial class Graph
{
    private Task? _initializationTask;
    private bool _snapshotLoaded;

    /// <summary>Initializes plugins and the configured session phase once, memoizing success or failure. Snapshot profiles defer reading to BeforeInvocation. Requires an inactive graph.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Graph initialization requires an inactive orchestrator.");
        try { await InitializeCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref _active, 0); }
    }

    private Task InitializeCoreAsync(CancellationToken cancellationToken) => _initializationTask ??= InitializeImplementationAsync(cancellationToken);

    private async Task InitializeImplementationAsync(CancellationToken cancellationToken)
    {
        if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.Native)
            await RestoreSessionDuringInitializationAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in _options.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(plugin.Name)) throw new ArgumentException($"Duplicate multi-agent plugin name: {plugin.Name}.", nameof(GraphOptions.Plugins));
            await plugin.InitializeAsync(this, _hooks, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (_options.SessionRestoreProfile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot) WireSessionRestoreHook();
        await _hooks.InvokeAsync(new MultiAgentInitializedEvent(this), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SaveSessionAsync()
    {
        if (_options.SessionStore is { } store)
            await store.SaveAsync(_options.SessionId!, CreateSnapshot(new()), CancellationToken.None).ConfigureAwait(false);
    }
}
