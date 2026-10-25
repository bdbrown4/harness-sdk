namespace Strands.Agents.Context;

internal static class OffloadHistory
{
    internal static SortedSet<int> SafeRemoval(IReadOnlyList<Message> messages, IEnumerable<int> candidates, ISet<int>? protectedIndices = null)
    {
        var selected = new SortedSet<int>();
        foreach (var index in candidates)
        {
            if (Protected(messages, index, protectedIndices)) continue;
            var message = messages[index];
            var partner = -1;
            if (message.Content.OfType<ToolResultBlock>().Any()) partner = index - 1;
            if (message.Content.OfType<ToolUseBlock>().Any()) partner = index + 1;
            if (partner >= 0 && Protected(messages, partner, protectedIndices)) continue;
            selected.Add(index);
            if (partner >= 0) selected.Add(partner);
        }
        return selected;
    }

    private static bool Protected(IReadOnlyList<Message> messages, int index, ISet<int>? protectedIndices) => index <= 0 || index >= messages.Count - 1 || protectedIndices?.Contains(index) == true || MessagePins.IsPinned(messages, index);

    internal static void RemoveAndMark(List<Message> messages, SortedSet<int> selected, string marker)
    {
        if (selected.Count == 0) return;
        var lowest = selected.Min;
        foreach (var index in selected.Reverse()) messages.RemoveAt(index);
        InsertMarker(messages, lowest, marker);
        RepairAlternation(messages);
    }

    internal static void InsertMarker(List<Message> messages, int index, string marker)
    {
        index = Math.Max(1, Math.Min(index, messages.Count));
        foreach (var neighbor in new[] { index - 1, index })
        {
            if (neighbor <= 0 || neighbor >= messages.Count || messages[neighbor].Role != Role.Assistant) continue;
            var carrier = messages[neighbor];
            messages[neighbor] = new(carrier.Role, carrier.Content.Concat(new[] { new TextBlock(marker) }).ToArray(), carrier.TrackingId, carrier.Metadata);
            return;
        }
        messages.Insert(index, Message.Assistant(marker));
    }

    internal static void RepairAlternation(List<Message> messages)
    {
        for (var index = 1; index < messages.Count;)
        {
            var previous = messages[index - 1];
            var current = messages[index];
            if (previous.Role != current.Role) { index++; continue; }
            var pinned = MessagePins.IsPinned(messages, index - 1) || MessagePins.IsPinned(messages, index);
            var merged = new Message(previous.Role, previous.Content.Concat(current.Content).ToArray(), previous.TrackingId, previous.Metadata);
            messages[index - 1] = pinned ? MessagePins.Pin(merged) : merged;
            messages.RemoveAt(index);
        }
    }
}
