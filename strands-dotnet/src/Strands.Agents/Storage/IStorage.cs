namespace Strands.Agents.Storage;

/// <summary>Stores opaque bytes under caller-selected keys. Custom backends need only these four operations.</summary>
public interface IStorage
{
    /// <summary>Writes or replaces a value without retaining the caller's mutable buffer.</summary>
    ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default);
    /// <summary>Returns an independent buffer, or null when the key is missing.</summary>
    ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Deletes a value; missing keys are a no-op.</summary>
    ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Returns full keys matching a prefix, sorted ordinally. An empty prefix matches all keys.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default);
}

/// <summary>Optional storage metadata. An unmarked custom backend is assumed persistent and unscoped.</summary>
public interface IStorageCharacteristics
{
    /// <summary>Whether values disappear when the backend's process exits.</summary>
    bool IsEphemeral { get; }
    /// <summary>Whether the caller already selected a namespace.</summary>
    bool IsNamespaced { get; }
}

/// <summary>A backend capable of ranked search in addition to byte storage.</summary>
public interface ISearchableStorage : IStorage
{
    /// <summary>Returns relevant keys in descending score order.</summary>
    ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>A ranked match. Larger scores indicate greater relevance; Data is optional.</summary>
/// <param name="Key">The matched key, relative to the storage view being searched.</param>
/// <param name="Score">The relevance score.</param>
/// <param name="Data">Optional stored bytes.</param>
public sealed record StorageSearchResult(string Key, double Score, byte[]? Data = null);

/// <summary>An invalid storage key or a failed backend operation.</summary>
public sealed class StorageException : IOException
{
    /// <summary>Creates a storage failure.</summary>
    public StorageException(string message) : base(message) { }
    /// <summary>Creates a storage failure retaining its backend cause.</summary>
    public StorageException(string message, Exception innerException) : base(message, innerException) { }
}
