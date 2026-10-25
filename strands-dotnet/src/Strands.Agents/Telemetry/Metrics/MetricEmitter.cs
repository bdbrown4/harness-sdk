using System.Diagnostics.Metrics;

namespace Strands.Agents.Telemetry;

internal sealed class MetricEmitter
{
    private static readonly Meter DefaultMeter = new(AgentMeter.MeterName);
    private readonly Meter _source;

    internal MetricEmitter(Meter? meter) => _source = meter ?? DefaultMeter;

    internal void InvocationStarted() => Counter("gen_ai.agent.invocation.count", 1);

    internal void CycleStarted(string cycleId)
    {
        Counter("gen_ai.agent.cycle.count", 1);
        var tags = CycleTags(cycleId);
        Counter("strands.event_loop.cycle_count", 1, "Count", tags);
        Counter("strands.event_loop.start_cycle", 1, "Count", tags);
    }

    internal void CycleCompleted(string cycleId, TimeSpan duration)
    {
        Histogram("gen_ai.agent.cycle.duration", duration.TotalMilliseconds, "ms");
        var tags = CycleTags(cycleId);
        Counter("strands.event_loop.end_cycle", 1, "Count", tags);
        Histogram("strands.event_loop.cycle_duration", duration.TotalSeconds, "s", tags);
    }

    internal void ModelUpdated(Usage? usage, ModelPerformanceMetrics? metrics)
    {
        if (usage is not null)
        {
            Counter("gen_ai.agent.tokens.input", usage.InputTokens);
            Counter("gen_ai.agent.tokens.output", usage.OutputTokens);
            TokenHistogram("strands.event_loop.input.tokens", usage.InputTokens);
            TokenHistogram("strands.event_loop.output.tokens", usage.OutputTokens);
            TokenHistogram("strands.event_loop.cache_read.input.tokens", usage.CacheReadInputTokens);
            TokenHistogram("strands.event_loop.cache_write.input.tokens", usage.CacheWriteInputTokens);
        }
        if (metrics is not null)
        {
            Histogram("gen_ai.agent.model.latency", metrics.Latency.TotalMilliseconds, "ms");
            Histogram("strands.event_loop.latency", metrics.Latency.TotalMilliseconds, "ms");
            if (metrics.TimeToFirstToken is { } first)
            {
                if (first > TimeSpan.Zero) Histogram("gen_ai.server.time_to_first_token", first.TotalSeconds, "s");
                Histogram("strands.model.time_to_first_token", first.TotalMilliseconds, "ms");
            }
        }
    }

    internal void ToolCompleted(ToolUseBlock tool, TimeSpan duration, bool success)
    {
        KeyValuePair<string, object?>[] tsTags = [new("gen_ai.tool.name", tool.Name)];
        Counter("gen_ai.agent.tool.call.count", 1, tags: tsTags);
        Histogram("gen_ai.agent.tool.duration", duration.TotalMilliseconds, "ms", tsTags);
        if (!success) Counter("gen_ai.agent.tool.error.count", 1, tags: tsTags);
        KeyValuePair<string, object?>[] pyTags = [new("tool_name", tool.Name), new("tool_use_id", tool.ToolUseId)];
        Counter("strands.tool.call_count", 1, "Count", pyTags);
        Histogram("strands.tool.duration", duration.TotalSeconds, "s", pyTags);
        Counter(success ? "strands.tool.success_count" : "strands.tool.error_count", 1, "Count", pyTags);
    }

    private static KeyValuePair<string, object?>[] CycleTags(string id) => [new("event_loop_cycle_id", id)];

    // Meter reuses instruments with identical metadata. Creating them at the emission
    // boundary also keeps a failing InstrumentPublished callback out of static initialization.
    private void Counter(string name, long value, string? unit = null, KeyValuePair<string, object?>[]? tags = null)
    {
        try { _source.CreateCounter<long>(name, unit).Add(value, tags ?? []); }
        catch (Exception) { /* Instrument publication and exporter callbacks cannot fail agent execution. */ }
    }

    private void Histogram(string name, double value, string unit, KeyValuePair<string, object?>[]? tags = null)
    {
        try { _source.CreateHistogram<double>(name, unit).Record(value, tags ?? []); }
        catch (Exception) { /* Metrics are observational; local collection is committed independently. */ }
    }

    private void TokenHistogram(string name, long value)
    {
        try { _source.CreateHistogram<long>(name, "token").Record(value); }
        catch (Exception) { /* Metrics are observational; local collection is committed independently. */ }
    }
}
