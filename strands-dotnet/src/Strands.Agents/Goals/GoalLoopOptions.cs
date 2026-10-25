namespace Strands.Agents.Goals;

/// <summary>Budgets, judging and retry input for an iterative goal run.</summary>
public sealed record GoalLoopOptions
{
    /// <summary>The plugin identifier. Only one goal loop may bind to an agent.</summary>
    public string Name { get; init; } = "strands:goal-loop";
    /// <summary>A positive attempt limit; null permits unbounded attempts subject to ordinary invocation limits.</summary>
    public int? MaxAttempts { get; init; }
    /// <summary>A positive budget checked between attempts before judging; null is unbounded. An in-flight attempt is not interrupted by this budget.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Whether retries retain prior responses and feedback. False restores model-visible session state while retaining application state and accepted external effects.</summary>
    public bool PreserveContext { get; init; } = true;
    /// <summary>The borrowed natural-language judge model: this model &gt; agent.AuxModel &gt; agent.Model. Ignored for programmatic validators.</summary>
    public IModel? JudgeModel { get; init; }
    /// <summary>Optional natural-language judge instructions; null uses the shared strict goal-evaluation prompt.</summary>
    public string? JudgeSystemPrompt { get; init; }
    /// <summary>Builds detached retry input from trimmed feedback; null uses the shared corrective prompt.</summary>
    public Func<string?, InvocationContinuation>? ResumePromptTemplate { get; init; }
}
