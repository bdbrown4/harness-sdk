namespace Strands.Agents;

/// <summary>Schedules original model calls without granting result or history mutation.</summary>
public sealed class ToolBatchExecutionContext
{
    private readonly Func<int, Task<ToolResultBlock?>> _execute;
    internal ToolBatchExecutionContext(IReadOnlyList<ToolUseBlock> calls, IReadOnlyDictionary<string, object?> invocationState, Func<int, Task<ToolResultBlock?>> execute)
    {
        Calls = Array.AsReadOnly(calls.Select(call => (ToolUseBlock)Message.CloneBlock(call)).ToArray());
        InvocationState = invocationState; _execute = execute;
    }
    /// <summary>Detached original calls in model-issued order; membership cannot be changed.</summary>
    public IReadOnlyList<ToolUseBlock> Calls { get; }
    /// <summary>The original application-owned state object.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>Schedules an original index once and returns its shared task. Null denotes real pending human input.</summary>
    /// <param name="index">The zero-based original call index.</param>
    /// <returns>The supported-content result snapshot, or null when that call pauses.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index does not identify an original call.</exception>
    /// <exception cref="InvalidOperationException">Admission has closed or the call would reenter its own pipeline.</exception>
    public Task<ToolResultBlock?> ExecuteAsync(int index) => _execute(index);
}
