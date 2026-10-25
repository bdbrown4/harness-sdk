using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>Requests human approval for tools not allowed by configuration, classification, or optional session trust.</summary>
public class HumanInTheLoop : InterventionHandler
{
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly HumanInTheLoopOptions _options;
    private readonly HashSet<string> _allowed;
    private readonly ConditionalWeakTable<Agent, ClassifiedCalls> _classified = new();
    /// <summary>The native application-state key for trusted tool names. This follows the TypeScript spelling; Python uses hitl:trusted_tools.</summary>
    public const string TrustedToolsStateKey = "hitl:trustedTools";
    /// <summary>Creates an approval handler. By default every tool pauses for approval and trust is disabled.</summary>
    public HumanInTheLoop(HumanInTheLoopOptions? options = null)
    {
        _options = options ?? new();
        Guard.NotNull(_options.AllowedTools, nameof(options));
        _allowed = new(_options.AllowedTools, StringComparer.Ordinal);
        if (_allowed.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Allowed tool entries cannot be blank.", nameof(options));
    }
    /// <inheritdoc />
    public override string Name => "strands:human-in-the-loop";
    /// <inheritdoc />
    public override async ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(value, nameof(value));
        cancellationToken.ThrowIfCancellationRequested();
        var name = value.ToolUse.Name;
        var negated = _allowed.Contains("!" + name);
        var classification = await RequiresApprovalAsync(value, negated, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!classification.RequiresHumanInTheLoop) return new Proceed();
        var reason = string.IsNullOrEmpty(classification.Reason) ? "" : " — " + classification.Reason;
        var prompt = $"Approve \"{name}\"{reason}?\n  Input: {JsonSerializer.Serialize(value.ToolUse.Input, PromptJson)}";
        bool Evaluate(JsonElement response)
        {
            if (!negated && _options.EnableTrust && (_options.EvaluateTrust ?? IsTrustResponse)(response))
            {
                Trust(value.Agent, name);
                return true;
            }
            return (_options.Evaluate ?? Confirm.DefaultEvaluate)(response);
        }
        if (_options.Ask is null) return new Confirm(prompt, evaluate: Evaluate);
        var answer = await _options.Ask(prompt, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answer is null) return new Deny($"Tool \"{name}\" denied: approval callback returned no response.");
        // Validate and detach the callback's JSON before invoking caller evaluators.
        var copy = InterruptJson.Copy(answer, nameof(answer))!.Value;
        if (!negated && _options.EnableTrust && (_options.EvaluateTrust ?? IsTrustResponse)(copy))
        {
            Trust(value.Agent, name);
            return new Proceed();
        }
        return new Confirm(prompt, response: copy, evaluate: _options.Evaluate);
    }

    private async ValueTask<HumanInTheLoopClassifierResult> RequiresApprovalAsync(BeforeToolCallEvent value, bool negated, CancellationToken cancellationToken)
    {
        if (negated) return new(true);
        if (value.Agent.State.Get<string[]>(TrustedToolsStateKey)?.Contains(value.ToolUse.Name, StringComparer.Ordinal) == true) return new(false);
        if (_allowed.Contains("*") || _allowed.Contains(value.ToolUse.Name)) return new(false);
        if (_options.Classifier is not { } classify) return new(true);
        // An already persisted approval must remain an approval, including in a fresh process or handler instance.
        if (value.Agent.InterruptState?.Interrupts.Any(question => question.Source == InterruptSource.Hook && !question.IsBatch && question.Name == Name && question.ToolUseId == value.ToolUse.ToolUseId) == true)
            return new(true);
        var calls = _classified.GetValue(value.Agent, _ => new());
        lock (calls)
        {
            var batch = value.Agent.ActiveInterruptBatchId;
            if (batch != calls.Batch) { calls.Batch = batch; calls.Ids.Clear(); }
            if (!calls.Ids.Add(value.ToolUse.ToolUseId)) return new(true);
        }
        try
        {
            var result = await classify(value, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null) return result;
            Warn($"Tool {value.ToolUse.Name} classifier returned null; human approval is required.");
        }
        catch (OperationCanceledException) { throw; }
        catch (InterruptException) { throw; }
        catch (Exception error) { Warn($"Tool {value.ToolUse.Name} classifier failed; human approval is required: {error.Message}"); }
        return new(true);
    }

    private static void Trust(Agent agent, string name)
    {
        // All native approval handlers use this synchronous lock so concurrent trust responses cannot lose another tool's name.
        lock (agent.State)
        {
            var current = agent.State.Get<string[]>(TrustedToolsStateKey) ?? [];
            if (!current.Contains(name, StringComparer.Ordinal)) agent.State.Set(TrustedToolsStateKey, current.Concat([name]).ToArray());
        }
    }

    private static bool IsTrustResponse(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is var text &&
        (text.Equals("t", StringComparison.OrdinalIgnoreCase) || text.Equals("trust", StringComparison.OrdinalIgnoreCase));

    private static void Warn(string message)
    {
        try { SdkLogging.Warning("{Message}", message); }
        catch (Exception) { /* Diagnostics do not change an approval requirement. */ }
    }

    private sealed class ClassifiedCalls
    {
        internal string? Batch { get; set; }
        internal HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
    }
}
