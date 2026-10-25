namespace Strands.Agents.Bidi;

/// <summary>A lifecycle notification from a real persistent bidirectional agent.</summary>
public interface IBidiHookEvent
{
    /// <summary>The conversation whose lifecycle produced this notification.</summary>
    BidiAgent Agent { get; }
}

/// <summary>Runs after an active conversation's model and background cleanup have been attempted.</summary>
/// <param name="Agent">The conversation being stopped.</param>
public sealed record BidiAgentStopEvent(BidiAgent Agent) : IBidiHookEvent;

/// <summary>Runs only when the model reports an actual completed response.</summary>
/// <param name="Agent">The conversation receiving the completion.</param>
/// <param name="ResponseId">The model's completed response identifier.</param>
public sealed record BidiResponseStopHookEvent(BidiAgent Agent, string ResponseId) : IBidiHookEvent;

/// <summary>Runs when the model reports that a user interrupted its current response.</summary>
/// <param name="Agent">The conversation receiving the interruption.</param>
public sealed record BidiBargeInHookEvent(BidiAgent Agent) : IBidiHookEvent;

/// <summary>Runs after sends have been gated and before any connection replacement is attempted.</summary>
/// <param name="Agent">The conversation replacing its connection.</param>
/// <param name="Reason">The source of the connection replacement.</param>
/// <param name="TimeoutError">The original reactive timeout, or null for a scheduled replacement.</param>
public sealed record BidiBeforeConnectionRestartEvent(BidiAgent Agent, BidiRestartReason Reason, BidiConnectionTimeoutException? TimeoutError = null) : IBidiHookEvent;

/// <summary>Runs after an attempted connection replacement, including a failed attempt.</summary>
/// <param name="Agent">The conversation replacing its connection.</param>
/// <param name="Reason">The source of the connection replacement.</param>
/// <param name="Error">The replacement failure, or null after a successful replacement.</param>
public sealed record BidiAfterConnectionRestartEvent(BidiAgent Agent, BidiRestartReason Reason, Exception? Error = null) : IBidiHookEvent;
