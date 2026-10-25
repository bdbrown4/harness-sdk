using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>Settings for a fresh, tool-free structured risk-evaluation conversation.</summary>
public sealed record LlmRiskClassifierOptions
{
    /// <summary>An explicit borrowed model; otherwise the owning agent's auxiliary model is used.</summary>
    public IModel? Model { get; init; }
    /// <summary>Optional replacement for the pinned general-purpose risk criteria.</summary>
    public string? SystemPrompt { get; init; }
}

/// <summary>Creates the model-based argument-level classifier used by human approval policies.</summary>
public static class LlmRiskClassifier
{
    private static readonly JsonSerializerOptions PromptJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"requiresApproval":{"type":"boolean","description":"Whether this tool call requires human approval before executing"},"reason":{"type":"string","description":"Brief reason (under 10 words) why approval is or is not required"}},"required":["requiresApproval","reason"],"additionalProperties":false}
        """);
    // Pinned TypeScript and Python classifier criteria are identical.
    private const string DefaultSystemPrompt = """
        You are a risk evaluator for an AI agent's tool calls. Your job is to decide whether each tool call requires human approval before executing.

        ## When to require approval

        Require approval when the tool call:
        - Is destructive or irreversible (deleting data, dropping tables, revoking access)
        - Modifies important state in production or shared environments
        - Accesses or transmits sensitive data (credentials, PII, financial records)
        - Communicates externally (sending emails, posting messages, making payments)
        - Has a large blast radius (affecting many records, users, or systems)

        ## When approval is NOT needed

        Do not require approval when the tool call:
        - Is read-only AND does not access sensitive data (listing non-sensitive files, querying public data, searching)
        - Operates on local or temporary resources
        - Has easily reversible effects
        - Is scoped to a single non-critical resource

        Note: even read-only operations that access credentials, secrets, PII, or financial data still require approval.

        ## Instructions

        Evaluate the tool name and its input arguments. Consider what could go wrong if this specific call executes with these specific arguments. When uncertain, require approval.

        Keep your reason under 10 words — it is shown to a human in a CLI prompt.
        """;

    /// <summary>Creates an awaited classifier. Each call has independent conversation, tools, state, hooks, and validation.</summary>
    public static Func<BeforeToolCallEvent, CancellationToken, ValueTask<HumanInTheLoopClassifierResult>> Create(LlmRiskClassifierOptions? options = null)
    {
        var model = options?.Model;
        var systemPrompt = options?.SystemPrompt ?? DefaultSystemPrompt;
        return async (value, cancellationToken) =>
        {
            Guard.NotNull(value, nameof(value));
            cancellationToken.ThrowIfCancellationRequested();
            var inner = new Agent(new() { Model = model ?? value.Agent.AuxModel, SystemPrompt = systemPrompt });
            await using var lifetime = inner.ConfigureAwait(false);
            var prompt = $"Should this tool call require human approval?\n\nTool: {value.ToolUse.Name}\nInput: {JsonSerializer.Serialize(value.ToolUse.Input, PromptJson)}";
            var result = await inner.InvokeStructuredAsync(prompt, new StructuredOutputOptions<HumanInTheLoopClassifierResult>
            {
                Schema = Schema,
                Deserializer = json => new(json.GetProperty("requiresApproval").GetBoolean(), json.GetProperty("reason").GetString())
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Output;
        };
    }
}
