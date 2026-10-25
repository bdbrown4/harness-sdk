namespace Strands.Agents.Storage;

// A host index may need a physical namespace before ranking and truncation.
// This is deliberately internal; ordinary/custom storage and search keep their public contracts.
internal interface IScopedStorageSearchStrategy
{
    ValueTask<IReadOnlyList<StorageSearchResult>> SearchScopedAsync(LocalFileStorage storage, string prefix, string query, CancellationToken cancellationToken);
}
