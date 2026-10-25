using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Telemetry;

public sealed partial class Tracer
{
    /// <summary>Starts a manual memory search activity without changing ambient context.</summary>
    /// <param name="options">The copied query, store selection, and source policy.</param>
    public TraceSpan StartMemorySearchSpan(StartMemorySearchSpanOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Query, nameof(options.Query));
        Guard.NotNull(options.StoreNames, nameof(options.StoreNames));
        var names = options.StoreNames.ToArray();
        var attributes = MemoryAttributes("memory.search", options.Profile);
        attributes["memory.store.names"] = JsonSerializer.Serialize(names);
        attributes["memory.store.count"] = names.Length;
        if (options.MaxSearchResults.HasValue) attributes["memory.max_search_results"] = options.MaxSearchResults.Value;
        ApplyMemoryCallAttributes(attributes, options.Profile, options.ParentSpan, options.CustomTraceAttributes, options.ExtraAttributes);
        var span = Create("memory.search", MemoryParent(options.ParentSpan), null, attributes, memoryProfile: options.Profile, includeConstructorAttributes: false);
        span.RecordEvent("memory.query", "content", options.Query, _conventions.AttributesOnly);
        return span;
    }

    /// <summary>Completes a search activity, recording isolated failures and its optional result payload.</summary>
    /// <param name="span">The activity handle, or null for no operation.</param>
    /// <param name="options">The result values, or defaults.</param>
    public void EndMemorySearchSpan(TraceSpan? span, EndMemorySearchSpanOptions? options = null)
    {
        if (!CanCompleteMemory(span)) return;
        options ??= new();
        MemorySpanContent.CompleteSearch(span!, options.Entries ?? [], options.StoreFailureCount, options.Error,
            span!.MemoryProfile ?? MemoryTelemetryProfile.TypeScript, _conventions);
    }

    /// <summary>Starts a manual memory write activity without changing ambient context.</summary>
    /// <param name="options">The content, destinations, and source policy.</param>
    public TraceSpan StartMemoryAddSpan(StartMemoryAddSpanOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Content, nameof(options.Content));
        Guard.NotNull(options.StoreNames, nameof(options.StoreNames));
        var names = options.StoreNames.ToArray();
        var attributes = MemoryAttributes("memory.add", options.Profile);
        attributes["memory.store.names"] = JsonSerializer.Serialize(names);
        attributes["memory.store.count"] = names.Length;
        ApplyMemoryCallAttributes(attributes, options.Profile, options.ParentSpan, options.CustomTraceAttributes, options.ExtraAttributes);
        var span = Create("memory.add", MemoryParent(options.ParentSpan), null, attributes, forceRoot: options.ForceRoot,
            memoryProfile: options.Profile, includeConstructorAttributes: false);
        span.RecordEvent("memory.content", "content", options.Content, _conventions.AttributesOnly);
        return span;
    }

    /// <summary>Completes a memory write activity.</summary>
    /// <param name="span">The activity handle, or null for no operation.</param>
    /// <param name="options">The failed-store count and original error, or defaults.</param>
    public void EndMemoryAddSpan(TraceSpan? span, EndMemoryAddSpanOptions? options = null)
    {
        if (!CanCompleteMemory(span)) return;
        options ??= new();
        MemorySpanContent.CompleteWrite(span!, options.StoreFailureCount, options.Error);
    }

    /// <summary>Starts a manual memory injection activity without changing ambient context.</summary>
    /// <param name="options">The optional limit and source policy, or defaults.</param>
    public TraceSpan StartMemoryInjectSpan(StartMemoryInjectSpanOptions? options = null)
    {
        options ??= new();
        var attributes = MemoryAttributes("memory.inject", options.Profile);
        if (options.MaxEntries.HasValue) attributes["memory.max_entries"] = options.MaxEntries.Value;
        ApplyMemoryCallAttributes(attributes, options.Profile, options.ParentSpan, options.CustomTraceAttributes, options.ExtraAttributes);
        return Create("memory.inject", MemoryParent(options.ParentSpan), null, attributes, memoryProfile: options.Profile, includeConstructorAttributes: false);
    }

    /// <summary>Completes fail-open injection successfully, including a renderer failure flag.</summary>
    /// <param name="span">The activity handle, or null for no operation.</param>
    /// <param name="options">The renderer's outcome.</param>
    public void EndMemoryInjectSpan(TraceSpan? span, EndMemoryInjectSpanOptions options)
    {
        if (!CanCompleteMemory(span)) return;
        Guard.NotNull(options, nameof(options));
        MemorySpanContent.CompleteInjection(span!, options.Injected, options.EntryCount, options.FormatError);
    }

    /// <summary>Starts a manual extraction root, optionally linked to a valid scheduling context.</summary>
    /// <param name="options">The store, message counts, and optional causal link.</param>
    public TraceSpan StartMemoryExtractSpan(StartMemoryExtractSpanOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.StoreName, nameof(options.StoreName));
        var attributes = MemoryAttributes("memory.extract", options.Profile);
        attributes["memory.store.name"] = options.StoreName;
        attributes["memory.message.count"] = options.MessageCount;
        attributes["memory.message.filtered_count"] = options.FilteredCount;
        if (options.Extractor is not null) attributes["memory.extractor"] = options.Extractor;
        ActivityLink[]? links = null;
        if (options.AgentSpanContext is { } context && context.TraceId != default && context.SpanId != default)
        {
            attributes["memory.parent.trace_id"] = context.TraceId.ToHexString();
            attributes["memory.parent.span_id"] = context.SpanId.ToHexString();
            links = [new(context)];
        }
        ApplyMemoryCallAttributes(attributes, options.Profile, null, null, options.ExtraAttributes);
        return Create("memory.extract", null, null, attributes, forceRoot: true, links: links,
            memoryProfile: options.Profile, includeConstructorAttributes: false);
    }

    /// <summary>Completes extraction without inventing an unknown saved-entry count.</summary>
    /// <param name="span">The activity handle, or null for no operation.</param>
    /// <param name="options">The known count and original failure, or defaults.</param>
    public void EndMemoryExtractSpan(TraceSpan? span, EndMemoryExtractSpanOptions? options = null)
    {
        if (!CanCompleteMemory(span)) return;
        options ??= new();
        MemorySpanContent.CompleteExtraction(span!, options.EntryCount, options.Error);
    }

    private Dictionary<string, object?> MemoryAttributes(string operation, MemoryTelemetryProfile profile)
    {
        if (!Enum.IsDefined(typeof(MemoryTelemetryProfile), profile)) throw new ArgumentException("Unknown memory telemetry profile.", nameof(profile));
        var serviceName = profile == MemoryTelemetryProfile.Python ? "strands-agents"
            : Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { Length: > 0 } name ? name : "strands-agents";
        return new(StringComparer.Ordinal)
        {
            ["gen_ai.operation.name"] = operation,
            [_conventions.Latest ? "gen_ai.provider.name" : "gen_ai.system"] = serviceName
        };
    }

    private static TraceSpan? MemoryParent(TraceSpan? parent) => parent?.RecordingContext is not null ? parent : null;

    private static bool CanCompleteMemory(TraceSpan? span)
    {
        if (span is null) return false;
        if (span.RecordingContext is not null) return true;
        span.EndContext(null);
        return false;
    }

    private static void ApplyMemoryCallAttributes(Dictionary<string, object?> attributes, MemoryTelemetryProfile profile, TraceSpan? parent,
        IReadOnlyDictionary<string, object?>? custom, IReadOnlyDictionary<string, object?>? extra)
    {
        if (profile == MemoryTelemetryProfile.TypeScript)
        {
            if (parent is not null || custom is not null || extra is not null)
                throw new ArgumentException("Explicit parents and per-call memory attributes require the Python profile.");
            return;
        }
        if (custom is not null) foreach (var value in custom) attributes[value.Key] = SnapshotAttribute(value.Value);
        if (extra is not null)
            foreach (var value in extra)
                if (value.Value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double)
                    attributes[value.Key] = value.Value;
    }
}
