namespace Strands.Agents;

public sealed partial class Agent
{
    private IModel? _auxModel;

    /// <summary>Sets a borrowed auxiliary model, or clears the explicit selection so auxiliary calls follow the primary model.</summary>
    /// <param name="model">The ready model instance, or null to follow the primary model.</param>
    public void SetAuxModel(IModel? model)
    {
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            Volatile.Write(ref _auxModel, model);
        }
    }
}
