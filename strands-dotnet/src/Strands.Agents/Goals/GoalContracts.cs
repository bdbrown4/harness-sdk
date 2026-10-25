namespace Strands.Agents.Goals;

/// <summary>Judges one completed assistant response against an application goal.</summary>
/// <param name="response">The detached completed assistant response.</param>
/// <param name="agent">The agent whose attempt is being evaluated.</param>
/// <param name="cancellationToken">The original invocation cancellation signal.</param>
/// <returns>The verdict and optional corrective feedback.</returns>
public delegate ValueTask<ValidationOutcome> GoalValidator(Message response, Agent agent, CancellationToken cancellationToken);

/// <summary>A goal verdict with optional feedback for the next attempt.</summary>
/// <param name="Passed">Whether every requirement is satisfied.</param>
/// <param name="Feedback">Specific changes required when the attempt failed.</param>
public sealed record ValidationOutcome(bool Passed, string? Feedback = null);

/// <summary>Why an iterative goal run completed.</summary>
public enum GoalStopReason
{
    /// <summary>The validator accepted the response.</summary>
    Satisfied,
    /// <summary>The configured number of attempts was exhausted.</summary>
    MaxAttempts,
    /// <summary>The between-attempt wall-clock budget was exhausted.</summary>
    Timeout
}

/// <summary>The outcome of one numbered goal attempt.</summary>
/// <param name="Attempt">The one-based attempt number.</param>
/// <param name="Passed">Whether the validator accepted this attempt.</param>
/// <param name="Feedback">The validator's corrective feedback.</param>
public sealed record GoalAttempt(int Attempt, bool Passed, string? Feedback = null);

/// <summary>An immutable summary of a completed goal run.</summary>
public sealed record GoalResult
{
    /// <summary>Copies the completed attempt summaries.</summary>
    /// <param name="passed">Whether the goal was satisfied.</param>
    /// <param name="stopReason">Why evaluation ended.</param>
    /// <param name="attempts">The ordered completed attempts.</param>
    public GoalResult(bool passed, GoalStopReason stopReason, IReadOnlyList<GoalAttempt> attempts)
    {
        Guard.NotNull(attempts, nameof(attempts));
        Passed = passed;
        StopReason = stopReason;
        Attempts = Array.AsReadOnly(attempts.ToArray());
    }

    /// <summary>Whether the goal was satisfied.</summary>
    public bool Passed { get; }
    /// <summary>Why evaluation ended.</summary>
    public GoalStopReason StopReason { get; }
    /// <summary>The ordered immutable attempt summaries.</summary>
    public IReadOnlyList<GoalAttempt> Attempts { get; }
}
