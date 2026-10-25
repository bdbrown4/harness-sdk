namespace Strands.Agents;

/// <summary>Detached input for an invocation cycle requested by an after-invocation hook. Exactly one input form is selected at construction.</summary>
public sealed record InvocationContinuation
{
    /// <summary>Continues using existing history without appending new input.</summary>
    public static InvocationContinuation Continue { get; } = new(Array.Empty<Message>());

    /// <summary>Continues with a new user text message, including an explicitly empty string.</summary>
    public InvocationContinuation(string prompt)
    {
        Guard.NotNull(prompt, nameof(prompt));
        Messages = Array.AsReadOnly(new[] { Message.User(prompt) });
    }

    /// <summary>Continues with a detached, ordered history batch. An empty batch appends nothing. Tool exchanges must be correlated.</summary>
    public InvocationContinuation(IReadOnlyList<Message> messages)
    {
        Guard.NotNull(messages, nameof(messages));
        var detached = messages.Select(message => message is null ? throw new ArgumentException("Continuation messages cannot be null.", nameof(messages)) :
            new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray();
        ConversationHistory.Validate(detached);
        Messages = Array.AsReadOnly(detached);
    }

    /// <summary>Continues with detached user content, including media. An empty list appends nothing; tool protocol blocks are rejected.</summary>
    public InvocationContinuation(IReadOnlyList<ContentBlock> content)
    {
        Guard.NotNull(content, nameof(content));
        var message = new Message(Role.User, content);
        if (message.Content.Any(block => block is ToolUseBlock or ToolResultBlock))
            throw new ArgumentException("User content cannot contain tool requests or tool results.", nameof(content));
        Messages = content.Count == 0 ? Array.Empty<Message>() : Array.AsReadOnly(new[] { message });
    }

    /// <summary>Continues a paused tool batch with detached human answers. Identifiers must be unique and target outstanding interrupts.</summary>
    public InvocationContinuation(IReadOnlyList<InterruptResponse> interruptResponses)
    {
        Guard.NotNull(interruptResponses, nameof(interruptResponses));
        var detached = interruptResponses.Select(response => response is null ? throw new ArgumentException("Interrupt responses cannot be null.", nameof(interruptResponses)) :
            new InterruptResponse(response.InterruptId, response.Response)).ToArray();
        if (detached.Select(response => response.InterruptId).Distinct(StringComparer.Ordinal).Count() != detached.Length)
            throw new ArgumentException("A continuation cannot provide duplicate interrupt responses.", nameof(interruptResponses));
        InterruptResponses = Array.AsReadOnly(detached);
    }

    internal IReadOnlyList<Message> Messages { get; } = Array.Empty<Message>();
    internal IReadOnlyList<InterruptResponse> InterruptResponses { get; } = Array.Empty<InterruptResponse>();
}
