using System.Globalization;
using System.Text;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Memory;

/// <summary>A small JSON-record store with lexical recall and optional file persistence.</summary>
public sealed class TestMemoryStore : MemoryStore, IWritableMemoryStore
{
    private readonly TestMemoryStoreOptions _options;
    private readonly IStorage _storage;
    private readonly string _key;
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>Creates a store without reading or writing its backend.</summary>
    public TestMemoryStore(TestMemoryStoreOptions options) : this(options, null, null) { }

    internal TestMemoryStore(TestMemoryStoreOptions options, IStorage? storage, string? key)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.Name, nameof(options.Name));
        if (options.MaxSearchResults is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "The search result limit must be positive.");
        if (options.Path is not null) Guard.NotNullOrWhiteSpace(options.Path, nameof(options.Path));
        if ((storage is null) != (key is null)) throw new ArgumentException("An internal backend and key must be supplied together.");
        _options = options with { };
        if (storage is not null)
        {
            _storage = storage;
            _key = StorageKey.Normalize(key!);
        }
        else if (!options.Persist)
        {
            _storage = new InMemoryStorage();
            _key = SanitizeName(options.Name) + ".json";
        }
        else
        {
            var path = System.IO.Path.GetFullPath(options.Path ?? DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), options.Name));
            _storage = new LocalFileStorage(System.IO.Path.GetDirectoryName(path)!);
            _key = System.IO.Path.GetFileName(path);
        }
    }

    /// <inheritdoc />
    public override string Name => _options.Name;
    /// <inheritdoc />
    public override string? Description => _options.Description;
    /// <inheritdoc />
    public override int? MaxSearchResults => _options.MaxSearchResults;
    /// <inheritdoc />
    public override bool Writable => _options.Writable;
    /// <inheritdoc />
    public override MemoryExtractionOptions? Extraction => _options.Extraction;

    /// <summary>Adds a fact or returns its existing identifier when trimmed content is identical.</summary>
    public ValueTask<TestMemoryAddResult> AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(content, nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Writable) throw new InvalidOperationException("TestMemoryStore is read-only.");
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Memory content must not be empty.", nameof(content));
        if (metadata is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            throw new ArgumentException("Memory metadata must be a JSON object or null.", nameof(metadata));
        var captured = metadata is { ValueKind: JsonValueKind.Object } value ? value.Clone() : (JsonElement?)null;
        return AddCoreAsync(content, captured, cancellationToken);
    }

    private async ValueTask<TestMemoryAddResult> AddCoreAsync(string content, JsonElement? metadata, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var normalized = content.Trim();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(record.GetProperty("content").GetString()!.Trim(), normalized, StringComparison.Ordinal))
                    return new(record.GetProperty("id").GetString()!);
            }
            var id = Guid.NewGuid().ToString("D");
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var bytes = TestMemoryRecordCodec.Append(records, id, content, timestamp, metadata);
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.WriteAsync(_key, bytes, cancellationToken).ConfigureAwait(false);
            // A late cancellation can report cancellation after a committed write; retrying deduplicates it.
            cancellationToken.ThrowIfCancellationRequested();
            return new(id);
        }
        catch (Exception error)
        {
            var selected = SelectCancellation(error, cancellationToken);
            if (ReferenceEquals(selected, error)) throw;
            throw selected;
        }
        finally { _writes.Release(); }
    }

    async ValueTask IWritableMemoryStore.AddAsync(string content, JsonElement? metadata, CancellationToken cancellationToken)
        => await AddAsync(content, metadata, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query, nameof(query));
        if (maxSearchResults <= 0) throw new ArgumentOutOfRangeException(nameof(maxSearchResults), "The search result limit must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = Tokenize(query);
        return tokens.Count == 0 ? new(Array.Empty<MemoryEntry>()) : SearchCoreAsync(tokens, maxSearchResults, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<MemoryEntry>> SearchCoreAsync(HashSet<string> tokens, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var scored = new List<(JsonElement Record, int Score)>();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var score = Tokenize(record.GetProperty("content").GetString()!).Count(tokens.Contains);
                if (score > 0) scored.Add((record, score));
            }
            var results = scored.OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Record.GetProperty("createdAt").GetString(), StringComparer.Ordinal)
                .Take(limit).Select(item => new MemoryEntry(item.Record.GetProperty("content").GetString()!,
                    metadata: TestMemoryRecordCodec.ScoredMetadata(item.Record, item.Score))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }
        catch (Exception error)
        {
            var selected = SelectCancellation(error, cancellationToken);
            if (ReferenceEquals(selected, error)) throw;
            throw selected;
        }
    }

    private async ValueTask<IReadOnlyList<JsonElement>> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await _storage.ReadAsync(_key, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes is null ? Array.Empty<JsonElement>() : TestMemoryRecordCodec.Read(bytes, _key, cancellationToken);
    }

    /// <summary>Searches using the configured result limit or a default of ten.</summary>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, MaxSearchResults ?? 10, cancellationToken);

    internal static string DefaultPath(string home, string name)
    {
        Guard.NotNullOrWhiteSpace(home, nameof(home));
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        return System.IO.Path.Combine(home, ".strands", "memory", SanitizeName(name) + ".json");
    }

    private static string SanitizeName(string name)
    {
        var replaced = name.Replace("..", "_").Replace('/', '_').Replace('\\', '_');
        return new string(replaced.Select(character => Guard.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
    }

    private static HashSet<string> Tokenize(string value)
    {
        var text = value.ToLowerInvariant();
        var result = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        for (var index = 0; index < text.Length;)
        {
            var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (text[index] == '_' || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
                word.Append(text, index, length);
            else if (word.Length > 0) { result.Add(word.ToString()); word.Clear(); }
            index += length;
        }
        if (word.Length > 0) result.Add(word.ToString());
        return result;
    }

    private static Exception SelectCancellation(Exception error, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested ||
            error is OperationCanceledException canceled && canceled.CancellationToken == cancellationToken ||
            error.Data["Strands.CleanupErrors"] is ICollection<Exception> { Count: > 0 }) return error;
        return new OperationCanceledException("Test memory operation was cancelled; an accepted backend write may already have committed.", error, cancellationToken);
    }
}
