using System.Text;
using System.Text.Json;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private IBidiOutputEvent? ProcessContent(IBidiOutputEvent output, Dictionary<string, PendingContent> blocks)
    {
        switch (output)
        {
            case BidiTextStartEvent start: StartContent(start.ContentId, "text", Role.Assistant, blocks); break;
            case BidiReasoningStartEvent start: StartContent(start.ContentId, "reasoning", Role.Assistant, blocks); break;
            case BidiTranscriptStartEvent start: StartContent(start.ContentId, "transcript", start.Role, blocks); break;
            case BidiTextDeltaEvent delta: GetContent(delta.ContentId, "text", Role.Assistant, blocks).Text.Append(delta.Delta); break;
            case BidiReasoningDeltaEvent delta: GetContent(delta.ContentId, "reasoning", Role.Assistant, blocks).Text.Append(delta.Delta); break;
            case BidiTranscriptDeltaEvent delta: GetContent(delta.ContentId, "transcript", delta.Role, blocks).Text.Append(delta.Delta); break;
            case BidiTextStopEvent stop:
                var text = CloseContent(stop.ContentId, "text", Role.Assistant, blocks); return new BidiTextBlockEvent(text, stop.ContentId);
            case BidiReasoningStopEvent stop:
                var reasoning = CloseContent(stop.ContentId, "reasoning", Role.Assistant, blocks); return new BidiReasoningBlockEvent(reasoning, stop.ContentId);
            case BidiTranscriptStopEvent stop:
                var transcript = CloseContent(stop.ContentId, "transcript", stop.Role, blocks); return new BidiTranscriptBlockEvent(transcript, stop.Role, stop.ContentId);
        }
        return null;
    }
    private void StartContent(string id, string kind, Role role, Dictionary<string, PendingContent> blocks)
    {
        if (string.IsNullOrWhiteSpace(id) || blocks.ContainsKey(id) || role is not (Role.User or Role.Assistant)) throw new ModelException("The bidirectional model supplied an invalid or repeated content start.");
        var message = new Message(role, [], metadata: ContentMetadata(kind, "pending"));
        lock (_gate)
        {
            blocks.Add(id, new(kind, role, _messages.Count, message.TrackingId)); _messages.Add(message);
            QueueHistoryLocked([new BidiMessageAddedEvent(this, message)], CancellationToken.None);
        }
    }
    private static PendingContent GetContent(string id, string kind, Role role, Dictionary<string, PendingContent> blocks)
        => blocks.TryGetValue(id, out var block) && block.Kind == kind && block.Role == role ? block : throw new ModelException("The bidirectional model supplied content without its matching start.");
    private string CloseContent(string id, string kind, Role role, Dictionary<string, PendingContent> blocks)
    {
        var block = GetContent(id, kind, role, blocks); blocks.Remove(id); CommitContent(block, true); return block.Text.ToString();
    }
    private void CommitContent(PendingContent block, bool complete)
    {
        var text = block.Kind == "transcript" && !complete ? "[Transcript unavailable.]" : block.Text.ToString();
        ContentBlock content = block.Kind == "reasoning" ? new ReasoningBlock(text) : new TextBlock(text);
        var message = new Message(block.Role, [content], block.TrackingId, ContentMetadata(block.Kind, complete ? "complete" : "incomplete"));
        lock (_gate) { _messages[block.Index] = message; QueueHistoryLocked([new BidiMessageUpdatedEvent(this, message)], CancellationToken.None); }
    }
    private static JsonElement ContentMetadata(string kind, string status) => JsonSerializer.SerializeToElement(new { custom = new { bidi = new { kind, status } } });
    private sealed class PendingContent(string kind, Role role, int index, string trackingId)
    {
        internal string Kind { get; } = kind;
        internal Role Role { get; } = role;
        internal int Index { get; } = index;
        internal string TrackingId { get; } = trackingId;
        internal StringBuilder Text { get; } = new();
    }
}
