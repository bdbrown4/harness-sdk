namespace Strands.Agents.Telemetry;

/// <summary>A cycle handle that preserves its original invocation when other cycles start.</summary>
public sealed class AgentCycle : IDisposable
{
    private readonly AgentMeter _owner;
    private readonly AgentMeter.CycleState _state;

    internal AgentCycle(AgentMeter owner, AgentMeter.CycleState state)
    {
        _owner = owner;
        _state = state;
    }

    /// <summary>The meter-local identifier of this cycle.</summary>
    public string CycleId => _state.Id;

    /// <summary>Adds model usage and optional provider timing to this cycle.</summary>
    /// <param name="usage">Additive provider usage, when available.</param>
    /// <param name="metrics">Provider timing, when available.</param>
    public void Update(Usage? usage = null, ModelPerformanceMetrics? metrics = null) => _owner.Update(_state, usage, metrics);

    /// <summary>Completes this cycle once, using a monotonic clock unless a duration is provided.</summary>
    /// <param name="duration">An explicit nonnegative duration, or null to measure elapsed time.</param>
    public void End(TimeSpan? duration = null) => _owner.End(_state, duration);

    /// <summary>Completes this cycle using its monotonic elapsed time.</summary>
    public void Dispose() => End();
}
