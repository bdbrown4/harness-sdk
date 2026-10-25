using System.Text.Json;

namespace Strands.Agents.SessionCatalogs;

internal static class SnapshotSessionCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static byte[] Write(SessionSnapshot snapshot, string session, string agent, string? id, bool stateful)
    {
        Validate(snapshot, agent, stateful);
        return JsonSerializer.SerializeToUtf8Bytes(new CatalogEnvelope(1, session, agent, id, stateful, snapshot), Json);
    }

    internal static SessionSnapshot Read(byte[] bytes, string session, string agent, string? id, bool stateful)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<CatalogEnvelope>(bytes, Json) ?? throw new InvalidDataException("The native catalog record cannot be null.");
            if (envelope.Version != 1 || envelope.SessionId != session || envelope.AgentId != agent || envelope.SnapshotId != id || envelope.Stateful != stateful)
                throw new InvalidDataException("The native catalog envelope does not match the requested session, agent, handle or model profile.");
            Validate(envelope.Snapshot, agent, stateful);
            return envelope.Snapshot;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("Malformed native snapshot catalog data.", error); }
    }

    private static void Validate(SessionSnapshot snapshot, string agent, bool stateful)
    {
        if (snapshot is null || snapshot.SchemaVersion is not (1 or 2) || snapshot.AgentId != agent || snapshot.Messages is null || snapshot.State is null || snapshot.ModelState is null || !snapshot.HasSystemPrompt)
            throw new InvalidDataException("A complete native catalog snapshot requires version, matching identity, messages and both state dictionaries.");
        if (stateful != snapshot.ModelHistoryCount.HasValue)
            throw new InvalidDataException("A native catalog's server-history cursor must match its model profile.");
        snapshot.ValidateHistoryProfile();
        ConversationHistory.Validate(snapshot.Messages, snapshot.HistoryProfile);
        foreach (var message in snapshot.Messages) ValidateContent(message.Content);
        snapshot.ValidateModelHistory();
        snapshot.ValidateModelInputReceipt();
        if (snapshot.ModelHistoryCount is > 0 && snapshot.ModelState.Count == 0)
            throw new InvalidDataException("Acknowledged server history requires provider conversation state.");
        snapshot.InterruptState?.ValidateHistory(snapshot.Messages, allowIndependent: true, independentOwnerProfile: snapshot.HistoryProfile == ModelHistoryBehavior.Native ? (ModelHistoryBehavior?)null : snapshot.HistoryProfile);
        _ = snapshot.CopyValidatedSystemPromptContent(allowGuardContent: true);
        _ = AgentSnapshot.CloneValues(snapshot.State, nameof(snapshot.State));
        _ = AgentSnapshot.CloneValues(snapshot.ModelState, nameof(snapshot.ModelState));
        _ = Context.StashCheckpoint.Prepare(snapshot.ContextStash, snapshot.ContextStashStorageType);
        if (snapshot.ConversationManagerState is { ValueKind: not JsonValueKind.Null } classic)
            _ = Conversation.ConversationManager.ValidateEnvelope(classic);
    }

    private static void ValidateContent(IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is null or TextBlock { Text: null }) throw new InvalidDataException("Catalog content requires nonnull blocks and text.");
            if (block is ToolResultBlock result) ValidateContent(result.ContentBlocks);
            _ = Message.CloneBlock(block);
        }
    }

    internal static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')))
            throw new ArgumentException("Catalog identifiers require a non-dot ASCII letter, digit, underscore, hyphen or dot segment.", parameter);
    }

    private sealed record CatalogEnvelope(int Version, string SessionId, string AgentId, string? SnapshotId, bool? Stateful, SessionSnapshot Snapshot);
}
