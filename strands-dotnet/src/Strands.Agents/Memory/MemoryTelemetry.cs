using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Memory;

internal sealed class MemoryTelemetry
{
    private readonly Tracer _tracer;
    private readonly MemoryTelemetryProfile _profile;
    private readonly TraceConventions _conventions = new();
    private readonly string _serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { Length: > 0 } name ? name : "strands-agents";
    internal Tracer Tracer => _tracer;
    internal MemoryTelemetryProfile Profile => _profile;

    internal MemoryTelemetry(MemoryTelemetryOptions options)
    {
        if (!Enum.IsDefined(typeof(MemoryTelemetryProfile), options.Profile)) throw new ArgumentException("Unknown memory telemetry profile.", nameof(options));
        _profile = options.Profile;
        _tracer = new(options.TraceAttributes);
    }

    internal Operation Search(string query, string[] storeNames, int? limit)
        => Start("memory.search", query, storeNames, limit, false);

    internal Operation Add(string content, string[] storeNames, bool detached)
        => Start("memory.add", content, storeNames, null, detached);

    internal Operation Inject(int maximumEntries)
    {
        var attributes = Attributes("memory.inject");
        attributes["memory.max_entries"] = maximumEntries;
        return new(_tracer.StartSpan("memory.inject", attributes: attributes), _conventions, _profile);
    }

    internal Operation Extract(string storeName, int messageCount, int filteredCount, string? extractor, ActivityContext? schedulingContext)
    {
        var attributes = Attributes("memory.extract");
        attributes["memory.store.name"] = storeName;
        attributes["memory.message.count"] = messageCount;
        attributes["memory.message.filtered_count"] = filteredCount;
        if (extractor is not null) attributes["memory.extractor"] = extractor;
        ActivityLink[]? links = null;
        if (schedulingContext is { } context && context.TraceId != default && context.SpanId != default)
        {
            attributes["memory.parent.trace_id"] = context.TraceId.ToHexString();
            attributes["memory.parent.span_id"] = context.SpanId.ToHexString();
            links = [new(context)];
        }
        return new(_tracer.StartSpan("memory.extract", forceRoot: true, links: links, attributes: attributes), _conventions, _profile);
    }

    private Dictionary<string, object?> Attributes(string operation) => new(StringComparer.Ordinal)
    {
        ["gen_ai.operation.name"] = operation,
        [_conventions.Latest ? "gen_ai.provider.name" : "gen_ai.system"] = _serviceName,
    };

    private Operation Start(string operation, string input, string[] storeNames, int? limit, bool forceRoot)
    {
        var attributes = Attributes(operation);
        attributes["memory.store.names"] = JsonSerializer.Serialize(storeNames);
        attributes["memory.store.count"] = storeNames.Length;
        if (limit.HasValue) attributes["memory.max_search_results"] = limit.Value;
        var span = _tracer.StartSpan(operation, forceRoot: forceRoot, attributes: attributes);
        span.RecordEvent(operation == "memory.search" ? "memory.query" : "memory.content", "content", input, _conventions.AttributesOnly);
        return new(span, _conventions, _profile);
    }

    internal sealed class Operation(TraceSpan span, TraceConventions conventions, MemoryTelemetryProfile profile) : IDisposable
    {
        internal ValueTask<T> RunAsync<T>(Func<ValueTask<T>> callback) => profile == MemoryTelemetryProfile.Python
            ? span.WithValueTaskContextAsync(callback) : callback();

        internal ValueTask<T> RunActiveAsync<T>(Func<ValueTask<T>> callback) => span.WithValueTaskContextAsync(callback);

        internal void Complete(IReadOnlyList<MemoryEntry> entries, int failures, Exception? error = null)
            => MemorySpanContent.CompleteSearch(span, entries, failures, error, profile, conventions);

        internal void CompleteWrite(int failures, Exception? error = null)
            => MemorySpanContent.CompleteWrite(span, failures, error);

        internal void CompleteInjection(bool injected, int entryCount, bool formatError)
            => MemorySpanContent.CompleteInjection(span, injected, entryCount, formatError);

        internal void CompleteExtraction(int? entryCount, Exception? error)
            => MemorySpanContent.CompleteExtraction(span, entryCount, error);

        public void Dispose() => span.Dispose();
    }
}
