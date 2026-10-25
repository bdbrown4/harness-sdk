using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Memory;

public sealed partial class MemoryManager
{
    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundWrites = [];

    /// <summary>Forces pending extraction past trigger/backoff gates and waits for accepted background writes. Failed extraction retains pending messages for retry; detached tool write failures are logged. Cancellation ends this wait without aborting accepted work.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_coordinator is { } coordinator) await coordinator.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task[] pending;
            lock (_backgroundGate) pending = _backgroundWrites.ToArray();
            if (pending.Length == 0) return;
            await TaskCancellation.WaitAsync(Task.WhenAll(pending), cancellationToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<ITool> CreateTools()
    {
        if (_options.SearchTool is { } search)
        {
            var spec = new ToolSpec(search.Name ?? "search_memory", Describe(search.Description ?? "Search long-term memory for relevant facts, preferences, and decisions.", _searchScope), JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"query":{"type":"string"},"maxSearchResults":{"type":"integer","minimum":1},"stores":{"type":"array","items":{"type":"string"}}},"required":["query"],"additionalProperties":false}
                """));
            yield return new MemoryTool(spec, async (input, token) =>
            {
                var entries = await SearchAsync(input.GetProperty("query").GetString()!, new()
                {
                    Stores = ResolveToolTargets(_searchScope, input),
                    MaxSearchResults = input.TryGetProperty("maxSearchResults", out var limit) ? limit.GetInt32() : null
                }, token).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(entries, ToolJson);
            });
        }
        if (_options.AddTool is { } add)
        {
            var spec = new ToolSpec(add.Name ?? "add_memory", Describe(add.Description ?? "Add facts, preferences, or decisions to long-term memory for later conversations.", _addScope), JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"entries":{"type":"array","items":{"type":"string"},"minItems":1},"stores":{"type":"array","items":{"type":"string"}}},"required":["entries"],"additionalProperties":false}
                """));
            yield return new MemoryTool(spec, async (input, token) =>
            {
                var stores = ResolveToolTargets(_addScope, input);
                var entries = input.GetProperty("entries").EnumerateArray().Select(entry => entry.GetString() ?? throw new ArgumentException("Memory entries cannot be null.", nameof(input))).ToArray();
                if (entries.Length == 0) throw new ArgumentException("At least one memory entry is required.", nameof(input));
                token.ThrowIfCancellationRequested();
                if (!add.WaitForWrites)
                {
                    var task = ObserveBackgroundWriteAsync(entries, stores);
                    lock (_backgroundGate) _backgroundWrites.Add(task);
                    _ = RemoveBackgroundWriteAsync(task);
                    return JsonSerializer.SerializeToElement(new { accepted = entries.Length });
                }
                await WriteEntriesAsync(entries, stores, token).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { stored = entries.Length });
            });
        }
    }

    private async Task WriteEntriesAsync(string[] entries, string[] stores, CancellationToken cancellationToken, bool detached = false)
    {
        var outcomes = await Task.WhenAll(entries.Select(async content =>
        {
            try { await AddCoreAsync(content, new() { Stores = stores }, detached, cancellationToken).ConfigureAwait(false); return (Exception?)null; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return error; }
        })).ConfigureAwait(false);
        var failures = outcomes.Where(error => error is not null).SelectMany(error => error is AggregateException aggregate ? (IEnumerable<Exception>)aggregate.Flatten().InnerExceptions : new[] { error! }).ToArray();
        if (failures.Length > 0) throw new AggregateException("Failed to add memory entries.", failures);
    }

    private async Task ObserveBackgroundWriteAsync(string[] entries, string[] stores)
    {
        try { await WriteEntriesAsync(entries, stores, CancellationToken.None, detached: true).ConfigureAwait(false); }
        catch (Exception error)
        {
            try { SdkLogging.Warning(error, "A detached memory write failed: {Error}", error.Message); }
            catch (Exception) { /* Diagnostic sinks must not strand accepted background work. */ }
        }
    }

    private async Task RemoveBackgroundWriteAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { lock (_backgroundGate) _backgroundWrites.Remove(task); }
    }

    private static string[] ResolveToolTargets(MemoryStore[] scope, JsonElement input)
    {
        var allowed = scope.Select(store => store.Name).ToArray();
        if (!input.TryGetProperty("stores", out var value)) return allowed;
        var requested = value.EnumerateArray().Select(item => item.GetString() ?? throw new ArgumentException("Store names cannot be null.", nameof(input))).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0) return allowed;
        var selected = requested.Where(name => allowed.Contains(name, StringComparer.Ordinal)).ToArray();
        if (selected.Length == 0) throw new ArgumentException("None of the requested stores is within this memory tool's scope.", nameof(input));
        if (selected.Length < requested.Length) SdkLogging.Warning("Ignoring requested memory stores outside this tool's scope.");
        return selected;
    }

    private static string Describe(string description, MemoryStore[] stores)
    {
        var descriptions = stores.Where(store => !string.IsNullOrWhiteSpace(store.Description)).Select(store => "- " + store.Name + ": " + store.Description).ToArray();
        return descriptions.Length == 0 ? description : description + "\n\nAvailable memory stores:\n" + string.Join("\n", descriptions);
    }

    private sealed class MemoryTool(ToolSpec spec, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> callback) : IRichTool
    {
        public ToolSpec Spec { get; } = spec;
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context));
            var json = await callback(input, cancellationToken).ConfigureAwait(false);
            return new(context.ToolUse.ToolUseId, json.GetRawText()) { ContentBlocks = [new JsonBlock(json)] };
        }
    }
}
