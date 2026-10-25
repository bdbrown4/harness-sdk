namespace Strands.Agents;

/// <summary>Creates an isolated model policy instance for all calls in one agent invocation.</summary>
public interface IInvocationModel : IModel
{
    /// <summary>Creates invocation-local routing or retry state without modifying caller-owned context.</summary>
    /// <param name="invocationState">The caller's read-only invocation context.</param>
    /// <returns>A model used throughout this invocation. The caller retains transport ownership.</returns>
    IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState);
}

/// <summary>A model whose provider keeps authoritative conversation history on the server.</summary>
public interface IStatefulModel : IModel
{
    /// <summary>Whether server-managed conversation state is enabled. Native agents capture this capability at construction; explicit retained-history profiles observe it at their selected provider and cleanup boundaries.</summary>
    bool Stateful { get; }
}

internal static class ModelStateValues
{
    internal static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Clone(IReadOnlyDictionary<string, System.Text.Json.JsonElement> values)
    {
        try
        {
            if (values.Any(value => value.Key is null || value.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined))
                throw new ModelException("Provider state must contain nonnull keys and valid JSON values.");
            return new StateStore(values).GetAll();
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or ArgumentException)
        { throw new ModelException("Provider state must contain detached, valid JSON values.", error); }
    }
}

/// <summary>A provider-neutral request. Providers translate it into their own wire formats.</summary>
/// <param name="Messages">The ordered conversation visible to this model call.</param>
/// <param name="SystemPrompt">Optional instructions sent separately from the conversation.</param>
/// <param name="Tools">The tools available to the model, or null when none are supplied.</param>
public sealed record ModelRequest(IReadOnlyList<Message> Messages, string? SystemPrompt = null, IReadOnlyList<ToolSpec>? Tools = null)
{
    private readonly string? _stringSystemPrompt = SystemPrompt;
    private readonly IReadOnlyList<ContentBlock>? _systemPromptContent;

    /// <summary>The legacy string, or the newline-joined text projection when typed system content is present.</summary>
    public string? SystemPrompt
    {
        get => _systemPromptContent is null ? _stringSystemPrompt : SessionSnapshot.GetSystemPromptText(_systemPromptContent);
        init => _stringSystemPrompt = value;
    }

    /// <summary>Optional detached, ordered text/cache-point/guard system content. Its presence takes precedence over the legacy string.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContentBlock>? SystemPromptContent
    {
        get => _systemPromptContent;
        init => _systemPromptContent = SessionSnapshot.CopySystemPromptContent(value, allowGuardContent: true);
    }
    /// <summary>Optional provider-neutral tool selection. Null preserves the provider's default.</summary>
    public ToolChoice? ToolChoice { get; init; }
    /// <summary>An optional agent session identifier for provider session and prompt-cache attribution.</summary>
    public string? SessionId { get; init; }
    /// <summary>An immutable, detached snapshot of agent-owned provider state, excluded from application invocation context.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ModelState { get; init; }
    /// <summary>The number of per-call blocks appended to the final user message. Automatic cache boundaries precede this suffix; zero leaves caching unchanged.</summary>
    public int DynamicTrailingBlocks { get; init; }
}

/// <summary>The permitted model tool-selection policy.</summary>
public enum ToolChoiceMode
{
    /// <summary>The model chooses between a text response and tools.</summary>
    Auto,
    /// <summary>The model must select at least one available tool.</summary>
    Any,
    /// <summary>The model must not select a tool.</summary>
    None,
    /// <summary>The model must select the named tool.</summary>
    Tool
}

/// <summary>Provider-neutral tool selection; Tool mode requires a registered tool name.</summary>
public sealed record ToolChoice(ToolChoiceMode Mode, string? Name = null);

/// <summary>The native model extension point. Implementations must honor cancellation and dispose transport resources.</summary>
public interface IModel
{
    /// <summary>Streams one assistant response using indexed content blocks and an explicit terminal stop event.</summary>
    /// <param name="request">The conversation, instructions, and available tool specifications.</param>
    /// <param name="cancellationToken">Cancellation propagated to provider requests and response reads.</param>
    /// <returns>The incremental provider-neutral model events.</returns>
    /// <exception cref="ModelException">The provider request fails or its response cannot be represented.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An incremental event from one model response.</summary>
public abstract record ModelStreamEvent;
/// <summary>Begins the assistant message for one model response.</summary>
/// <param name="Role">The response role, which must be assistant in the agent loop.</param>
public sealed record ModelMessageStartEvent(Role Role = Role.Assistant) : ModelStreamEvent;
/// <summary>Begins a uniquely indexed block before any deltas for that block.</summary>
/// <param name="Index">The nonnegative block index, used to restore source order.</param>
/// <param name="ToolUse">The initial tool identity and input, or null for text or reasoning.</param>
public sealed record ModelContentBlockStartEvent(int Index, ToolUseBlock? ToolUse = null) : ModelStreamEvent
{
    /// <summary>An optional initial typed block, including opaque reasoning or media content.</summary>
    public ContentBlock? ContentBlock { get; init; }
}
/// <summary>Appends content to an open block. Each block contains text, tool input, or reasoning.</summary>
/// <param name="Index">The index of the open block.</param>
/// <param name="Text">Optional plain text to append.</param>
/// <param name="ToolInput">Optional raw JSON fragment for a tool's arguments.</param>
/// <param name="Reasoning">Optional reasoning text to append.</param>
/// <param name="Signature">Optional reasoning-signature fragment to append.</param>
public sealed record ModelContentBlockDeltaEvent(int Index, string? Text = null, string? ToolInput = null, string? Reasoning = null, string? Signature = null) : ModelStreamEvent
{
    private readonly byte[]? _redactedReasoningBytes;
    /// <summary>An opaque reasoning byte fragment. Input and returned arrays are detached; null omits this payload.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? RedactedReasoningBytes { get => _redactedReasoningBytes?.ToArray(); init => _redactedReasoningBytes = value?.ToArray(); }
    private readonly IReadOnlyList<Citation> _citations = [];
    /// <summary>Additional source citations for this text block, copied when the event is constructed.</summary>
    public IReadOnlyList<Citation> Citations { get => _citations; init => _citations = Citation.Copy(value); }
}
/// <summary>Closes a content block after its final delta.</summary>
/// <param name="Index">The index of the open block to close.</param>
public sealed record ModelContentBlockStopEvent(int Index) : ModelStreamEvent
{
    /// <summary>Optional completed typed content. Its text, reasoning and tool identity/input must agree with emitted deltas.</summary>
    public ContentBlock? ContentBlock { get; init; }
}
/// <summary>Ends a model response after all content blocks have closed.</summary>
/// <param name="StopReason">A standard or provider-specific reason for stopping.</param>
public sealed record ModelMessageStopEvent(string StopReason) : ModelStreamEvent
{
    /// <summary>Optional response metadata, persisted with the completed message and excluded from future provider input.</summary>
    public System.Text.Json.JsonElement? Metadata { get; init; }
    /// <summary>Optional complete replacement for agent-owned provider state, accepted only after the complete model turn succeeds.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ModelState { get; init; }
}
/// <summary>Reports additive usage for this response; providers should not emit repeated cumulative totals.</summary>
/// <param name="Usage">The token counts contributed by this event.</param>
public sealed record ModelMetadataEvent(Usage Usage) : ModelStreamEvent
{
    private Usage _usage = Usage;
    private bool _usageReported = true;

    /// <summary>The additive usage counters. Assigning a replacement explicitly marks those counters as reported.</summary>
    public Usage Usage { get => _usage; init { _usage = value; _usageReported = true; } }
    /// <summary>Whether Usage was reported by the provider, including an explicit zero. False requires a zero placeholder and permits timing-only metadata.</summary>
    public bool UsageReported { get => _usageReported; init => _usageReported = value; }
    /// <summary>Optional provider-reported latency and time to first token. Missing timing does not imply zero latency.</summary>
    public Telemetry.ModelPerformanceMetrics? PerformanceMetrics { get; init; }
}

/// <summary>Replaces blocked input or output after a provider guardrail assessment. Stream consumers may have already observed the original deltas.</summary>
/// <param name="InputReplacement">Replacement for the latest user message, preserving tool-result correlation; null leaves input unchanged.</param>
/// <param name="OutputReplacement">Replacement for the complete assistant content; null leaves output unchanged.</param>
public sealed record ModelRedactionEvent(string? InputReplacement = null, string? OutputReplacement = null) : ModelStreamEvent
{
    /// <summary>Optional original output supplied by the provider's trace. Applications should handle it as sensitive content.</summary>
    public string? RedactedOutput { get; init; }
}

/// <summary>When provider-requested input redaction changes native history.</summary>
public enum InputRedactionTiming
{
    /// <summary>Apply only when the model result is accepted, matching the TypeScript model/agent contract.</summary>
    AcceptedTurn,
    /// <summary>Apply when the provider emits its event, including before later stream failure or disposal, matching Python's inline contract.</summary>
    ProviderEvent
}

/// <summary>A provider request or model protocol failure.</summary>
/// <param name="message">A description of the failure.</param>
/// <param name="innerException">The original transport, provider, or parsing exception when available.</param>
public class ModelException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The model rejected a request because its context window is full.</summary>
/// <param name="message">A description of the rejected request.</param>
/// <param name="innerException">The original provider exception when available.</param>
public sealed class ContextWindowOverflowException(string message, Exception? innerException = null) : ModelException(message, innerException);

/// <summary>The provider throttled a request.</summary>
/// <param name="message">A description of the throttling or capacity failure.</param>
/// <param name="innerException">The original provider exception when available.</param>
public sealed class ModelThrottledException(string message, Exception? innerException = null) : ModelException(message, innerException);
