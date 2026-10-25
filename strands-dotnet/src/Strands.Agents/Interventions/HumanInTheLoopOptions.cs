using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>A classification of whether a concrete tool call requires a human response.</summary>
/// <param name="RequiresHumanInTheLoop">Whether to request approval before executing the tool.</param>
/// <param name="Reason">Optional explanation shown in the approval prompt.</param>
public sealed record HumanInTheLoopClassifierResult(bool RequiresHumanInTheLoop, string? Reason = null);

/// <summary>Human approval, optional session trust, and argument-level classification settings.</summary>
public sealed record HumanInTheLoopOptions
{
    /// <summary>Names allowed without approval. The wildcard * allows all; !name always requires approval and cannot be trusted.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    /// <summary>An optional awaited classifier for calls not covered by the allowlist or session trust. Failures require approval.</summary>
    public Func<BeforeToolCallEvent, CancellationToken, ValueTask<HumanInTheLoopClassifierResult>>? Classifier { get; init; }
    /// <summary>Enables t/trust responses to approve a tool and remember its name in agent state. Defaults to false.</summary>
    public bool EnableTrust { get; init; }
    /// <summary>Optional trust evaluator; defaults to trimmed case-insensitive t/trust strings.</summary>
    public Func<JsonElement, bool>? EvaluateTrust { get; init; }
    /// <summary>Optional approval evaluator; defaults to Confirm.DefaultEvaluate.</summary>
    public Func<JsonElement, bool>? Evaluate { get; init; }
    /// <summary>Optional inline prompt callback. Without it the invocation pauses. A missing callback response denies; JSON null is an explicit declined response.</summary>
    public Func<string, CancellationToken, ValueTask<JsonElement?>>? Ask { get; init; }
}
