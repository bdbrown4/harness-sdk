namespace Strands.Agents.SessionRepositories;

internal static class RepositoryHistoryRepair
{
    // Only indexed stateless restoration opts into this source repair. Whole snapshots stay strict.
    internal static PreparedRepositoryHistory Prepare(IReadOnlyList<Message> original)
    {
        var messages = original.ToList();
        var replacements = new List<(Message Original, Message Replacement)>();
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != Role.User || !message.Content.OfType<ToolResultBlock>().Any() ||
                index > 0 && messages[index - 1].Content.OfType<ToolUseBlock>().Any()) continue;
            var retained = message.Content.Where(block => block is not ToolResultBlock).ToArray();
            if (retained.Length == 0) messages.RemoveAt(index);
            else
            {
                messages[index] = new(message.Role, retained, message.TrackingId, message.Metadata);
                replacements.Add((message, messages[index]));
            }
        }
        for (var index = messages.Count - 2; index >= 0; index--)
        {
            var calls = messages[index].Content.OfType<ToolUseBlock>().ToArray();
            if (calls.Length == 0) continue;
            var next = messages[index + 1];
            var results = next.Content.OfType<ToolResultBlock>().ToArray();
            var existing = new Dictionary<string, ToolResultBlock>(StringComparer.Ordinal);
            foreach (var result in results) existing[result.ToolUseId] = result;
            if (calls.Length == results.Length && calls.Select(call => call.ToolUseId).Distinct(StringComparer.Ordinal).Count() == calls.Length &&
                new HashSet<string>(existing.Keys, StringComparer.Ordinal).SetEquals(calls.Select(call => call.ToolUseId))) continue;
            var other = next.Content.Where(block => block is not ToolResultBlock).ToArray();
            if (results.Length == 0 && other.Length > 0)
            {
                next = new(Role.User, []);
                messages.Insert(index + 1, next);
                other = [];
            }
            var content = calls.Select(call => (ContentBlock)(existing.TryGetValue(call.ToolUseId, out var result) ? result :
                new ToolResultBlock(call.ToolUseId, "Tool execution was interrupted; its result is unavailable.", ToolResultStatus.Error))).Concat(other).ToArray();
            messages[index + 1] = new(next.Role, content, next.TrackingId, next.Metadata);
            replacements.Add((next, messages[index + 1]));
        }
        ConversationHistory.Validate(messages);
        return new(messages, replacements);
    }
}

internal sealed record PreparedRepositoryHistory(IReadOnlyList<Message> Messages, IReadOnlyList<(Message Original, Message Replacement)> Replacements);
