namespace Strands.Agents.Storage;

/// <summary>Namespacing and portable search for custom and built-in storage backends.</summary>
public static class StorageExtensions
{
    /// <summary>Creates a composable view of a slash-separated namespace without changing its backend.</summary>
    public static IStorage Namespace(this IStorage storage, string prefix)
    {
        Guard.NotNull(storage, nameof(storage));
        var normalized = StorageKey.Prefix(prefix).TrimEnd('/');
        return storage is ISearchableStorage searchable ? new SearchableNamespace(searchable, normalized) : new NamespacedStorage(storage, normalized);
    }

    /// <summary>Uses backend search when supplied, otherwise a keyword scan over the four byte operations.</summary>
    public static ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(this IStorage storage, string query, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(storage, nameof(storage));
        return storage is ISearchableStorage searchable ? searchable.SearchAsync(query, cancellationToken) : new KeywordSearchStrategy().SearchAsync(storage, query, cancellationToken);
    }

    private class NamespacedStorage : IStorage, IStorageCharacteristics
    {
        protected readonly IStorage Backend;
        protected readonly string Prefix;
        internal NamespacedStorage(IStorage backend, string prefix) { Backend = backend; Prefix = prefix.Length == 0 ? "" : prefix + "/"; }
        public bool IsEphemeral => Backend is IStorageCharacteristics { IsEphemeral: true };
        public bool IsNamespaced => true;
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) => Backend.WriteAsync(Prefix + StorageKey.Normalize(key), data, cancellationToken);
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Backend.ReadAsync(Prefix + StorageKey.Normalize(key), cancellationToken);
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => Backend.DeleteAsync(Prefix + StorageKey.Normalize(key), cancellationToken);
        public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
        {
            var keys = await Backend.ListAsync(Prefix + StorageKey.Prefix(prefix), cancellationToken).ConfigureAwait(false);
            return keys.Where(key => key.StartsWith(Prefix, StringComparison.Ordinal)).Select(key => key.Substring(Prefix.Length)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        }

        protected bool TrySearchScoped(string query, CancellationToken cancellationToken, out ValueTask<IReadOnlyList<StorageSearchResult>> results)
        {
            var backend = Backend;
            var prefix = Prefix;
            while (backend is NamespacedStorage nested)
            {
                prefix = nested.Prefix + prefix;
                backend = nested.Backend;
            }
            if (backend is LocalFileStorage local) return local.TrySearchScoped(prefix, query, cancellationToken, out results);
            results = default;
            return false;
        }
    }

    private sealed class SearchableNamespace(ISearchableStorage backend, string prefix) : NamespacedStorage(backend, prefix), ISearchableStorage
    {
        public async ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (TrySearchScoped(query, cancellationToken, out var scoped)) return await scoped.ConfigureAwait(false);
            var results = await backend.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return results.Where(result => result.Key.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(result => result with { Key = result.Key.Substring(Prefix.Length), Data = result.Data is null ? null : (byte[])result.Data.Clone() }).ToArray();
        }
    }
}
