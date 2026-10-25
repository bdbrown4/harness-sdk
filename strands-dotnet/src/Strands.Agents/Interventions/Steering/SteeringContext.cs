using System.Text.Json;

namespace Strands.Agents.Interventions.Steering;

/// <summary>A borrowed factory for independent steering context bound to each observing agent.</summary>
public interface ISteeringContextProvider
{
    /// <summary>The unique identifier of this provider within a handler.</summary>
    string Name { get; }
    /// <summary>Creates one owned subscription. A failing factory is responsible for rolling back registrations made before it returns.</summary>
    ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default);
}

/// <summary>An agent-local context subscription, owned and released by the observing steering handler.</summary>
public interface ISteeringContext : IAsyncDisposable
{
    /// <summary>A JSON object with a string type discriminator. Every consumer receives a detached snapshot.</summary>
    JsonElement Context { get; }
}

/// <summary>Settings shared by steering handlers.</summary>
public sealed record SteeringHandlerOptions
{
    /// <summary>Borrowed provider factories, copied in declaration order. The base handler defaults to no providers.</summary>
    public IReadOnlyList<ISteeringContextProvider> ContextProviders { get; init; } = [];
}
