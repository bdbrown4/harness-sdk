using System.Text;
using System.Text.RegularExpressions;

namespace Strands.Agents.Storage;

/// <summary>Extends storage search, optionally maintaining an index as values are written.</summary>
public abstract class StorageSearchStrategy
{
    /// <summary>Indexes a committed value. The default performs no indexing.</summary>
    public virtual ValueTask IndexAsync(IStorage storage, string key, byte[] data, CancellationToken cancellationToken = default) => default;
    /// <summary>Searches a storage view and returns relevant keys, best first.</summary>
    public abstract ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default);
}

/// <summary>Ranks entries by distinct query words found in their key or UTF-8 content.</summary>
public sealed class KeywordSearchStrategy : StorageSearchStrategy
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(query, nameof(query));
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return Array.Empty<StorageSearchResult>();
        var results = new List<StorageSearchResult>();
        foreach (var key in await storage.ListAsync("", cancellationToken).ConfigureAwait(false))
        {
            var data = await storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (data is null) continue;
            var score = Tokenize(key + " " + Encoding.UTF8.GetString(data)).Count(tokens.Contains);
            if (score > 0) results.Add(new(key, score));
        }
        return results.OrderByDescending(result => result.Score).ThenBy(result => result.Key, StringComparer.Ordinal).ToArray();
    }

    internal static HashSet<string> Tokenize(string value) => new(Words.Matches(value.ToLowerInvariant()).Cast<Match>().Select(match => match.Value), StringComparer.Ordinal);
}
