namespace Strands.Agents;

/// <summary>An optional typed configuration capability for native model adapters.</summary>
/// <typeparam name="TOptions">The immutable options snapshot accepted by the adapter.</typeparam>
public interface IConfigurableModel<TOptions> where TOptions : class
{
    /// <summary>Returns the current detached configuration snapshot.</summary>
    TOptions GetConfig();
    /// <summary>Validates and atomically replaces configuration using the current snapshot. Entered requests retain their captured configuration.</summary>
    /// <param name="update">A transformation evaluated once; throwing or returning invalid options leaves the prior configuration unchanged.</param>
    void UpdateConfig(Func<TOptions, TOptions> update);
}
