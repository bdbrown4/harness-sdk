namespace Strands.Agents.SessionRepositories;

/// <summary>Optional deletion of all indexed records belonging to one opaque session identifier.</summary>
/// <remarks>Missing sessions are a no-op. This capability does not clear live Agents or separately stored context originals.</remarks>
public interface IDeletableSessionRepository : ISessionRepository
{
    /// <summary>Deletes the selected session's metadata, agent, message and multi-agent rows.</summary>
    /// <param name="sessionId">The opaque, nonempty session identifier.</param>
    /// <param name="cancellationToken">Cancellation observed before each backend operation.</param>
    /// <returns>A task that joins each admitted backend deletion.</returns>
    ValueTask DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
