namespace Strands.Agents.Tasks;

/// <summary>Opt-in background tool execution. A null agent setting disables the feature.</summary>
public sealed record BackgroundTasksConfig
{
    /// <summary>Wait for queued or physically running work before an invocation returns. Defaults to true.</summary>
    public bool WaitForCompletion { get; init; } = true;
    /// <summary>Registered tool names whose execution mode the model selects. Null defaults to all eligible tools unless Always or Never supplies a wildcard.</summary>
    public IReadOnlyList<string>? Agentic { get; init; }
    /// <summary>Registered tool names that always run in the background. An asterisk selects eligible tools.</summary>
    public IReadOnlyList<string> Always { get; init; } = [];
    /// <summary>Registered tool names that always run in the foreground. An asterisk selects all tools.</summary>
    public IReadOnlyList<string> Never { get; init; } = [];
    /// <summary>Maximum physically executing tasks, including cancelled or timed-out bodies still unwinding. Defaults to four.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Per-execution deadline, starting when a queued task begins. Null means no deadline.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Validates physical limits and conflicting tool-name assignments without executing tools or resolving dependencies.</summary>
    public void Validate() { BackgroundTaskScheduler.ValidateLimits(MaxConcurrency, Timeout); _ = new BackgroundTaskPolicy(this); }
}
