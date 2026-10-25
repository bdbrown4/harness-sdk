namespace Strands.Agents.Telemetry;

/// <summary>Provider performance measurements with explicit duration units.</summary>
/// <param name="Latency">The duration of the model request.</param>
/// <param name="TimeToFirstToken">Time until the first response token, when reported.</param>
public sealed record ModelPerformanceMetrics(TimeSpan Latency, TimeSpan? TimeToFirstToken = null);

/// <summary>A detached snapshot of one event-loop cycle.</summary>
/// <param name="CycleId">The meter-local cycle identifier.</param>
/// <param name="Duration">Elapsed time, or zero before completion.</param>
/// <param name="Usage">Usage accumulated for this cycle.</param>
public sealed record AgentLoopMetrics(string CycleId, TimeSpan Duration, Usage Usage);

/// <summary>A detached snapshot of one invocation's cycles and usage.</summary>
public sealed class AgentInvocationMetrics
{
    internal AgentInvocationMetrics(IReadOnlyList<AgentLoopMetrics> cycles, Usage usage)
    {
        Cycles = Array.AsReadOnly(cycles.ToArray());
        Usage = usage;
    }

    /// <summary>The invocation's cycles in start order.</summary>
    public IReadOnlyList<AgentLoopMetrics> Cycles { get; }
    /// <summary>Accumulated model usage for this invocation.</summary>
    public Usage Usage { get; }
}

/// <summary>A detached snapshot of execution statistics for a named tool.</summary>
public sealed class ToolMetrics
{
    internal ToolMetrics(long callCount, long successCount, long errorCount, TimeSpan totalTime, ToolUseBlock tool)
    {
        CallCount = callCount;
        SuccessCount = successCount;
        ErrorCount = errorCount;
        TotalTime = totalTime;
        LastToolCall = tool;
    }

    /// <summary>Number of recorded calls.</summary>
    public long CallCount { get; }
    /// <summary>Number of successful calls.</summary>
    public long SuccessCount { get; }
    /// <summary>Number of unsuccessful calls.</summary>
    public long ErrorCount { get; }
    /// <summary>Total execution time.</summary>
    public TimeSpan TotalTime { get; }
    /// <summary>The latest recorded call, with detached JSON input and provider metadata.</summary>
    public ToolUseBlock LastToolCall { get; }
    /// <summary>Mean execution time per call.</summary>
    public TimeSpan AverageTime => CallCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TotalTime.Ticks / CallCount);
    /// <summary>The fraction of recorded calls that succeeded, between zero and one.</summary>
    public double SuccessRate => CallCount == 0 ? 0 : (double)SuccessCount / CallCount;
}

/// <summary>A detached, read-only snapshot of locally collected agent metrics.</summary>
public sealed class AgentMetrics
{
    internal AgentMetrics(long cycleCount, Usage usage, ModelPerformanceMetrics performance,
        IReadOnlyList<AgentInvocationMetrics> invocations, IReadOnlyDictionary<string, ToolMetrics> tools,
        long? latestContextSize, long? projectedContextSize, TimeSpan totalDuration)
    {
        CycleCount = cycleCount;
        AccumulatedUsage = usage;
        AccumulatedMetrics = performance;
        AgentInvocations = Array.AsReadOnly(invocations.ToArray());
        ToolMetrics = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ToolMetrics>(
            tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        LatestContextSize = latestContextSize;
        ProjectedContextSize = projectedContextSize;
        TotalDuration = totalDuration;
    }

    /// <summary>Total number of cycles started, including cycles outside retained invocation history.</summary>
    public long CycleCount { get; }
    /// <summary>Model usage across all invocations, including evicted history.</summary>
    public Usage AccumulatedUsage { get; }
    /// <summary>Accumulated model latency; time to first token is emitted per request rather than summed.</summary>
    public ModelPerformanceMetrics AccumulatedMetrics { get; }
    /// <summary>Recent invocation snapshots, in start order.</summary>
    public IReadOnlyList<AgentInvocationMetrics> AgentInvocations { get; }
    /// <summary>Statistics keyed by tool name using ordinal comparison.</summary>
    public IReadOnlyDictionary<string, ToolMetrics> ToolMetrics { get; }
    /// <summary>The newest retained invocation, or null before any invocation.</summary>
    public AgentInvocationMetrics? LatestAgentInvocation => AgentInvocations.Count == 0 ? null : AgentInvocations[AgentInvocations.Count - 1];
    /// <summary>Total prompt tokens selected by the collector's context-size policy, including separately reported cached tokens.</summary>
    public long? LatestContextSize { get; }
    /// <summary>Selected prompt plus output tokens, or null when the selected invocation has no cycle.</summary>
    public long? ProjectedContextSize { get; }
    /// <summary>Total duration of completed cycles across all invocations.</summary>
    public TimeSpan TotalDuration { get; }
    /// <summary>Total completed duration divided by all cycles started.</summary>
    public TimeSpan AverageCycleTime => CycleCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TotalDuration.Ticks / CycleCount);
}
