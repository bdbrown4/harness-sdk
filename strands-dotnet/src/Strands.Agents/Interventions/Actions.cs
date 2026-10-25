using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>A policy decision applied at a supported agent lifecycle boundary.</summary>
public interface IInterventionAction;

/// <summary>Allows the operation to continue. Reason is diagnostic metadata, excluded from model input.</summary>
public sealed record Proceed(string? Reason = null) : Steering.IToolSteeringAction, Steering.IModelSteeringAction;

/// <summary>Denies a before-invocation, before-tool, or before-model operation with a model-visible reason.</summary>
public sealed record Deny(string Reason) : IInterventionAction;

/// <summary>Accumulates guidance tagged with its handler name. After-model guidance retries before committing the rejected response.</summary>
public sealed record Guide(string Feedback, string? Reason = null) : Steering.IToolSteeringAction, Steering.IModelSteeringAction;

/// <summary>Requests human approval before a tool call. A supplied JSON null is an immediate response, distinct from an absent response.</summary>
public sealed record Confirm : Steering.IToolSteeringAction
{
    /// <summary>Creates an approval request. The optional response is cloned; the optional evaluator remains caller-owned.</summary>
    public Confirm(string prompt, string? reason = null, JsonElement? response = null, Func<JsonElement, bool>? evaluate = null)
    {
        Guard.NotNull(prompt, nameof(prompt));
        Prompt = prompt; Reason = reason; Response = InterruptJson.Copy(response, nameof(response)); Evaluate = evaluate ?? DefaultEvaluate;
    }
    /// <summary>The human-facing approval prompt, passed as the interrupt reason.</summary>
    public string Prompt { get; }
    /// <summary>Optional diagnostic metadata, excluded from model input.</summary>
    public string? Reason { get; }
    /// <summary>The detached preemptive response, or null to pause for external input.</summary>
    public JsonElement? Response { get; }
    /// <summary>The response evaluator; defaults to true or a trimmed, case-insensitive y/yes string.</summary>
    public Func<JsonElement, bool> Evaluate { get; }
    /// <summary>Accepts JSON true and trimmed y/yes strings; all other JSON values are declined.</summary>
    public static bool DefaultEvaluate(JsonElement response) => response.ValueKind == JsonValueKind.True ||
        response.ValueKind == JsonValueKind.String && response.GetString()!.Trim() is var text && (text.Equals("y", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Transforms a mutable lifecycle event before later handlers evaluate it.</summary>
public sealed record Transform : IInterventionAction
{
    /// <summary>Creates a synchronous event transformation.</summary>
    public Transform(Action<HookEvent> apply, string? reason = null)
    {
        Guard.NotNull(apply, nameof(apply));
        Apply = (value, _) => { apply(value); return default; }; Reason = reason;
    }
    /// <summary>Creates an awaited native transformation, forwarding the lifecycle cancellation token.</summary>
    public Transform(Func<HookEvent, CancellationToken, ValueTask> apply, string? reason = null)
    { Apply = apply ?? throw new ArgumentNullException(nameof(apply)); Reason = reason; }
    /// <summary>The transformation to await; later handlers see its mutation.</summary>
    public Func<HookEvent, CancellationToken, ValueTask> Apply { get; }
    /// <summary>Optional diagnostic metadata, excluded from model input.</summary>
    public string? Reason { get; }
}
