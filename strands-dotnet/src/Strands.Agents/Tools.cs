using System.Text.Json;

namespace Strands.Agents;

/// <summary>The model-facing description and input schema of a tool.</summary>
public sealed record ToolSpec
{
    /// <summary>Creates a model-facing tool specification and clones its input schema.</summary>
    /// <param name="name">A unique name containing 1â€“64 ASCII letters, digits, underscores, or hyphens.</param>
    /// <param name="description">A nonempty description that helps the model choose the tool.</param>
    /// <param name="inputSchema">A JSON object defining the input schema.</param>
    /// <exception cref="ArgumentException">A name, description, or schema is invalid.</exception>
    public ToolSpec(string name, string description, JsonElement inputSchema)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        Guard.NotNullOrWhiteSpace(description, nameof(description));
        if (name.Length > 64 || name.Any(c => !Guard.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Tool names must contain 1â€“64 ASCII letters, digits, underscores, or hyphens.", nameof(name));
        if (inputSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A tool input schema must be a JSON object.", nameof(inputSchema));
        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
    }

    /// <summary>The name used by model tool requests and the agent registry.</summary>
    public string Name { get; }
    /// <summary>The model-facing description of the tool's purpose.</summary>
    public string Description { get; }
    /// <summary>The cloned JSON input schema validated by the agent before execution.</summary>
    public JsonElement InputSchema { get; }
    /// <summary>An optional output schema advertised by protocols such as MCP.</summary>
    public JsonElement? OutputSchema { get; init; }
    /// <summary>Optional protocol-specific advisory annotations; these do not grant authorization.</summary>
    public JsonElement? Annotations { get; init; }
}

/// <summary>A callable capability. The agent validates its schema before invoking it.</summary>
public interface ITool
{
    /// <summary>The tool's model-facing name, description, and input schema.</summary>
    ToolSpec Spec { get; }
    /// <summary>Invokes the tool directly. The agent performs schema validation and converts failures into tool results.</summary>
    /// <param name="input">The tool input. Direct callers are responsible for validating it.</param>
    /// <param name="context">The originating request and caller-owned invocation state.</param>
    /// <param name="cancellationToken">Cancellation forwarded to the tool implementation.</param>
    /// <returns>The text returned by the tool.</returns>
    ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>A tool capable of returning typed content and an explicit error status.</summary>
public interface IRichTool : ITool
{
    /// <summary>Returns a complete result correlated with the current tool-use identifier.</summary>
    ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>Context shared with a tool, including caller-owned invocation state.</summary>
/// <param name="Agent">The agent executing this tool call.</param>
/// <param name="ToolUse">The model-issued request for this execution.</param>
/// <param name="InvocationState">The caller-owned context shared throughout the invocation.</param>
public sealed record ToolContext(Agent Agent, ToolUseBlock ToolUse, IReadOnlyDictionary<string, object?> InvocationState)
{
    private readonly CancellationToken _cancelSignal = Agent?.CancelSignal ?? CancellationToken.None;
    /// <summary>The captured cancellation signal for this execution; it never changes to a later pass's token. Detached background and direct calls use their own execution signal.</summary>
    public CancellationToken CancelSignal => ExecutionCancelSignal ?? _cancelSignal;
    internal CancellationToken? ExecutionCancelSignal { get; init; }
    private readonly string _interruptToolUseId = ToolUse?.ToolUseId ?? string.Empty;
    private readonly string? _interruptBatchId = Agent?.ActiveInterruptBatchId;
    /// <summary>Returns a supplied human response, or pauses this batch. Place approvals before side effects because incomplete tool bodies replay from the beginning.</summary>
    public JsonElement Interrupt(string name, JsonElement? reason = null, JsonElement? response = null) => InterruptHandler is { } handler ? handler(name, reason, response) : Agent.ResolveInterrupt(_interruptToolUseId, name, reason, InterruptSource.Tool, response, originBatchId: _interruptBatchId);
    internal Func<string, JsonElement?, JsonElement?, JsonElement>? InterruptHandler { get; init; }
    internal AgentToolCallContext? AgentToolInterrupts { get; init; }
}

/// <summary>A tool backed by an asynchronous delegate.</summary>
/// <param name="spec">The model-facing specification.</param>
/// <param name="callback">The implementation invoked with input, execution context, and cancellation.</param>
public sealed class FunctionTool(ToolSpec spec, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) : ITool
{
    /// <inheritdoc/>
    public ToolSpec Spec { get; } = spec ?? throw new ArgumentNullException(nameof(spec));
    private readonly Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    /// <inheritdoc/>
    public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => _callback(input, context, cancellationToken);

    /// <summary>Deserializes input with System.Text.Json after schema validation by the agent.</summary>
    /// <typeparam name="T">The type used to deserialize the tool input.</typeparam>
    /// <param name="name">The tool's registered name.</param>
    /// <param name="description">The description presented to the model.</param>
    /// <param name="schema">The JSON input schema used by the agent.</param>
    /// <param name="callback">The implementation receiving the deserialized input.</param>
    /// <param name="serializerOptions">Serialization settings, or null for System.Text.Json web defaults.</param>
    /// <returns>A function tool with typed input deserialization.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="ArgumentException">The specification is invalid.</exception>
    public static FunctionTool Create<T>(string name, string description, JsonElement schema,
        Func<T, ToolContext, CancellationToken, ValueTask<string>> callback, JsonSerializerOptions? serializerOptions = null)
    {
        Guard.NotNull(callback, nameof(callback));
        return new(new ToolSpec(name, description, schema), (input, context, cancellationToken) =>
            callback(input.Deserialize<T>(serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException("Tool input cannot be null."), context, cancellationToken));
    }
}
