using System.Text.Json;

namespace Strands.Agents.Middleware;

/// <summary>Explicit model input. Provider state is deliberately excluded; invocation state and the borrowed model remain shared references.</summary>
public sealed record InvokeModelContext
{
    private string? _systemPrompt;
    private IReadOnlyList<ContentBlock>? _systemPromptContent;
    /// <summary>Creates a model-call context from provider-neutral input, excluding provider state.</summary>
    public InvokeModelContext(Agent agent, IModel model, ModelRequest request, IReadOnlyDictionary<string, object?> invocationState)
    {
        Guard.NotNull(agent, nameof(agent)); Guard.NotNull(model, nameof(model)); Guard.NotNull(request, nameof(request)); Guard.NotNull(invocationState, nameof(invocationState));
        Agent = agent; Model = model; Messages = MiddlewareCopies.Messages(request.Messages); SystemPrompt = request.SystemPrompt;
        if (request.SystemPromptContent is { } systemPromptContent) SystemPromptContent = systemPromptContent;
        ToolSpecs = MiddlewareCopies.Tools(request.Tools ?? Array.Empty<ToolSpec>()); ToolChoice = request.ToolChoice;
        InvocationState = invocationState; DynamicTrailingBlocks = request.DynamicTrailingBlocks;
    }
    /// <summary>The owning agent.</summary>
    public Agent Agent { get; }
    /// <summary>The borrowed model selected for this call, replaceable without changing the agent's model.</summary>
    public IModel Model { get; init; }
    /// <summary>The detached, model-visible messages.</summary>
    public IReadOnlyList<Message> Messages { get; init; }
    /// <summary>Optional model instructions. Explicit assignment replaces the whole prompt, clearing typed content.</summary>
    public string? SystemPrompt { get => _systemPrompt; init { _systemPrompt = value; _systemPromptContent = null; } }
    /// <summary>Ordered instructions copied on assignment. Explicit assignment, including null, replaces the whole prompt and its text projection.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContentBlock>? SystemPromptContent
    {
        get => _systemPromptContent;
        init { _systemPromptContent = SessionSnapshot.CopySystemPromptContent(value, allowGuardContent: true); _systemPrompt = SessionSnapshot.GetSystemPromptText(_systemPromptContent); }
    }
    /// <summary>The detached model-facing tool specifications.</summary>
    public IReadOnlyList<ToolSpec> ToolSpecs { get; init; }
    /// <summary>Optional tool selection.</summary>
    public ToolChoice? ToolChoice { get; init; }
    /// <summary>The unchanged caller-owned invocation context. The SDK adds no entries.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>An optional nonnegative input-token estimate.</summary>
    public long? ProjectedInputTokens { get; init; }
    /// <summary>Per-call trailing user blocks that automatic cache boundaries must precede.</summary>
    public int DynamicTrailingBlocks { get; init; }
    internal InvokeModelContext Snapshot()
    {
        Guard.NotNull(Model, nameof(Model));
        if (ProjectedInputTokens is < 0) throw new ArgumentOutOfRangeException(nameof(ProjectedInputTokens));
        if (DynamicTrailingBlocks < 0) throw new ArgumentOutOfRangeException(nameof(DynamicTrailingBlocks));
        var snapshot = this with { Messages = MiddlewareCopies.Messages(Messages), ToolSpecs = MiddlewareCopies.Tools(ToolSpecs) };
        return SystemPromptContent is { } content ? snapshot with { SystemPromptContent = content } : snapshot;
    }
}

/// <summary>The authoritative model result after middleware has completed.</summary>
/// <param name="Message">The completed assistant message.</param>
/// <param name="StopReason">The extensible provider or framework stop reason.</param>
/// <param name="Usage">The authoritative nonnegative usage counters.</param>
public sealed record InvokeModelResult(Message Message, string StopReason, Usage Usage)
{
    private Usage _usage = Usage;
    private bool _usageReported = true;

    /// <summary>The authoritative usage counters. Assigning a replacement explicitly marks those counters as reported.</summary>
    public Usage Usage { get => _usage; init { _usage = value; _usageReported = true; } }
    /// <summary>Whether usage was reported. False retains a zero placeholder without implying a measured zero-token response.</summary>
    public bool UsageReported { get => _usageReported; init => _usageReported = value; }
    /// <summary>Optional authoritative provider timing after model middleware has completed.</summary>
    public Telemetry.ModelPerformanceMetrics? PerformanceMetrics { get; init; }
    /// <summary>Optional replacement for the latest user message after this model result is accepted. Null leaves input unchanged.</summary>
    public string? InputRedaction { get; init; }

    internal InvokeModelResult Snapshot()
    {
        Guard.NotNull(Message, nameof(Message)); Guard.NotNullOrWhiteSpace(StopReason, nameof(StopReason)); Guard.NotNull(Usage, nameof(Usage));
        if (PerformanceMetrics is { } timing && (timing.Latency < TimeSpan.Zero || timing.TimeToFirstToken < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(PerformanceMetrics), "Middleware performance timings cannot be negative.");
        if (Message.Role != Role.Assistant) throw new ArgumentException("A model middleware result must contain an assistant message.", nameof(Message));
        if (Usage.InputTokens < 0 || Usage.OutputTokens < 0 || Usage.CacheReadInputTokens < 0 || Usage.CacheWriteInputTokens < 0 || Usage.ReportedTotalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(Usage), "Middleware usage counters cannot be negative.");
        try { _ = Usage.TotalTokens; }
        catch (OverflowException error) { throw new ArgumentOutOfRangeException(nameof(Usage), error.Message); }
        if (!UsageReported && (Usage.InputTokens != 0 || Usage.OutputTokens != 0 || Usage.CacheReadInputTokens != 0 || Usage.CacheWriteInputTokens != 0 || Usage.TotalTokens != 0))
            throw new ArgumentException("Unreported middleware usage must use a zero placeholder.", nameof(UsageReported));
        var calls = Message.Content.OfType<ToolUseBlock>().ToArray();
        if (calls.Select(call => call.ToolUseId).Distinct(StringComparer.Ordinal).Count() != calls.Length)
            throw new ArgumentException("A model middleware result cannot repeat tool-use identifiers.", nameof(Message));
        if (StopReason == StopReasons.ToolUse && calls.Length == 0)
            throw new ArgumentException("A tool-use stop requires at least one tool request.", nameof(StopReason));
        return this with { Message = new(Message.Role, Message.Content, Message.TrackingId, Message.Metadata) };
    }
}

/// <summary>Explicit tool input. Tool implementations and caller invocation state are borrowed references.</summary>
public sealed record ExecuteToolContext
{
    private readonly string _interruptToolUseId;
    private readonly string? _interruptBatchId;
    /// <summary>Returns a supplied human response, or pauses before invoking the next tool middleware.</summary>
    public MiddlewareInterruptResult Interrupt(string name, JsonElement? reason = null, JsonElement? response = null) => new(InterruptHandler is { } handler ? handler(name, reason, response) : Agent.ResolveInterrupt(_interruptToolUseId, name, reason, InterruptSource.Middleware, response, originBatchId: _interruptBatchId));
    internal Func<string, JsonElement?, JsonElement?, JsonElement>? InterruptHandler { get; init; }
    /// <summary>Creates a tool-call context whose cancellation token belongs to the executor.</summary>
    public ExecuteToolContext(Agent agent, ITool? tool, ToolUseBlock toolUse, IReadOnlyDictionary<string, object?> invocationState, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent)); Guard.NotNull(toolUse, nameof(toolUse)); Guard.NotNull(invocationState, nameof(invocationState));
        Agent = agent; Tool = tool; ToolUse = (ToolUseBlock)Message.CloneBlock(toolUse); InvocationState = invocationState; CancellationToken = cancellationToken;
        _interruptToolUseId = toolUse.ToolUseId; _interruptBatchId = agent.ActiveInterruptBatchId;
    }
    /// <summary>The owning agent.</summary>
    public Agent Agent { get; }
    /// <summary>The borrowed implementation, or null when no matching tool was found.</summary>
    public ITool? Tool { get; init; }
    /// <summary>The effective request; its original model-issued correlation is restored by the agent.</summary>
    public ToolUseBlock ToolUse { get; init; }
    /// <summary>The unchanged caller-owned invocation context.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>The executor-owned cancellation signal. Middleware may observe it but cannot replace it.</summary>
    public CancellationToken CancellationToken { get; }
    internal ExecuteToolContext Snapshot()
    {
        Guard.NotNull(ToolUse, nameof(ToolUse));
        return this with { ToolUse = (ToolUseBlock)Message.CloneBlock(ToolUse) };
    }
}

/// <summary>A middleware interrupt response wrapper, allowing future additions without changing the JSON response contract.</summary>
public sealed record MiddlewareInterruptResult
{
    /// <summary>Copies the supplied JSON response, including an explicit JSON null.</summary>
    public MiddlewareInterruptResult(JsonElement response) => Response = InterruptJson.Copy(response, nameof(response))!.Value;
    /// <summary>The supplied human response; callers validate or deserialize its expected shape.</summary>
    public JsonElement Response { get; }
}

/// <summary>The authoritative tool result after middleware has completed.</summary>
/// <param name="Result">The result to return to the model.</param>
public sealed record ExecuteToolResult(ToolResultBlock Result)
{
    internal ExecuteToolResult Snapshot()
    {
        Guard.NotNull(Result, nameof(Result));
        if (!Enum.IsDefined(typeof(ToolResultStatus), Result.Status)) throw new ArgumentException("A tool middleware result has an invalid status.", nameof(Result));
        return this with { Result = (ToolResultBlock)Message.CloneBlock(Result) };
    }
}

internal static class MiddlewareCopies
{
    internal static IReadOnlyList<Message> Messages(IReadOnlyList<Message> messages)
    {
        Guard.NotNull(messages, nameof(messages));
        return Array.AsReadOnly(messages.Select(message =>
        {
            Guard.NotNull(message, nameof(messages));
            return new Message(message.Role, message.Content, message.TrackingId, message.Metadata);
        }).ToArray());
    }
    internal static IReadOnlyList<ToolSpec> Tools(IReadOnlyList<ToolSpec> tools)
    {
        Guard.NotNull(tools, nameof(tools));
        return Array.AsReadOnly(tools.Select(tool =>
        {
            Guard.NotNull(tool, nameof(tools));
            return new ToolSpec(tool.Name, tool.Description, tool.InputSchema) { OutputSchema = tool.OutputSchema?.Clone(), Annotations = tool.Annotations?.Clone() };
        }).ToArray());
    }
}
