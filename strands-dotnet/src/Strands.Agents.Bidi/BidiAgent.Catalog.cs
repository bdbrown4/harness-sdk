using System.Text.Json;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    /// <summary>The stable catalog namespace within this agent's session.</summary>
    public string AgentId => _options.AgentId;

    internal void ValidateCatalogRestore() { RejectCallbackLifecycle(); lock (_gate) ValidateSnapshotLoad(); }
    internal static void ValidateCatalogSnapshot(AgentSnapshot snapshot) => _ = PrepareSnapshot(snapshot);
    internal AgentSnapshot CaptureCatalogSnapshot()
    {
        lock (_gate) return new(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(_messages.Select(Copy).ToArray(), SnapshotJson),
            ["state"] = JsonSerializer.SerializeToElement(State.GetAll(), SnapshotJson),
            ["systemPrompt"] = JsonSerializer.SerializeToElement(SystemPromptContent, SnapshotJson)
        });
    }
    internal async ValueTask<string?> WriteCatalogAsync(Func<AgentSnapshot, ValueTask<string?>> write, CancellationToken cancellationToken)
    {
        RejectSessionReentry(); cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { ThrowIfDisposed(); if (_explicitSaves++ == 0) _explicitSaveDrain = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var previous = SessionScope.Value; var previousScope = Scope.Value;
            SessionScope.Value = new(this, previous); Scope.Value = new(this, previousScope);
            try { return await write(CaptureCatalogSnapshot()).ConfigureAwait(false); }
            finally { SessionScope.Value = previous; Scope.Value = previousScope; _sessionGate.Release(); }
        }
        finally { lock (_gate) if (--_explicitSaves == 0) _explicitSaveDrain!.TrySetResult(true); }
    }
    private void RejectSessionReentry()
    {
        for (var scope = SessionScope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A session callback cannot await its own persistence.");
    }
}
