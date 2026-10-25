namespace Strands.Agents;

/// <summary>Schedules the model-issued calls of one batch through the SDK's existing tool pipeline.</summary>
public interface IToolBatchExecutor
{
    /// <summary>Schedules calls while the context is open. Accepted SDK operations remain owned and joined by the agent.</summary>
    /// <param name="execution">The SDK-owned batch scheduling capability.</param>
    /// <param name="cancellationToken">The linked custom-batch operation token; the caller's token source remains borrowed.</param>
    ValueTask ExecuteAsync(ToolBatchExecutionContext execution, CancellationToken cancellationToken = default);
}
