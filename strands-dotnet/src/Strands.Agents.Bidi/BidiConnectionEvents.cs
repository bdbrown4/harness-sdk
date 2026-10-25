namespace Strands.Agents.Bidi;

/// <summary>Why an automatic connection replacement was requested.</summary>
public enum BidiRestartReason
{
    /// <summary>The active model reported its connection deadline.</summary>
    Timeout,
    /// <summary>The declared proactive restart interval elapsed.</summary>
    Scheduled
}

/// <summary>An accepted automatic connection replacement, emitted before the model is restarted.</summary>
/// <param name="Reason">The trigger for replacement.</param>
/// <param name="TimeoutError">The original deadline failure on the reactive path.</param>
/// <param name="TurnInterrupted">Whether an active or unanswered turn is being interrupted.</param>
public sealed record BidiConnectionRestartEvent(BidiRestartReason Reason, BidiConnectionTimeoutException? TimeoutError = null, bool TurnInterrupted = false) : IBidiOutputEvent;

/// <summary>An informational warning before the declared proactive restart deadline.</summary>
/// <param name="TimeLeft">The approximate interval remaining when the warning was scheduled.</param>
public sealed record BidiConnectionWarningEvent(TimeSpan TimeLeft) : IBidiOutputEvent;

/// <summary>A requested conversation stop notification, emitted before resource cleanup.</summary>
/// <param name="ConnectionId">The active connection identifier.</param>
/// <param name="Reason">The stop reason; user_request identifies a completed cooperative request.</param>
public sealed record BidiConnectionStopEvent(string ConnectionId, string Reason) : IBidiOutputEvent;
