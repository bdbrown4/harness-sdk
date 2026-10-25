namespace Strands.Agents.Injection;

/// <summary>The detached model-visible conversation and borrowed application context for one injection.</summary>
public sealed class InjectionContext
{
    internal InjectionContext(Agent agent, IReadOnlyList<Message> messages)
    {
        Agent = agent;
        Messages = Array.AsReadOnly(messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
    }

    /// <summary>The agent receiving the ephemeral context.</summary>
    public Agent Agent { get; }
    /// <summary>A detached view of this call's ordered conversation.</summary>
    public IReadOnlyList<Message> Messages { get; }
    /// <summary>The agent's borrowed, durable application state; the injector adds no keys.</summary>
    public StateStore AppState => Agent.AppState;
}
