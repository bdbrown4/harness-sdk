namespace Strands.Agents;

/// <summary>Selects ordinary-Agent automatic latest checkpoint boundaries.</summary>
public enum SnapshotSessionSaveStrategy
{
    /// <summary>Saves latest after each invocation attempt.</summary>
    Invocation,
    /// <summary>Saves after accepted message events and again after invocation completion.</summary>
    Message,
    /// <summary>Writes automatically only when the checkpoint trigger fires or fails; redaction still forces latest.</summary>
    Trigger
}
