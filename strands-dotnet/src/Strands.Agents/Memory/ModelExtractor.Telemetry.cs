using Strands.Agents.Telemetry;

namespace Strands.Agents.Memory;

public sealed partial class ModelExtractor
{
    private static ModelExtractorOptions ValidateTelemetryOptions(ModelExtractorOptions options)
    {
        if (options.TelemetryProfile is { } profile && !Enum.IsDefined(typeof(MemoryTelemetryProfile), profile))
            throw new ArgumentOutOfRangeException(nameof(options.TelemetryProfile));
        return options;
    }

    private ModelTrace StartModelTrace(IModel model, ModelRequest request, ExtractionContext? context)
    {
        var profile = _options.TelemetryProfile ?? context?.TelemetryProfile ?? MemoryTelemetryProfile.TypeScript;
        if (!Enum.IsDefined(typeof(MemoryTelemetryProfile), profile)) throw new ArgumentOutOfRangeException(nameof(context));
        var tracer = context?.Tracer ?? (profile == MemoryTelemetryProfile.Python ? new Tracer() : null);
        if (tracer is null) return new(null, false);
        string? modelId = null;
        try { modelId = (model as IModelMetadataProvider)?.Metadata?.ModelId; }
        catch (Exception) { /* Metadata observation cannot prevent extraction. */ }
        var span = tracer.StartModelInvokeSpan(modelId);
        span.RecordInput(request);
        return new(span, profile == MemoryTelemetryProfile.Python);
    }

    private sealed class ModelTrace(TraceSpan? span, bool active) : IDisposable
    {
        internal T Run<T>(Func<T> callback) => active && span is not null ? span.WithContext(callback) : callback();
        internal ValueTask<T> RunAsync<T>(Func<ValueTask<T>> callback)
            => active && span is not null ? span.WithValueTaskContextAsync(callback) : callback();
        internal void Complete(Message output, ModelStreamAccumulator accumulator, string stopReason)
        {
            if (span?.RecordingContext is null) { span?.EndContext(null); return; }
            // Python's streaming helper initializes zero usage even without a measured provider event.
            // This is extractor-profile accounting only; the accumulator retains its presence flag.
            var usage = active || accumulator.UsageReported ? accumulator.Usage : null;
            if (usage is not null && accumulator.PerformanceMetrics is { } metrics)
            {
                if (metrics.Latency > TimeSpan.Zero) span?.SetAttribute("gen_ai.server.request.duration", metrics.Latency.TotalMilliseconds);
                if (active && metrics.TimeToFirstToken > TimeSpan.Zero)
                    span?.SetAttribute("gen_ai.server.time_to_first_token", metrics.TimeToFirstToken.Value.TotalMilliseconds);
            }
            span?.End(output, usage, stopReason: stopReason);
        }
        internal void End(Message? output = null, Usage? usage = null, Exception? error = null, string? stopReason = null)
        {
            if (span?.RecordingContext is null) span?.EndContext(null);
            else span.End(output, usage, error, stopReason);
        }
        public void Dispose() => span?.Dispose();
    }
}
