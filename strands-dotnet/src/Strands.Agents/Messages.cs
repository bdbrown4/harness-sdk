using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>The conversational role understood by Strands models.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    /// <summary>A user prompt or a message containing tool results.</summary>
    [JsonStringEnumMemberName("user")]
    User,
    /// <summary>A model response, which may request tool execution.</summary>
    [JsonStringEnumMemberName("assistant")]
    Assistant
}

/// <summary>The outcome of a tool call.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolResultStatus>))]
public enum ToolResultStatus
{
    /// <summary>The tool completed successfully.</summary>
    [JsonStringEnumMemberName("success")]
    Success,
    /// <summary>The tool failed, was denied, or was cancelled.</summary>
    [JsonStringEnumMemberName("error")]
    Error
}

/// <summary>A typed block in a conversation. Discriminators are local snapshot fields.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ToolUseBlock), "toolUse")]
[JsonDerivedType(typeof(ToolResultBlock), "toolResult")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(AudioBlock), "audio")]
[JsonDerivedType(typeof(VideoBlock), "video")]
[JsonDerivedType(typeof(GuardContentBlock), "guardContent")]
[JsonDerivedType(typeof(JsonBlock), "json")]
[JsonDerivedType(typeof(ResourceLinkBlock), "resourceLink")]
[JsonDerivedType(typeof(RedactedReasoningBlock), "redactedReasoning")]
[JsonDerivedType(typeof(CachePointBlock), "cachePoint")]
[JsonDerivedType(typeof(DocumentBlock), "document")]
[JsonDerivedType(typeof(Models.ResponsesContentBlock), "openaiResponsesContent")]
[JsonDerivedType(typeof(Models.ResponsesReasoningBlock), "openaiResponsesReasoning")]
[JsonDerivedType(typeof(Models.ResponsesOutputItemBlock), "openaiResponsesOutputItem")]
[JsonDerivedType(typeof(Models.GoogleContentBlock), "googleContent")]
[JsonDerivedType(typeof(Models.AnthropicContentBlock), "anthropicContent")]
[JsonDerivedType(typeof(CitationsBlock), "citations")]
public abstract record ContentBlock;

/// <summary>Plain text content.</summary>
/// <param name="Text">The text to present to the model or caller.</param>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>A model request to execute a named tool with JSON input.</summary>
/// <param name="ToolUseId">The provider-issued identifier used to correlate the result.</param>
/// <param name="Name">The registered tool name.</param>
/// <param name="Input">A JSON object containing the tool arguments.</param>
public sealed record ToolUseBlock(string ToolUseId, string Name, JsonElement Input) : ContentBlock
{
    /// <summary>Optional provider-specific replay fields, keyed by provider name. Input, Name and ToolUseId remain authoritative.</summary>
    public JsonElement? ProviderMetadata { get; init; }
}

/// <summary>A tool result correlated with its originating request.</summary>
/// <param name="ToolUseId">The identifier from the corresponding tool request.</param>
/// <param name="Content">The text returned to the model, including a failure explanation when appropriate.</param>
/// <param name="Status">Whether the tool execution succeeded.</param>
[method: JsonConstructor]
public sealed record ToolResultBlock(string ToolUseId, string Content, ToolResultStatus Status = ToolResultStatus.Success) : ContentBlock
{
    private readonly JsonElement? _metadata;
    /// <summary>Detached protocol or application metadata, persisted locally and excluded from model-provider payloads. Null omits the serialized field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Metadata
    {
        get => _metadata;
        init
        {
            if (value is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) }) throw new ArgumentException("Tool result metadata must be an object or null.", nameof(value));
            _metadata = value is { ValueKind: JsonValueKind.Object } ? value.Value.Clone() : null;
        }
    }
    /// <summary>Additional typed result content, preserved in message snapshots and provider translation.</summary>
    public IReadOnlyList<ContentBlock> ContentBlocks { get; init; } = [];
    /// <summary>When true, ContentBlocks is authoritative even when empty. False retains the legacy textual fallback.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasExplicitContent { get; init; }
    /// <summary>An optional protocol-defined structured result, separate from textual display content.</summary>
    public JsonElement? StructuredContent { get; init; }
    /// <summary>The original execution failure for hooks and local inspection. It is excluded from snapshots and provider serialization.</summary>
    [JsonIgnore]
    public Exception? Error { get; init; }
    [JsonIgnore]
    internal bool IteratorCleanupFailed { get; init; }
    /// <summary>Constructs a rich result while retaining a convenient textual representation.</summary>
    public ToolResultBlock(string toolUseId, IReadOnlyList<ContentBlock> content, ToolResultStatus status = ToolResultStatus.Success, JsonElement? structuredContent = null)
        : this(toolUseId, string.Concat(content.OfType<TextBlock>().Select(block => block.Text)), status)
    {
        ContentBlocks = Array.AsReadOnly(content.Select(Message.CloneBlock).ToArray());
        StructuredContent = structuredContent?.Clone();
    }
}

/// <summary>Image content supplied as base64 bytes, an S3 location, or an HTTP URL.</summary>
[method: JsonConstructor]
public sealed record ImageBlock(string MediaType, string Data) : ContentBlock
{
    /// <summary>An S3 source, mutually exclusive with nonempty Data and Url.</summary>
    public S3Location? Location { get; init; }
    /// <summary>An absolute HTTP or HTTPS image source, mutually exclusive with nonempty Data and Location.</summary>
    public Uri? Url { get; init; }
    /// <summary>Creates an image referencing an S3 object.</summary>
    public ImageBlock(string mediaType, S3Location location) : this(mediaType, "") => Location = location ?? throw new ArgumentNullException(nameof(location));
    /// <summary>Creates an image referencing an HTTP or HTTPS URL.</summary>
    public ImageBlock(string mediaType, Uri url) : this(mediaType, "") => Url = url ?? throw new ArgumentNullException(nameof(url));
}
/// <summary>Audio content supplied as base64 bytes or an S3 location.</summary>
[method: JsonConstructor]
public sealed record AudioBlock(string MediaType, string Data) : ContentBlock
{
    /// <summary>An S3 source, mutually exclusive with nonempty Data.</summary>
    public S3Location? Location { get; init; }
    /// <summary>Creates audio referencing an S3 object.</summary>
    public AudioBlock(string mediaType, S3Location location) : this(mediaType, "") => Location = location ?? throw new ArgumentNullException(nameof(location));
}
/// <summary>A detached JSON value returned by a tool.</summary>
public sealed record JsonBlock(JsonElement Json) : ContentBlock;
/// <summary>A protocol resource reference supplied as tool output.</summary>
public sealed record ResourceLinkBlock(string Uri, string Name, string? MediaType = null, string? Description = null) : ContentBlock;
/// <summary>Opaque provider-redacted reasoning that must be replayed without decoding or alteration.</summary>
public sealed record RedactedReasoningBlock(string Data) : ContentBlock;
/// <summary>An explicit provider prompt-cache boundary, with an optional provider-supported duration.</summary>
public sealed record CachePointBlock(string? Ttl = null) : ContentBlock;
/// <summary>A base64 document and its media type, with an optional display name.</summary>
[method: JsonConstructor]
public sealed record DocumentBlock(string MediaType, string Data, string? Name = null) : ContentBlock
{
    /// <summary>An S3 source, mutually exclusive with nonempty Data.</summary>
    public S3Location? Location { get; init; }
    /// <summary>A plain text source, mutually exclusive with bytes, Location and Content.</summary>
    public string? Text { get; init; }
    /// <summary>Ordered text segments, mutually exclusive with bytes, Location and Text. Message boundaries copy this list.</summary>
    public IReadOnlyList<TextBlock>? Content { get; init; }
    /// <summary>Creates a UTF-8 plain text document.</summary>
    public static DocumentBlock FromText(string text, string? name = null) => new("text/plain", "", name) { Text = text ?? throw new ArgumentNullException(nameof(text)) };
    /// <summary>Creates a document containing ordered text segments.</summary>
    public static DocumentBlock FromContent(IReadOnlyList<TextBlock> content, string? name = null) => (DocumentBlock)MediaContent.Snapshot(new DocumentBlock("text/plain", "", name) { Content = content ?? throw new ArgumentNullException(nameof(content)) });
    /// <summary>Creates a document referencing an S3 object.</summary>
    public DocumentBlock(string mediaType, S3Location location, string? name = null) : this(mediaType, "", name) => Location = location ?? throw new ArgumentNullException(nameof(location));
    /// <summary>Optional document citation configuration for providers supporting document citations.</summary>
    public bool? CitationsEnabled { get; init; }
    /// <summary>Optional context accompanying the source document.</summary>
    public string? Context { get; init; }
}

/// <summary>Provider reasoning, including the signature needed for replay.</summary>
/// <param name="Text">The reasoning text supplied by the provider.</param>
/// <param name="Signature">An optional provider signature required by some providers when replaying reasoning.</param>
public sealed record ReasoningBlock(string Text, string? Signature = null) : ContentBlock;

/// <summary>An immutable snapshot of one conversation message.</summary>
public sealed record Message
{
    /// <summary>Copies content into an immutable message, cloning JSON tool input.</summary>
    /// <param name="role">The message's conversational role.</param>
    /// <param name="content">The ordered content blocks. Null blocks are rejected.</param>
    /// <param name="trackingId">An application-visible tracking identifier, or null to generate one.</param>
    /// <param name="metadata">Optional usage, metrics, and custom metadata, excluded from provider requests.</param>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The role is not defined.</exception>
    /// <exception cref="ArgumentException">A content block is null.</exception>
    [JsonConstructor]
    public Message(Role role, IReadOnlyList<ContentBlock> content, string? trackingId = null, JsonElement? metadata = null)
    {
        Guard.NotNull(content, nameof(content));
        if (!Enum.IsDefined(typeof(Role), role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Message metadata must be a JSON object.", nameof(metadata));
        Role = role;
        Content = Array.AsReadOnly(content.Select(CloneBlock).ToArray());
        TrackingId = trackingId ?? Guid.NewGuid().ToString();
        Metadata = metadata?.Clone();
    }

    /// <summary>The message's conversational role.</summary>
    public Role Role { get; }
    /// <summary>The message's ordered, immutable content snapshot.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
    /// <summary>The tracking identifier retained when the message is serialized.</summary>
    public string TrackingId { get; }
    /// <summary>Detached message metadata, persisted with snapshots and excluded from provider requests.</summary>
    public JsonElement? Metadata { get; }
    /// <summary>Concatenated display text, including Responses text/refusals; tool results and reasoning are excluded.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Content.Select(block => block switch { TextBlock text => text.Text, CitationsBlock citations => citations.Text, Models.ResponsesContentBlock response => response.Text, Models.GoogleContentBlock google => google.Text, Models.AnthropicContentBlock anthropic => anthropic.Text, _ => "" }));

    /// <summary>Creates a user message containing one text block.</summary>
    /// <param name="text">The user text.</param>
    /// <returns>A new user message.</returns>
    /// <exception cref="ArgumentNullException">Text is null.</exception>
    public static Message User(string text) => new(Role.User, [new TextBlock(text ?? throw new ArgumentNullException(nameof(text)))]);
    /// <summary>Creates an assistant message containing one text block.</summary>
    /// <param name="text">The assistant text.</param>
    /// <returns>A new assistant message.</returns>
    /// <exception cref="ArgumentNullException">Text is null.</exception>
    public static Message Assistant(string text) => new(Role.Assistant, [new TextBlock(text ?? throw new ArgumentNullException(nameof(text)))]);

    internal static ContentBlock CloneBlock(ContentBlock block) => block switch
    {
        ToolUseBlock tool => tool with { Input = tool.Input.Clone(), ProviderMetadata = tool.ProviderMetadata?.Clone() },
        ToolResultBlock result => result with { ContentBlocks = Array.AsReadOnly(result.ContentBlocks.Select(CloneBlock).ToArray()), StructuredContent = result.StructuredContent?.Clone(), Metadata = result.Metadata?.Clone() },
        JsonBlock json => json with { Json = json.Json.Clone() },
        ImageBlock or AudioBlock or DocumentBlock or VideoBlock => MediaContent.Snapshot(block),
        Models.ResponsesContentBlock response => new Models.ResponsesContentBlock(response.ItemId, response.ContentIndex, response.Part, response.Status, response.Item),
        Models.ResponsesReasoningBlock reasoning => new Models.ResponsesReasoningBlock(reasoning.Item),
        Models.ResponsesOutputItemBlock output => new Models.ResponsesOutputItemBlock(output.Item),
        Models.GoogleContentBlock google => new Models.GoogleContentBlock(google.Part),
        Models.AnthropicContentBlock anthropic => new Models.AnthropicContentBlock(anthropic.Content),
        CitationsBlock citations => new CitationsBlock(citations.Citations, citations.Content, citations.ProviderMetadata),
        null => throw new ArgumentException("Content cannot contain null blocks."),
        _ => block
    };
}

/// <summary>Provider and framework stop reasons, kept extensible for custom models.</summary>
public static class StopReasons
{
    /// <summary>An experimental cycle checkpoint was reported.</summary>
    public const string Checkpoint = "checkpoint";
    /// <summary>Execution paused for human input; resume by supplying interrupt responses without a new prompt.</summary>
    public const string Interrupt = "interrupt";
    /// <summary>The model completed its response.</summary>
    public const string EndTurn = "end_turn";
    /// <summary>The model requested tools, causing the agent to execute them and continue.</summary>
    public const string ToolUse = "tool_use";
    /// <summary>The provider reached the output limit for one model request.</summary>
    public const string MaxTokens = "max_tokens";
    /// <summary>The provider encountered a configured stop sequence.</summary>
    public const string StopSequence = "stop_sequence";
    /// <summary>The provider filtered the generated response.</summary>
    public const string ContentFiltered = "content_filtered";
    /// <summary>The invocation reached its model-turn limit after completing requested tools.</summary>
    public const string MaxTurns = "max_turns";
    /// <summary>The invocation was cancelled.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>The invocation reached its cumulative token limit.</summary>
    public const string MaxTotalTokens = "max_total_tokens";
    /// <summary>The invocation reached its cumulative output-token limit.</summary>
    public const string MaxOutputTokens = "max_output_tokens";
}

/// <summary>Token counts reported by model providers.</summary>
/// <param name="InputTokens">The provider's input-token count. Some providers report cache tokens separately.</param>
/// <param name="OutputTokens">The provider's output-token count.</param>
/// <param name="CacheReadInputTokens">Input tokens read from a provider cache.</param>
/// <param name="CacheWriteInputTokens">Input tokens written to a provider cache.</param>
/// <param name="ReportedTotalTokens">An explicit total that preserves provider accounting, or null to sum input and output.</param>
public sealed record Usage(long InputTokens = 0, long OutputTokens = 0, long CacheReadInputTokens = 0, long CacheWriteInputTokens = 0, long? ReportedTotalTokens = null)
{
    /// <summary>The explicit reported total when available; otherwise input plus output tokens.</summary>
    public long TotalTokens => ReportedTotalTokens ?? checked(InputTokens + OutputTokens);
    /// <summary>Combines usage counters and retains any explicit total-token accounting.</summary>
    /// <param name="left">The first set of usage counters.</param>
    /// <param name="right">The second set of usage counters.</param>
    /// <returns>The combined usage.</returns>
    public static Usage operator +(Usage left, Usage right)
    {
        checked
        {
            return new(left.InputTokens + right.InputTokens, left.OutputTokens + right.OutputTokens,
                left.CacheReadInputTokens + right.CacheReadInputTokens, left.CacheWriteInputTokens + right.CacheWriteInputTokens,
                left.ReportedTotalTokens is null && right.ReportedTotalTokens is null ? null : left.TotalTokens + right.TotalTokens);
        }
    }
}
