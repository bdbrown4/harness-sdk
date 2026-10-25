using Strands.Agents;

namespace Strands.Harness.Plugins;

internal static class ContextInjection
{
    internal static bool IsUserTurn(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return false;
        var last = messages[messages.Count - 1];
        return last.Role == Role.User && !last.Content.OfType<ToolResultBlock>().Any();
    }

    internal static ModelRequest Fold(ModelRequest request, string text)
    {
        var messages = request.Messages.ToArray();
        for (var index = messages.Length - 1; index >= 0; index--)
        {
            var target = messages[index];
            if (target.Role != Role.User) continue;
            var separator = target.Content.Count > 0 && !text.StartsWith("\n", StringComparison.Ordinal) ? "\n\n" : string.Empty;
            messages[index] = new Message(target.Role, target.Content.Concat(new[] { new TextBlock(separator + text) }).ToArray(), target.TrackingId, target.Metadata);
            return request with { Messages = Array.AsReadOnly(messages) };
        }
        return request;
    }
}
