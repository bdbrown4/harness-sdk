namespace Strands.Agents;

public sealed partial class Agent
{
    internal IDisposable RegisterOwnedShutdownCallback(Func<ShutdownEvent, CancellationToken, ValueTask> callback)
    {
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            return _hooks.Register(callback);
        }
    }
}
