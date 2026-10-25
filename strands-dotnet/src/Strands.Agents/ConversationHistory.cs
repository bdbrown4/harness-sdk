namespace Strands.Agents;

internal static class ConversationHistory
{
    internal static void Validate(IReadOnlyList<Message> messages, ModelHistoryBehavior profile)
    {
        Guard.NotNull(messages, nameof(messages));
        if (!Enum.IsDefined(typeof(ModelHistoryBehavior), profile))
            throw new InvalidDataException("The stored history profile is unknown.");
        if (profile != ModelHistoryBehavior.Python || messages.FirstOrDefault() is not { } first ||
            !first.Content.OfType<ToolResultBlock>().Any())
        {
            Validate(messages);
            return;
        }
        var results = first.Content.OfType<ToolResultBlock>().ToArray();
        if (first.Role != Role.User || results.Length != first.Content.Count ||
            results.Any(result => string.IsNullOrWhiteSpace(result.ToolUseId) || !Enum.IsDefined(typeof(ToolResultStatus), result.Status)) ||
            results.Select(result => result.ToolUseId).Distinct(StringComparer.Ordinal).Count() != results.Length)
            throw new InvalidDataException("An independent Python history prefix requires only unique, nonempty, valid tool results.");
        Validate(Array.AsReadOnly(messages.Skip(1).ToArray()));
    }

    public static void Validate(IReadOnlyList<Message> messages)
    {
        Guard.NotNull(messages, nameof(messages));
        HashSet<string> pending = new(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is null) throw new InvalidDataException("Conversation messages cannot be null.");
            var uses = message.Content.OfType<ToolUseBlock>().ToArray();
            var results = message.Content.OfType<ToolResultBlock>().ToArray();
            if (results.Any(result => !Enum.IsDefined(typeof(ToolResultStatus), result.Status)))
                throw new InvalidDataException("Tool-result status must be success or error.");
            if (uses.Length > 0 && message.Role != Role.Assistant || results.Length > 0 && message.Role != Role.User)
                throw new InvalidDataException("Tool requests must be assistant content and tool results must be user content.");
            if (pending.Count > 0)
            {
                if (results.Length != pending.Count || results.Select(result => result.ToolUseId).Distinct(StringComparer.Ordinal).Count() != results.Length ||
                    !pending.SetEquals(results.Select(result => result.ToolUseId)))
                    throw new InvalidDataException("Every tool request must have exactly one correlated result in the following message.");
                pending.Clear();
            }
            else if (results.Length > 0) throw new InvalidDataException("Conversation contains tool results without preceding requests.");
            foreach (var use in uses)
                if (string.IsNullOrWhiteSpace(use.ToolUseId) || !pending.Add(use.ToolUseId))
                    throw new InvalidDataException("Tool-use identifiers must be nonempty and unique within each batch.");
        }
        // A final pending batch is a valid crash snapshot; the agent repairs it without repeating side effects.
    }
}
