namespace Strands.Agents;

/// <summary>Reduces model-visible history to a message window, extending it to retain the complete latest user exchange.</summary>
/// <remarks>A single long exchange can exceed the window. Durable history remains intact.</remarks>
public sealed class SlidingWindowConversationManager : IConversationManager
{
    private readonly int _maxMessages;

    /// <summary>Creates a history reducer with a target message-window size.</summary>
    /// <param name="maxMessages">A positive target size. The window expands to preserve the latest user exchange.</param>
    /// <exception cref="ArgumentOutOfRangeException">The target size is less than one.</exception>
    public SlidingWindowConversationManager(int maxMessages = 40)
    {
        if (maxMessages < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        _maxMessages = maxMessages;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">Messages is null.</exception>
    public IReadOnlyList<Message> Reduce(IReadOnlyList<Message> messages)
    {
        Guard.NotNull(messages, nameof(messages));
        var start = Math.Max(0, messages.Count - _maxMessages);
        while (start > 0 && !IsUserPrompt(messages[start])) start--;
        return Array.AsReadOnly(messages.Skip(start).ToArray());
    }

    private static bool IsUserPrompt(Message message) => message.Role == Role.User && !message.Content.OfType<ToolResultBlock>().Any();
}
