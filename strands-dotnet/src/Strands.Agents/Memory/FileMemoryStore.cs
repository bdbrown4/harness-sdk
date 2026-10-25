using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents.Storage;

namespace Strands.Agents.Memory;

/// <summary>Configures markdown facts stored under a unified byte-storage namespace.</summary>
public sealed record FileMemoryStoreOptions
{
    /// <summary>The unique store name and default memory namespace suffix.</summary>
    public required string Name { get; init; }
    /// <summary>An optional description presented to the agent.</summary>
    public string? Description { get; init; }
    /// <summary>An explicit backend. An already namespaced view is used as its exact root.</summary>
    public IStorage? Storage { get; init; }
    /// <summary>Whether managers may write entries. Defaults to true.</summary>
    public bool Writable { get; init; } = true;
    /// <summary>An optional positive default result limit. Direct searches otherwise return up to ten entries.</summary>
    public int? MaxSearchResults { get; init; }
    /// <summary>An optional store-local search implementation; it does not alter the backend's search policy.</summary>
    public StorageSearchStrategy? Search { get; init; }
    /// <summary>Uses the backend's search and optional result bytes when Search is absent. Defaults to false.</summary>
    public bool UseStorageSearch { get; init; }
    /// <summary>Optional automatic fact extraction with the store's heading-aware output contract.</summary>
    public FileMemoryExtractionOptions? Extraction { get; init; }
}

/// <summary>Adds model and guidance overrides to file-backed memory extraction.</summary>
public sealed record FileMemoryExtractionOptions : MemoryExtractionOptions
{
    /// <summary>An explicit extraction model, taking precedence over the agent auxiliary model. Ignored when Extractor is set.</summary>
    public IModel? Model { get; init; }
    /// <summary>Optional fact-selection guidance. The store always appends its JSON and heading contract. Ignored when Extractor is set.</summary>
    public string? SystemPrompt { get; init; }
}

/// <summary>A topic-grouped markdown memory store with keyword retrieval and serialized appends.</summary>
public sealed class FileMemoryStore : MemoryStore, IWritableMemoryStore
{
    private readonly FileMemoryStoreOptions _options;
    private readonly IStorage _storage;
    private readonly MemoryExtractionOptions? _extraction;
    private readonly object _writeGate = new();
    private readonly Dictionary<string, Task<string>> _writes = new(StringComparer.Ordinal);
    private static readonly Regex Heading = new("^#+\\s*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex NonSlug = new("[^a-z0-9\\s-]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex WhiteSpace = new("\\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>Creates a store without accessing its backend.</summary>
    public FileMemoryStore(FileMemoryStoreOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.Name, nameof(options.Name));
        if (options.MaxSearchResults is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "The search result limit must be positive.");
        _options = options;
        var storage = options.Storage ?? new LocalFileStorage();
        _storage = storage is IStorageCharacteristics { IsNamespaced: true } ? storage : storage.Namespace("memory/" + Name);
        _extraction = options.Extraction is { } extraction
            ? extraction with { Extractor = extraction.Extractor ?? new KeyAwareExtractor(_storage, extraction.Model, extraction.SystemPrompt) } : null;
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
    public override MemoryExtractionOptions? Extraction => _extraction;
    /// <summary>Appends to a topic's markdown entry and returns its relative storage key.</summary>
    public ValueTask<string> AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(content, nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Writable) throw new InvalidOperationException("This memory store is read-only.");
        var lines = content.Split('\n');
        var slug = WhiteSpace.Replace(NonSlug.Replace(Heading.Replace(lines[0], "").ToLowerInvariant(), "").Trim(), "-");
        if (slug.Length > 50) slug = slug.Substring(0, 50);
        slug = slug.TrimEnd('-');
        var key = (slug.Length == 0 ? "entry-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : slug) + ".md";
        Task<string>? previous;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeGate) { _writes.TryGetValue(key, out previous); _writes[key] = completion.Task; }
        _ = AppendAsync(previous, completion, key, content, lines, cancellationToken);
        return new(completion.Task);
    }

    private async Task AppendAsync(Task<string>? previous, TaskCompletionSource<string> completion, string key, string content, string[] lines, CancellationToken cancellationToken)
    {
        try
        {
            if (previous is not null)
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception) { /* A preceding failed write does not prevent later independent facts from being stored. */ }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            var merged = content;
            if (existing is not null)
            {
                var current = Encoding.UTF8.GetString(existing);
                var facts = string.Join("\n", lines.Skip(1)).Trim();
                merged = facts.Length > 0 ? current.TrimEnd() + "\n" + facts : current;
            }
            await _storage.WriteAsync(key, Encoding.UTF8.GetBytes(merged), cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(key);
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            lock (_writeGate)
                if (_writes.TryGetValue(key, out var current) && ReferenceEquals(current, completion.Task)) _writes.Remove(key);
        }
    }
    async ValueTask IWritableMemoryStore.AddAsync(string content, JsonElement? metadata, CancellationToken cancellationToken)
        => _ = await AddAsync(content, metadata, cancellationToken).ConfigureAwait(false);
    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query, nameof(query));
        if (maxSearchResults <= 0) throw new ArgumentOutOfRangeException(nameof(maxSearchResults));
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Search is { } search)
        {
            var results = await search.SearchAsync(_storage, query, cancellationToken).ConfigureAwait(false);
            var entries = new List<MemoryEntry>();
            foreach (var result in results.Take(maxSearchResults))
            {
                var bytes = await _storage.ReadAsync(result.Key, cancellationToken).ConfigureAwait(false);
                if (bytes is not null) entries.Add(Entry(result.Key, result.Score, bytes));
            }
            return entries;
        }
        if (_options.UseStorageSearch)
        {
            var results = await _storage.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var accepted = results.Select(result => result with { Data = result.Data is null ? null : (byte[])result.Data.Clone() }).ToArray();
            var entries = new List<MemoryEntry>();
            foreach (var result in accepted.Take(maxSearchResults))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = result.Data ?? await _storage.ReadAsync(result.Key, cancellationToken).ConfigureAwait(false);
                if (bytes is not null) entries.Add(Entry(result.Key, result.Score, bytes));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }
        var tokens = KeywordSearchStrategy.Tokenize(query);
        if (tokens.Count == 0) return [];
        var keys = await _storage.ListAsync("", cancellationToken).ConfigureAwait(false);
        var scored = new List<(MemoryEntry Entry, int Score, string Key)>();
        for (var offset = 0; offset < keys.Count; offset += 8)
        {
            var batch = await Task.WhenAll(keys.Skip(offset).Take(8).Select(async key =>
            {
                try
                {
                    var bytes = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) return ((MemoryEntry?)null, 0, key);
                    var name = Basename(key);
                    var score = KeywordSearchStrategy.Tokenize(name + " " + Encoding.UTF8.GetString(bytes)).Count(tokens.Contains);
                    return (score > 0 ? Entry(key, score, bytes) : null, score, key);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    SdkLogging.Warning(error, "Skipping unreadable memory entry '{Key}': {Error}", key, error.Message);
                    return ((MemoryEntry?)null, 0, key);
                }
            })).ConfigureAwait(false);
            foreach (var result in batch)
                if (result.Item1 is { } entry) scored.Add((entry, result.Item2, result.Item3));
        }
        return scored.OrderByDescending(result => result.Score).ThenBy(result => result.Key, StringComparer.Ordinal).Take(maxSearchResults).Select(result => result.Entry).ToArray();
    }

    private static MemoryEntry Entry(string key, double score, byte[] bytes)
        => new(Encoding.UTF8.GetString(bytes).Trim(), metadata: JsonSerializer.SerializeToElement(new { path = key, score }));

    private static string Basename(string key)
    {
        var name = key.Substring(key.LastIndexOf('/') + 1);
        return name.EndsWith(".md", StringComparison.Ordinal) ? name.Substring(0, name.Length - 3) : name;
    }

    private sealed class KeyAwareExtractor(IStorage storage, IModel? model, string? guidance) : IExtractor
    {
        public async ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default)
        {
            var headings = (await storage.ListAsync("", cancellationToken).ConfigureAwait(false)).Select(key => Basename(key).Replace('-', ' ')).ToArray();
            var prompt = (guidance ?? "You extract durable facts worth remembering across future conversations from a transcript.") + "\n\n" + OutputContract;
            if (headings.Length > 0) prompt += "\n\nExisting topics: " + string.Join(", ", headings) + ". Reuse an existing topic heading when new facts belong to it.";
            return await new ModelExtractor(new() { Model = model, SystemPrompt = prompt }).ExtractAsync(messages, context, cancellationToken).ConfigureAwait(false);
        }
        private const string OutputContract = "Return ONLY a JSON array of objects: {\"content\": string}.\n\nGroup related facts into a single entry. The first line is a markdown heading (e.g. \"# User preferences\", \"# Project setup\", \"# Team conventions\"). Put each fact on its own line below the heading.\n\nIf there is nothing worth remembering, return [].";
    }
    /// <summary>Searches using this store's default limit, or ten when it is unspecified.</summary>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, MaxSearchResults ?? 10, cancellationToken);
}
