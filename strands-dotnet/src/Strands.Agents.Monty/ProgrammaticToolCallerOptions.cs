namespace Strands.Agents.Monty;

/// <summary>Guest execution and tool selection for a genuine Monty worker.</summary>
public sealed record ProgrammaticToolCallerOptions
{
    /// <summary>Registered tool names exposed to guest code; null selects every other ordinary tool. No programmatic caller is exposed.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    /// <summary>The model-facing tool name.</summary>
    public string Name { get; init; } = "programmatic_tool_caller";
    /// <summary>The model-facing tool description.</summary>
    public string Description { get; init; } = ProgrammaticToolCaller.DefaultDescription;
    /// <summary>The wall-clock deadline, including tools and worker startup. Null disables this deadline; guest compute limits remain active.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Maximum cumulative guest compute time, excluding suspended host calls.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Maximum allocator-backed guest memory, enforced by Monty's official allocator in the dedicated worker.</summary>
    public int MaxMemoryBytes { get; init; } = 256 * 1024 * 1024;
    /// <summary>The maximum number of host-serviced suspensions per run.</summary>
    public int MaxSuspensions { get; init; } = 1000;
    /// <summary>The maximum guest recursion depth.</summary>
    public int MaxRecursionDepth { get; init; } = 1000;
    /// <summary>An explicit, trusted native worker executable. Null selects the package's asset for the current process platform and architecture; PATH is not searched.</summary>
    public string? WorkerPath { get; init; }
    /// <summary>An explicit worker process working directory. Guest OS callbacks remain disabled; file and network access occur only through exposed tools.</summary>
    public string? WorkingDirectory { get; init; }
    internal Action<int>? ProcessStarted { get; init; }
    internal Action<int, int>? WorkerReady { get; init; }
}
