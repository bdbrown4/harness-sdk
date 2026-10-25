namespace Strands.Agents;

/// <summary>Optional deletion of a persisted latest-session snapshot by its opaque identifier.</summary>
/// <remarks>Deleting a missing snapshot succeeds. This capability does not mutate a running Agent or delete separately stored context originals.</remarks>
public interface IDeletableSessionStore : ISessionStore
{
    /// <summary>Deletes only the snapshot belonging to the specified session.</summary>
    /// <param name="sessionId">The opaque, nonempty session key.</param>
    /// <param name="cancellationToken">Cancellation before the backend commits the deletion.</param>
    /// <returns>A task that completes after the deletion commits.</returns>
    ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
