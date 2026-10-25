using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Telemetry;

internal static class MemorySpanContent
{
    internal static void CompleteExtraction(TraceSpan span, int? entryCount, Exception? error)
    {
        if (entryCount.HasValue) span.SetAttribute("memory.entry.count", entryCount.Value);
        span.End(error: error);
    }

    internal static void CompleteInjection(TraceSpan span, bool injected, int entryCount, bool formatError)
    {
        span.SetAttribute("memory.injected", injected);
        span.SetAttribute("memory.entry.count", entryCount);
        if (formatError) span.SetAttribute("memory.inject.format_error", true);
        span.End();
    }

    internal static void CompleteWrite(TraceSpan span, int failures, Exception? error)
    {
        span.SetAttribute("memory.store.failure_count", failures);
        span.End(error: error);
    }

    internal static void CompleteSearch(TraceSpan span, IReadOnlyList<MemoryEntry> entries, int failures,
        Exception? error, MemoryTelemetryProfile profile, TraceConventions conventions)
    {
        try
        {
            span.SetAttribute("memory.result.count", entries.Count);
            span.SetAttribute("memory.store.failure_count", failures);
            if (error is null)
            {
                var values = entries.Select(entry =>
                {
                    var value = new Dictionary<string, object?> { ["content"] = entry.Content };
                    if (entry.StoreName is not null || profile == MemoryTelemetryProfile.Python)
                        value[profile == MemoryTelemetryProfile.Python ? "store_name" : "storeName"] = entry.StoreName;
                    if (entry.Metadata.HasValue || profile == MemoryTelemetryProfile.Python) value["metadata"] = entry.Metadata;
                    return value;
                });
                span.RecordEvent("memory.results", "content", JsonSerializer.Serialize(values), conventions.AttributesOnly);
            }
        }
        catch (Exception) { /* Observing caller-owned results cannot affect an application operation. */ }
        finally { span.End(error: error); }
    }
}
