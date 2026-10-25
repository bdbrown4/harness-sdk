namespace Strands.Agents.Context;

/// <summary>An ordered context reduction policy. Built-in strategies are immutable and reusable.</summary>
public abstract class ContextStrategy
{
    /// <summary>A stable identifier for diagnostics.</summary>
    public abstract string Name { get; }
    /// <summary>Initializes an agent-specific hook, if the strategy needs one.</summary>
    public virtual ValueTask InitializeAsync(Agent agent, Stash? stash = null, CancellationToken cancellationToken = default) => default;
    /// <summary>Transforms the working conversation and returns true when it changed. Preserve complete tool exchanges.</summary>
    public abstract ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default);
}

/// <summary>A strategy's working conversation. Changes are validated before the manager commits them.</summary>
public sealed class ContextState
{
    internal ContextState(Agent agent, List<Message> messages, double utilization, bool overflow, Stash? stash = null)
    { Agent = agent; Messages = messages; Utilization = utilization; Overflow = overflow; Stash = stash; }
    /// <summary>The agent whose context is being reduced.</summary>
    public Agent Agent { get; }
    /// <summary>The ordered, mutable working conversation.</summary>
    public List<Message> Messages { get; }
    /// <summary>The estimated context use divided by the model's capacity; overflow may exceed one.</summary>
    public double Utilization { get; internal set; }
    /// <summary>Whether the provider rejected this context despite the local estimate.</summary>
    public bool Overflow { get; internal set; }
    /// <summary>The stash containing originals, or null when original-content persistence is disabled.</summary>
    public Stash? Stash { get; }
}

/// <summary>Configures context strategies and original-content persistence.</summary>
public sealed record ContextManagerOptions
{
    /// <summary>The default strategy profile and whether model-operated context tools are exposed. Defaults to Auto.</summary>
    public ContextMode Mode { get; init; } = ContextMode.Auto;
    /// <summary>An ordered pipeline. Null selects proactive truncation and summarization; an empty list selects only emergency recovery.</summary>
    public IReadOnlyList<ContextStrategy>? Strategies { get; init; }
    /// <summary>Whether originals are persisted for retrieval. Defaults to true.</summary>
    public bool StashEnabled { get; init; } = true;
    /// <summary>An explicit storage backend for originals. A namespaced view becomes the exact shared stash root.</summary>
    public Storage.IStorage? StashStorage { get; init; }
    /// <summary>Whether to expose retrieve_context when stashing is enabled.</summary>
    public bool RetrievalToolEnabled { get; init; } = true;
    /// <summary>Whether a backend error restoring original context aborts initialization. Defaults to TypeScript's Throw; Continue selects Python's best-effort restore.</summary>
    public StashRestoreFailurePolicy StashRestoreFailurePolicy { get; init; }
    /// <summary>Explicit model metadata for sizing, taking precedence over provider metadata.</summary>
    public ModelMetadata? ModelMetadata { get; init; }
}

/// <summary>Selects automatic policies or model-operated context management.</summary>
public enum ContextMode
{
    /// <summary>Proactively offload large tool results and summarize at eighty-five percent utilization.</summary>
    Auto,
    /// <summary>Expose context tools with a higher automatic offload threshold and summarize only at capacity.</summary>
    Agentic
}

/// <summary>How session restoration handles a failure writing original context to the stash backend.</summary>
public enum StashRestoreFailurePolicy
{
    /// <summary>Propagate the backend error, matching TypeScript.</summary>
    Throw,
    /// <summary>Warn and continue with restored conversation state, matching Python. Requested caller cancellation still propagates.</summary>
    Continue
}
