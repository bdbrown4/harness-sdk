using Strands.Agents.Context;

namespace Strands.Agents;

public sealed partial class Agent
{
    private StashCheckpoint? _restoredContextCheckpoint;
    private static StashCheckpoint? PrepareContextCheckpoint(SessionSnapshot snapshot)
        => StashCheckpoint.Prepare(snapshot.ContextStash, snapshot.ContextStashStorageType);
    private void SetRestoredContextCheckpoint(StashCheckpoint? checkpoint)
    {
        _restoredContextCheckpoint = checkpoint;
        RestoredContextStash = checkpoint?.Entries;
    }
    internal StashCheckpoint? TakeRestoredContextCheckpoint()
    {
        var checkpoint = _restoredContextCheckpoint;
        _restoredContextCheckpoint = null;
        _ = TakeRestoredContextStash();
        return checkpoint;
    }
}
