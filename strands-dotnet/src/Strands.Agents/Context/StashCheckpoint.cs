using System.Collections.ObjectModel;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Context;

internal sealed class StashCheckpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private StashCheckpoint(IReadOnlyDictionary<string, JsonElement>? entries, string? storageType)
    { Entries = entries; StorageType = storageType; }
    internal IReadOnlyDictionary<string, JsonElement>? Entries { get; }
    internal string? StorageType { get; }

    internal static StashCheckpoint? Prepare(IReadOnlyDictionary<string, JsonElement>? entries, string? storageType)
    {
        if (entries is not null && storageType is not null)
            throw new InvalidDataException("A context stash checkpoint cannot contain both inline entries and an external backend marker.");
        if (storageType is not null && string.IsNullOrWhiteSpace(storageType))
            throw new InvalidDataException("An external context stash backend type cannot be blank.");
        return entries is null && storageType is null ? null : new(entries is null ? null : CopyEntries(entries), storageType);
    }

    internal static IReadOnlyDictionary<string, JsonElement> CopyEntries(IReadOnlyDictionary<string, JsonElement> entries)
    {
        Guard.NotNull(entries, nameof(entries));
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in entries)
            {
                if (StorageKey.Normalize(entry.Key) != entry.Key) throw new InvalidDataException("Context stash references must be canonical storage keys.");
                var value = entry.Value.Clone();
                var block = value.Deserialize<ContentBlock>(JsonOptions) ?? throw new InvalidDataException("A stashed snapshot block cannot be null.");
                if (block is TextBlock { Text: null }) throw new InvalidDataException("A stashed text block cannot contain null text.");
                _ = Message.CloneBlock(block);
                copy.Add(entry.Key, value);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or StorageException)
        { throw new InvalidDataException("Context stash checkpoint entries are malformed.", error); }
        return new ReadOnlyDictionary<string, JsonElement>(copy);
    }

    internal static void Warn(string message)
    {
        try { SdkLogging.Warning("{Message}", message); }
        catch (Exception) { /* A diagnostic sink cannot change stash persistence or restoration. */ }
    }
}
