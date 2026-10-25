namespace Strands.Agents.Bidi;

/// <summary>Runs once after native session restoration and before model connection I/O.</summary>
/// <param name="Agent">The initialized conversation.</param>
public sealed record BidiInitializedEvent(BidiAgent Agent) : IBidiHookEvent;

/// <summary>An accepted history append. The message is detached and history already contains the complete atomic batch.</summary>
public sealed record BidiMessageAddedEvent : IBidiHookEvent
{
    /// <summary>Copies an accepted message for awaited observers.</summary>
    public BidiMessageAddedEvent(BidiAgent agent, Message message)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        if (message is null) throw new ArgumentNullException(nameof(message));
        Message = new(message.Role, message.Content, message.TrackingId, message.Metadata);
    }
    /// <inheritdoc/>
    public BidiAgent Agent { get; }
    /// <summary>The detached appended message, including its stable tracking identifier.</summary>
    public Message Message { get; }
}

/// <summary>An accepted history replacement preserving its original tracking identifier.</summary>
public sealed record BidiMessageUpdatedEvent : IBidiHookEvent
{
    /// <summary>Copies an accepted replacement for awaited observers.</summary>
    public BidiMessageUpdatedEvent(BidiAgent agent, Message message)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        if (message is null) throw new ArgumentNullException(nameof(message));
        Message = new(message.Role, message.Content, message.TrackingId, message.Metadata);
    }
    /// <inheritdoc/>
    public BidiAgent Agent { get; }
    /// <summary>The detached replacement message.</summary>
    public Message Message { get; }
    /// <summary>The original tracking identifier.</summary>
    public string TrackingId => Message.TrackingId;
}
