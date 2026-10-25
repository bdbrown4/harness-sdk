namespace Strands.Agents.Storage;

internal static class StorageKey
{
    internal static string Normalize(string key)
    {
        var normalized = Prefix(key).TrimEnd('/');
        if (normalized.Length == 0) throw new StorageException("Storage keys cannot be empty.");
        return normalized;
    }

    internal static string Prefix(string prefix)
    {
        Guard.NotNull(prefix, nameof(prefix));
        if (prefix.Contains('\\')) throw new StorageException("Storage keys cannot contain backslashes.");
        var segments = prefix.Split('/').Where(segment => segment.Length != 0).ToArray();
        if (segments.Contains("..", StringComparer.Ordinal)) throw new StorageException("Storage keys cannot contain '..' path segments.");
        var joined = string.Join("/", segments);
        return joined.Length != 0 && prefix.EndsWith("/", StringComparison.Ordinal) ? joined + "/" : joined;
    }
}
