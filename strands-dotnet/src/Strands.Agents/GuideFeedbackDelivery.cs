namespace Strands.Agents;

/// <summary>How model-boundary intervention feedback enters the agent's history.</summary>
public enum GuideFeedbackDelivery
{
    /// <summary>Commit feedback through ordinary message callbacks and durable session handling.</summary>
    Native = 0,
    /// <summary>Append tracked feedback directly, bypassing its message-added callbacks and immediate session write.</summary>
    DirectAppend = 1
}
