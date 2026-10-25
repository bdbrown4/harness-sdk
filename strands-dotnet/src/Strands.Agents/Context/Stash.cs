using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Context;

/// <summary>Persists original content independently of the model's reduced conversation.</summary>
public sealed class Stash
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStorage _baseStorage;
    private readonly IStorage _storage;
    private readonly string _sessionPrefix;
    private readonly bool _callerScoped;

    /// <summary>Creates an agent-scoped stash. An explicitly supplied namespaced view can be used as an exact shared root.</summary>
    public Stash(IStorage storage, string sessionId, string agentId, bool customStashNamespace = false)
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));
        Guard.NotNullOrWhiteSpace(agentId, nameof(agentId));
        _baseStorage = storage;
        _sessionPrefix = "context/" + Uri.EscapeDataString(sessionId) + "/";
        _callerScoped = customStashNamespace && storage is IStorageCharacteristics { IsNamespaced: true };
        _storage = _callerScoped ? storage : storage.Namespace(_sessionPrefix + "scopes/agent/" + Uri.EscapeDataString(agentId));
    }

    /// <summary>Whether originals survive backend process restarts.</summary>
    public bool IsDurable => _baseStorage is not IStorageCharacteristics { IsEphemeral: true };
    /// <summary>The base backend's type name, used only to diagnose an external stash restored with different storage.</summary>
    public string StorageTypeName => _baseStorage.GetType().Name;

    /// <summary>Returns stable references for each original item. Message identity and original block content prevent reused provider or caller identifiers from aliasing different results.</summary>
    public IReadOnlyList<string> GetReferences(ContentBlock block, Message message, int blockIndex)
    {
        Guard.NotNull(block, nameof(block));
        Guard.NotNull(message, nameof(message));
        if (blockIndex < 0 || blockIndex >= message.Content.Count) throw new ArgumentOutOfRangeException(nameof(blockIndex));
        using var hash = SHA256.Create();
        var identity = BitConverter.ToString(hash.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(new { message.TrackingId, Block = block }, JsonOptions))).Replace("-", "").ToLowerInvariant();
        var prefix = "m_" + identity + "_" + blockIndex.ToString(CultureInfo.InvariantCulture);
        return block is ToolResultBlock result ? Enumerable.Range(0, Items(result).Count).Select(index => prefix + "_" + index.ToString(CultureInfo.InvariantCulture)).ToArray() : [prefix];
    }

    /// <summary>Stores all retrievable content before reduction, preserving an existing original when restoring a placeholder.</summary>
    public async ValueTask StoreMessageAsync(Message message, ISet<string>? excludedToolUseIds = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(message, nameof(message));
        for (var index = 0; index < message.Content.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = message.Content[index];
            if (block is ToolUseBlock or CachePointBlock or ReasoningBlock or RedactedReasoningBlock or Models.ResponsesReasoningBlock) continue;
            if (block is Models.GoogleContentBlock google &&
                (google.Part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True || google.Part.TryGetProperty("thoughtSignature", out _))) continue;
            if (block is ToolResultBlock toolResult && excludedToolUseIds?.Contains(toolResult.ToolUseId) == true) continue;
            var references = GetReferences(block, message, index);
            IReadOnlyList<ContentBlock> items = block is ToolResultBlock result ? Items(result) : [block];
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                try
                {
                    if (await _storage.ReadAsync(references[itemIndex], cancellationToken).ConfigureAwait(false) is null)
                        await _storage.WriteAsync(references[itemIndex], JsonSerializer.SerializeToUtf8Bytes<ContentBlock>(items[itemIndex], JsonOptions), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { StashCheckpoint.Warn($"Failed to stash message '{message.TrackingId}' block {index}: {error.Message}"); }
            }
        }
    }

    /// <summary>Restores a detached original block, or null when the reference is absent.</summary>
    public async ValueTask<ContentBlock?> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
    {
        var data = await _storage.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        return data is null ? null : JsonSerializer.Deserialize<ContentBlock>(data, JsonOptions) ?? throw new InvalidDataException("A stashed block cannot be null.");
    }

    /// <summary>Lists references in this stash.</summary>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => _storage.ListAsync("", cancellationToken);
    /// <summary>Deletes one reference.</summary>
    public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default) => _storage.DeleteAsync(reference, cancellationToken);
    /// <summary>Deletes the whole stash view, including other agents' entries when the caller chose a shared root.</summary>
    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in await ListAsync(cancellationToken).ConfigureAwait(false)) await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Deletes this session's automatically scoped entries; explicit caller-scoped stashes are retained.</summary>
    public async ValueTask ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_callerScoped) return;
        cancellationToken.ThrowIfCancellationRequested();
        var keys = (await _baseStorage.ListAsync(_sessionPrefix, cancellationToken).ConfigureAwait(false)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var key in keys)
        {
            if (key is null || key.Length <= _sessionPrefix.Length || !key.StartsWith(_sessionPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The stash listing contains a key outside the selected session.");
            string normalized;
            try { normalized = StorageKey.Normalize(key); }
            catch (StorageException error) { throw new InvalidDataException("The stash listing contains an invalid key.", error); }
            if (!string.Equals(key, normalized, StringComparison.Ordinal))
                throw new InvalidDataException("The stash listing contains a noncanonical key.");
        }
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _baseStorage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
    /// <summary>Captures all entries in this stash, including a whole caller-scoped shared view.</summary>
    public async ValueTask<IReadOnlyDictionary<string, JsonElement>> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var data = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (data is not null) entries[key] = JsonSerializer.Deserialize<JsonElement>(data);
        }
        return StashCheckpoint.CopyEntries(entries);
    }
    /// <summary>Validates and copies the complete batch, then restores originals. Matching keys are replaced; backend write failures can leave a partial merge.</summary>
    public async ValueTask LoadSnapshotAsync(IReadOnlyDictionary<string, JsonElement> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = StashCheckpoint.CopyEntries(entries);
        try
        {
            foreach (var entry in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _storage.WriteAsync(entry.Key, JsonSerializer.SerializeToUtf8Bytes(entry.Value, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && (error is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken))
        { throw new OperationCanceledException("Context stash restoration was cancelled.", error, cancellationToken); }
    }

    internal static IReadOnlyList<ContentBlock> Items(ToolResultBlock result)
    {
        IReadOnlyList<ContentBlock> content = result.ContentBlocks.Count == 0 ? [new TextBlock(result.Content)] : result.ContentBlocks;
        return result.StructuredContent is { } structured ? content.Concat(new[] { new JsonBlock(structured) }).ToArray() : content;
    }
}
