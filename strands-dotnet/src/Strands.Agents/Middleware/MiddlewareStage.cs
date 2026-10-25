namespace Strands.Agents.Middleware;

/// <summary>A typed interception point. The built-in model and tool stages are exposed by MiddlewareStages.</summary>
/// <typeparam name="TContext">The stage's immutable input wrapper.</typeparam>
/// <typeparam name="TResult">The stage's completed output wrapper.</typeparam>
public sealed class MiddlewareStage<TContext, TResult>
{
    internal MiddlewareStage(string name, Func<TContext, TContext> snapshotContext, Func<TResult, TResult> snapshotResult)
    { Name = name; SnapshotContext = snapshotContext; SnapshotResult = snapshotResult; }
    /// <summary>The stable stage name.</summary>
    public string Name { get; }
    internal Func<TContext, TContext> SnapshotContext { get; }
    internal Func<TResult, TResult> SnapshotResult { get; }
}

/// <summary>The public middleware interception points.</summary>
public static class MiddlewareStages
{
    /// <summary>Creates an independent stage token with caller-supplied snapshot functions.</summary>
    /// <typeparam name="TContext">The stage's input wrapper type.</typeparam>
    /// <typeparam name="TResult">The stage's completed output wrapper type.</typeparam>
    /// <param name="name">The descriptive stage name.</param>
    /// <param name="snapshotContext">The function used by the registry to snapshot inputs.</param>
    /// <param name="snapshotResult">The function used by the registry to snapshot completed results.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static MiddlewareStage<TContext, TResult> Create<TContext, TResult>(
        string name, Func<TContext, TContext> snapshotContext, Func<TResult, TResult> snapshotResult)
    {
        Guard.NotNull(name, nameof(name));
        Guard.NotNull(snapshotContext, nameof(snapshotContext));
        Guard.NotNull(snapshotResult, nameof(snapshotResult));
        return new(name, snapshotContext, snapshotResult);
    }

    /// <summary>Wraps one model operation inside its BeforeModelCall and AfterModelCall hooks.</summary>
    public static MiddlewareStage<InvokeModelContext, InvokeModelResult> InvokeModel { get; } = new("invokeModel", value => { Guard.NotNull(value, nameof(value)); return value.Snapshot(); }, value => { Guard.NotNull(value, nameof(value)); return value.Snapshot(); });
    /// <summary>Wraps one tool operation inside its BeforeToolCall and AfterToolCall hooks.</summary>
    public static MiddlewareStage<ExecuteToolContext, ExecuteToolResult> ExecuteTool { get; } = new("executeTool", value => { Guard.NotNull(value, nameof(value)); return value.Snapshot(); }, value => { Guard.NotNull(value, nameof(value)); return value.Snapshot(); });
}
