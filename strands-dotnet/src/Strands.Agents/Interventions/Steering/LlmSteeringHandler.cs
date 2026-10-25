using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents.Interventions.Steering;

/// <summary>Model, prompt, and context settings for tool-call steering.</summary>
public sealed record LlmSteeringHandlerOptions
{
    /// <summary>The steering rules supplied separately from the evaluated context.</summary>
    public required string SystemPrompt { get; init; }
    /// <summary>An explicit borrowed model; otherwise the owning agent's auxiliary model is resolved for each evaluation.</summary>
    public IModel? Model { get; init; }
    /// <summary>An optional rich prompt builder. Wrap a plain string in TextBlock; cache boundaries and other supported content remain typed.</summary>
    public Func<IReadOnlyList<JsonElement>, ToolUseBlock?, IReadOnlyList<ContentBlock>>? PromptBuilder { get; init; }
    /// <summary>Optional borrowed factories. Null creates the default tool ledger; an empty list disables context providers.</summary>
    public IReadOnlyList<ISteeringContextProvider>? ContextProviders { get; init; }
    /// <summary>The stable handler identifier; override when registering multiple steering policies.</summary>
    public string Name { get; init; } = "strands:llm-steering-handler";
}

/// <summary>Evaluates tool calls through fresh structured model conversations. Model-output steering remains the base handler's default Proceed.</summary>
public class LlmSteeringHandler : SteeringHandler
{
    private static readonly JsonSerializerOptions PromptJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"type":{"enum":["proceed","guide","confirm"]},"decision":{"enum":["proceed","guide","interrupt"]},"reason":{"type":"string"}},"required":["reason"],"oneOf":[{"required":["type"]},{"required":["decision"]}],"additionalProperties":false}
        """);
    private readonly IModel? _model;
    private readonly string _systemPrompt;
    private readonly Func<IReadOnlyList<JsonElement>, ToolUseBlock?, IReadOnlyList<ContentBlock>> _promptBuilder;
    /// <summary>Copies configuration and defaults context to one agent-local tool ledger.</summary>
    public LlmSteeringHandler(LlmSteeringHandlerOptions options) : base(ProviderOptions(options))
    {
        Guard.NotNull(options.SystemPrompt, nameof(options)); Guard.NotNullOrWhiteSpace(options.Name, nameof(options));
        Name = options.Name; _model = options.Model; _systemPrompt = options.SystemPrompt; _promptBuilder = options.PromptBuilder ?? DefaultPrompt;
    }
    /// <inheritdoc />
    public override string Name { get; }
    /// <inheritdoc />
    public override async ValueTask<IToolSteeringAction> SteerBeforeToolAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(value, nameof(value)); cancellationToken.ThrowIfCancellationRequested();
        var prompt = _promptBuilder(GetSteeringContext(value.Agent), value.ToolUse) ?? throw new InvalidOperationException("The steering prompt builder returned null content.");
        var inner = new Agent(new() { Model = _model ?? value.Agent.AuxModel, SystemPrompt = _systemPrompt });
        await using var lifetime = inner.ConfigureAwait(false);
        var result = await inner.InvokeStructuredContentAsync(prompt, new StructuredOutputOptions<IToolSteeringAction>
        {
            Schema = Schema,
            Deserializer = json =>
            {
                var decision = json.TryGetProperty("type", out var type) ? type.GetString() : json.GetProperty("decision").GetString();
                var reason = json.GetProperty("reason").GetString()!;
                return decision switch
                {
                    "proceed" => new Proceed(reason),
                    "guide" => new Guide(reason),
                    "confirm" or "interrupt" => new Confirm(reason, reason),
                    _ => throw new ArgumentException("The steering decision must be proceed, guide, confirm, or Python's interrupt.", nameof(json))
                };
            }
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    private static SteeringHandlerOptions ProviderOptions(LlmSteeringHandlerOptions options)
    {
        Guard.NotNull(options, nameof(options));
        return new() { ContextProviders = options.ContextProviders ?? [new ToolLedgerProvider()] };
    }

    private static IReadOnlyList<ContentBlock> DefaultPrompt(IReadOnlyList<JsonElement> context, ToolUseBlock? toolUse)
    {
        var contextText = context.Count > 0 ? JsonSerializer.Serialize(context, PromptJson) : "No context available";
        var actionType = toolUse is null ? "action" : "tool call";
        var actionTitle = toolUse is null ? "Action" : "Tool Call";
        var description = toolUse is null ? "General evaluation" : $"Tool: {toolUse.Name}\nArguments: {JsonSerializer.Serialize(toolUse.Input, PromptJson)}";
        var ledgerExplanation = context.Any(value => value.GetProperty("type").GetString() == "toolLedger") ? """


            ### Understanding Ledger Tool States

            If the context includes a ledger with tool_calls, the "status" field indicates:

            - **"pending"**: The tool is CURRENTLY being evaluated by you (the steering agent).
            This is NOT a duplicate call — it's the tool you're deciding whether to approve.
            The tool has NOT started executing yet.
            - **"success"**: The tool completed successfully in a previous turn
            - **"error"**: The tool failed or was cancelled in a previous turn

            **IMPORTANT**: When you see a tool with status="pending" that matches the tool you're evaluating,
            that IS the current tool being evaluated. It is NOT already executing or a duplicate.
            """ : "";
        var instructions = $$"""
            # Steering Evaluation

            ## Overview

            You are a STEERING AGENT that evaluates a {{actionType}} that ANOTHER AGENT is attempting to make.
            Your job is to provide contextual guidance to help the other agent navigate workflows effectively.
            You act as a safety net that can intervene when patterns in the context data suggest the agent
            should try a different approach or get human input.

            **YOUR ROLE:**
            - Analyze context data for concerning patterns (repeated failures, inappropriate timing, etc.)
            - Provide just-in-time guidance when the agent is going down an ineffective path
            - Allow normal operations to proceed when context shows no issues

            **CRITICAL CONSTRAINTS:**
            - Base decisions ONLY on the context data provided
            - Do NOT use external knowledge about domains, URLs, or tool purposes
            - Do NOT make assumptions about what tools "should" or "shouldn't" do
            - Focus ONLY on patterns in the context data{{ledgerExplanation}}

            ## Steps

            ### 1. Analyze the {{actionTitle}}

            Review ONLY the context data. Look for patterns in the data that indicate:

            - Previous failures or successes with this tool
            - Frequency of attempts
            - Any relevant tracking information

            **Constraints:**
            - You MUST base analysis ONLY on the provided context data
            - You MUST NOT use external knowledge about tool purposes or domains
            - You SHOULD identify patterns in the context data
            - You MAY reference relevant context data to inform your decision

            ### 2. Make Steering Decision

            **Constraints:**
            - You MUST respond with exactly one of: "proceed", "guide", or "confirm"
            - You MUST base the decision ONLY on context data patterns
            - Your reason will be shown to the AGENT as guidance

            **Decision Options:**
            - "proceed" if context data shows no concerning patterns
            - "guide" if context data shows patterns requiring intervention
            - "confirm" if context data shows patterns requiring human input
            """;
        var dynamic = $$"""
            ## Context

            {{contextText}}

            ## Event to Evaluate

            {{description}}
            """;
        return [new TextBlock(instructions), new CachePointBlock(), new TextBlock(dynamic)];
    }
}
