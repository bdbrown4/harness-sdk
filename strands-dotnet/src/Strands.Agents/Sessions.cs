namespace Strands.Agents;

/// <summary>A structural record of local input consumed by a completed server-managed response that was omitted from local history. This is not an authenticated provider attestation.</summary>
/// <param name="ConsumedMessageCount">The complete local prefix consumed by that response.</param>
/// <param name="LastConsumedTrackingId">The exact ordinal tracking identifier at that prefix boundary.</param>
/// <param name="DiscardedResponseTrackingId">The original completed response tracking identifier, absent from retained history.</param>
public sealed record ModelInputReceipt(int ConsumedMessageCount, string LastConsumedTrackingId, string DiscardedResponseTrackingId);

/// <summary>A local .NET snapshot. Its schema is independent of the Python and TypeScript snapshot formats.</summary>
/// <param name="SchemaVersion">The native snapshot schema version: one without an input receipt, or two with its complete provider-state pair.</param>
/// <param name="Messages">The complete conversation to persist or restore.</param>
public sealed record SessionSnapshot(int SchemaVersion, IReadOnlyList<Message> Messages)
{
    /// <summary>The explicit validation domain of committed messages. Native preserves paired history; retained profiles do not grant provider acknowledgement or change the receiving agent's configured profile.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ModelHistoryBehavior HistoryProfile { get; init; }
    /// <summary>Optional application state. Absent values preserve compatibility with earlier message-only snapshots.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? State { get; init; }
    /// <summary>Optional provider conversation state. Absent values preserve earlier snapshot compatibility.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ModelState { get; init; }
    /// <summary>Whether SystemPrompt is present, including an explicitly null prompt. False preserves the configured prompt when restoring an earlier snapshot.</summary>
    public bool HasSystemPrompt { get; init; }
    /// <summary>The agent's prompt when HasSystemPrompt is true; null explicitly clears it.</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>Optional structured text/cache-point/guard prompt retained by compatible ordinary agent hosts. Requires HasSystemPrompt and a matching SystemPrompt text projection; null preserves earlier string-only snapshots.</summary>
    public IReadOnlyList<ContentBlock>? SystemPromptContent { get; init; }
    /// <summary>An optional interrupted tool batch, preserving accepted side effects and unanswered requests across restore.</summary>
    public InterruptState? InterruptState { get; init; }
    /// <summary>The acknowledged message-prefix length for server-managed history. Absent values identify earlier snapshots whose stale local history is discarded for stateful models.</summary>
    public int? ModelHistoryCount { get; init; }
    /// <summary>Optional version-two input-consumption metadata, paired with provider state and a preserved legacy cursor.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModelInputReceipt? ModelInputReceipt { get; init; }
    /// <summary>The stable agent identity used by automatically scoped storage; absent in earlier snapshots.</summary>
    public string? AgentId { get; init; }
    /// <summary>Original context entries embedded only for ephemeral stash backends; absent in earlier snapshots.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ContextStash { get; init; }
    /// <summary>The diagnostic backend type for externally persisted context originals; mutually exclusive with inline ContextStash entries. Absent in earlier snapshots.</summary>
    public string? ContextStashStorageType { get; init; }
    /// <summary>Optional Python classic conversation-manager checkpoint; absent in older sessions and independent of first-class context-manager transient state.</summary>
    public System.Text.Json.JsonElement? ConversationManagerState { get; init; }

    internal IReadOnlyList<ContentBlock>? CopyValidatedSystemPromptContent()
        => CopyValidatedSystemPromptContent(allowGuardContent: false);

    internal IReadOnlyList<ContentBlock>? CopyValidatedSystemPromptContent(bool allowGuardContent)
    {
        var content = CopySystemPromptContent(SystemPromptContent, allowGuardContent);
        if (content is not null && (!HasSystemPrompt || SystemPrompt != GetSystemPromptText(content)))
            throw new InvalidDataException("Structured system content requires a present, matching text projection.");
        return content;
    }

    internal static IReadOnlyList<ContentBlock>? CopySystemPromptContent(IReadOnlyList<ContentBlock>? content)
        => CopySystemPromptContent(content, allowGuardContent: false);

    internal static IReadOnlyList<ContentBlock>? CopySystemPromptContent(IReadOnlyList<ContentBlock>? content, bool allowGuardContent)
    {
        if (content is null) return null;
        var copy = new ContentBlock[content.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            var block = content[index];
            if ((block is not (TextBlock or CachePointBlock) && !(allowGuardContent && block is GuardContentBlock)) || block is TextBlock { Text: null })
                throw new InvalidDataException(allowGuardContent
                    ? "System content accepts only nonnull text, cache-point and guard blocks."
                    : "System content accepts only nonnull text and cache-point blocks.");
            copy[index] = Message.CloneBlock(block);
        }
        return Array.AsReadOnly(copy);
    }

    internal static string? GetSystemPromptText(IReadOnlyList<ContentBlock>? content)
    {
        if (content is null) return null;
        var text = content.OfType<TextBlock>().Select(block => block.Text).ToArray();
        return text.Length == 0 ? null : string.Join("\n", text);
    }

    internal void ValidateHistoryProfile()
    {
        if (!Enum.IsDefined(typeof(ModelHistoryBehavior), HistoryProfile))
            throw new InvalidDataException("The stored history profile is unknown.");
        if (HistoryProfile != ModelHistoryBehavior.Native && (ModelHistoryCount is not null || ModelInputReceipt is not null))
            throw new InvalidDataException("Retained-history snapshots cannot contain native acknowledgement metadata.");
    }

    internal void ValidateModelHistory()
    {
        if (ModelHistoryCount is not { } count) return;
        if (count < 0 || count > Messages.Count || count > 0 && Messages[count - 1].Role != Role.Assistant)
            throw new InvalidDataException("The server-history cursor must end at an acknowledged assistant message.");
    }

    // Requires the complete conversation and its unchanged legacy cursor to have been validated first.
    internal void ValidateModelInputReceipt()
    {
        if (SchemaVersion == 1)
        {
            if (ModelInputReceipt is not null) throw new InvalidDataException("A version-one session cannot contain an input receipt.");
            return;
        }
        if (SchemaVersion != 2) throw new InvalidDataException($"Unsupported .NET session schema version: {SchemaVersion}.");
        var receipt = ModelInputReceipt ?? throw new InvalidDataException("A version-two session requires an input receipt.");
        if (ModelHistoryCount is not { } cursor || receipt.ConsumedMessageCount <= cursor ||
            receipt.ConsumedMessageCount < 1 || receipt.ConsumedMessageCount > Messages.Count)
            throw new InvalidDataException("An input receipt must extend a present acknowledged prefix within retained history.");
        var boundary = Messages[receipt.ConsumedMessageCount - 1];
        if (boundary.Role != Role.User || receipt.LastConsumedTrackingId is null ||
            !StringComparer.Ordinal.Equals(receipt.LastConsumedTrackingId, boundary.TrackingId))
            throw new InvalidDataException("An input receipt must identify its exact retained user boundary.");
        if (string.IsNullOrEmpty(receipt.DiscardedResponseTrackingId) ||
            Messages.Any(message => StringComparer.Ordinal.Equals(message.TrackingId, receipt.DiscardedResponseTrackingId)))
            throw new InvalidDataException("The discarded response identifier must be nonempty and absent from retained history.");
        if (ModelState is not { Count: > 0 }) throw new InvalidDataException("An input receipt requires its provider conversation state.");
    }
}

/// <summary>Persists a conversation by opaque session identifier.</summary>
public interface ISessionStore
{
    /// <summary>Loads a previously saved conversation by its session key.</summary>
    /// <param name="sessionId">The opaque, nonempty session key.</param>
    /// <param name="cancellationToken">Cancellation for the storage operation.</param>
    /// <returns>The stored snapshot, or null when the session does not exist.</returns>
    ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Saves a conversation under its session key.</summary>
    /// <param name="sessionId">The opaque, nonempty session key.</param>
    /// <param name="snapshot">The complete conversation snapshot to persist.</param>
    /// <param name="cancellationToken">Cancellation for the storage operation.</param>
    /// <returns>A task that completes when persistence finishes.</returns>
    ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Reduces the model-visible conversation while retaining complete tool exchanges.</summary>
public interface IConversationManager
{
    /// <summary>Selects the model-visible conversation while preserving complete tool request/result exchanges.</summary>
    /// <param name="messages">The complete conversation, which must remain unmodified.</param>
    /// <returns>The ordered messages to send to the model.</returns>
    IReadOnlyList<Message> Reduce(IReadOnlyList<Message> messages);
}
