namespace Strands.Agents.Storage;

/// <summary>An ephemeral byte store. Values and returned buffers are independent of callers.</summary>
public sealed class InMemoryStorage : ISearchableStorage, IStorageCharacteristics
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly StorageSearchStrategy _searchStrategy;

    /// <summary>Creates an unbounded byte store with optional search indexing.</summary>
    public InMemoryStorage(StorageSearchStrategy? searchStrategy = null) => _searchStrategy = searchStrategy ?? new KeywordSearchStrategy();
    /// <inheritdoc />
    public bool IsEphemeral => true;
    /// <inheritdoc />
    public bool IsNamespaced => false;
    /// <inheritdoc />
    public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(data, nameof(data));
        var normalized = StorageKey.Normalize(key);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = (byte[])data.Clone();
        lock (_gate) _values[normalized] = copy;
        await _searchStrategy.IndexAsync(this, normalized, (byte[])copy.Clone(), cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return new(_values.TryGetValue(normalized, out var data) ? (byte[])data.Clone() : null);
    }
    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _values.Remove(normalized);
        return default;
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Prefix(prefix);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return new(_values.Keys.Where(key => key.StartsWith(normalized, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) => _searchStrategy.SearchAsync(this, query, cancellationToken);

    /// <summary>Removes every value from this store.</summary>
    public void Clear() { lock (_gate) _values.Clear(); }
}
