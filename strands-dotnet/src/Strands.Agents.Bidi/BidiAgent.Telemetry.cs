using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private BidiTelemetry StartBidiSession(BidiSessionOptions session)
    {
        var tracer = new Tracer(usePythonToolErrorStatus: true);
        var conventions = new TraceConventions(usePythonToolErrorStatus: true);
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (string.IsNullOrEmpty(serviceName)) serviceName = "strands-agents";
        var attributes = BidiAttributes("bidi_session", conventions, serviceName!);
        var name = Name;
        attributes["gen_ai.agent.name"] = name;
        string? modelId = null;
        try { modelId = (_options.Model as IModelMetadataProvider)?.Metadata?.ModelId; }
        catch (Exception) { /* Optional identity cannot fail connection establishment. */ }
        if (!string.IsNullOrEmpty(modelId)) attributes["gen_ai.request.model"] = modelId;
        if (session.Tools.Count != 0) attributes["gen_ai.agent.tools"] = JsonSerializer.Serialize(session.Tools.Select(tool => tool.Name).ToArray());
        if (!string.IsNullOrEmpty(session.SystemPrompt))
            attributes["gen_ai.system_instructions"] = conventions.Content("gen_ai.system_instructions", () => session.SystemPrompt!);
        return new(tracer, conventions, serviceName!, modelId, tracer.StartSpan("bidi_session " + name, attributes: attributes));
    }

    private static Dictionary<string, object?> BidiAttributes(string operation, TraceConventions conventions, string serviceName)
        => new(StringComparer.Ordinal)
        {
            ["gen_ai.operation.name"] = operation,
            [conventions.Latest ? "gen_ai.provider.name" : "gen_ai.system"] = serviceName,
        };

    private static TraceSpan StartBidiSpan(BidiTelemetry telemetry, string name, string operation,
        IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var tags = BidiAttributes(operation, telemetry.Conventions, telemetry.ServiceName);
        if (attributes is not null) foreach (var attribute in attributes) tags[attribute.Key] = attribute.Value;
        return telemetry.Tracer.StartSpan(name, telemetry.Session, attributes: tags);
    }

    private static TraceSpan StartBidiConnect(BidiTelemetry telemetry)
    {
        var attributes = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(telemetry.ModelId)) attributes["gen_ai.request.model"] = telemetry.ModelId;
        return StartBidiSpan(telemetry, "bidi_connect", "bidi_connect", attributes);
    }

    private static TraceSpan StartBidiRestart(BidiTelemetry telemetry, BidiRestartReason reason, BidiConnectionTimeoutException? timeout)
    {
        var attributes = new Dictionary<string, object?> { ["gen_ai.bidi.restart_reason"] = reason == BidiRestartReason.Timeout ? "timeout" : "scheduled" };
        if (timeout is not null && !string.IsNullOrEmpty(timeout.Message)) attributes["gen_ai.bidi.restart_error_message"] = timeout.Message;
        return StartBidiSpan(telemetry, "bidi_connection_restart", "bidi_connection_restart", attributes);
    }

    private static TraceSpan StartBidiTool(BidiTelemetry telemetry, ToolUseBlock call)
    {
        var span = StartBidiSpan(telemetry, "execute_tool " + call.Name, "execute_tool",
            new Dictionary<string, object?> { ["gen_ai.tool.name"] = call.Name, ["gen_ai.tool.call.id"] = call.ToolUseId });
        span.RecordToolInput(call);
        return span;
    }

    private static Exception? BidiTraceError(Exception? error, CancellationToken token)
        => error is OperationCanceledException && token.IsCancellationRequested ? null : error;

    private static void ObserveBidiOutput(Conversation conversation, ReaderContext context, IBidiOutputEvent output)
    {
        if (conversation.Telemetry is not { } telemetry) return;
        switch (output)
        {
            case BidiResponseStartEvent started:
                EndBidiResponse(context);
                context.ResponseSpan = StartBidiSpan(telemetry, "bidi_response", "bidi_response",
                    new Dictionary<string, object?> { ["gen_ai.response.id"] = started.ResponseId });
                context.ResponseStarted = Stopwatch.GetTimestamp();
                context.FirstAudioMilliseconds = null;
                break;
            case BidiAudioDeltaEvent when context.ResponseSpan is not null && context.FirstAudioMilliseconds is null:
                context.FirstAudioMilliseconds = (long)((Stopwatch.GetTimestamp() - context.ResponseStarted) * 1000d / Stopwatch.Frequency);
                break;
            case BidiResponseStopEvent:
                EndBidiResponse(context);
                break;
            case BidiBargeInEvent when telemetry.Session.RecordingContext is not null:
                telemetry.Session.WithContext(() =>
                {
                    try { Activity.Current?.AddEvent(new ActivityEvent("bidi_barge_in")); }
                    catch (Exception) { /* Observability cannot change the accepted output. */ }
                    return true;
                });
                break;
        }
    }

    private static void EndBidiResponse(ReaderContext context, Exception? error = null)
    {
        var span = context.ResponseSpan;
        context.ResponseSpan = null;
        if (context.FirstAudioMilliseconds is { } milliseconds) span?.SetAttribute("gen_ai.server.time_to_first_audio", milliseconds);
        span?.End(error: error);
    }

    // Called only while accepting a current-generation event under the history gate.
    private static void RecordBidiUsage(Conversation conversation, BidiUsageEvent output)
    {
        if (conversation.Telemetry is not { } telemetry || Volatile.Read(ref telemetry.Ended) != 0 || !telemetry.UsageValid) return;
        var cacheRead = output.InputDetails is { } details && details.TryGetValue("cache_read", out var count) ? count : 0;
        try
        {
            var input = checked(telemetry.InputTokens + output.Usage.InputTokens);
            var generated = checked(telemetry.OutputTokens + output.Usage.OutputTokens);
            var total = checked(telemetry.TotalTokens + output.Usage.TotalTokens);
            var cached = checked(telemetry.CacheReadTokens + cacheRead);
            telemetry.InputTokens = input;
            telemetry.OutputTokens = generated;
            telemetry.TotalTokens = total;
            telemetry.CacheReadTokens = cached;
        }
        catch (OverflowException) { telemetry.UsageValid = false; }
    }

    private void EndBidiSession(BidiTelemetry? telemetry, Exception? error = null)
    {
        if (telemetry is null || Interlocked.Exchange(ref telemetry.Ended, 1) != 0) return;
        long input, output, total, cached;
        bool valid;
        lock (_gate)
        {
            input = telemetry.InputTokens;
            output = telemetry.OutputTokens;
            total = telemetry.TotalTokens;
            cached = telemetry.CacheReadTokens;
            valid = telemetry.UsageValid;
        }
        if (valid)
        {
            Positive("gen_ai.usage.input_tokens", input);
            Positive("gen_ai.usage.output_tokens", output);
            Positive("gen_ai.usage.total_tokens", total);
            Positive("gen_ai.usage.cache_read.input_tokens", cached);
            if (!telemetry.Conventions.Latest) Positive("gen_ai.usage.cache_read_input_tokens", cached);
        }
        telemetry.Session.End(error: error);
        void Positive(string name, long value) { if (value > 0) telemetry.Session.SetAttribute(name, value); }
    }

    private sealed class BidiTelemetry(Tracer tracer, TraceConventions conventions, string serviceName, string? modelId, TraceSpan session)
    {
        internal Tracer Tracer { get; } = tracer;
        internal TraceConventions Conventions { get; } = conventions;
        internal string ServiceName { get; } = serviceName;
        internal string? ModelId { get; } = modelId;
        internal TraceSpan Session { get; } = session;
        internal long InputTokens;
        internal long OutputTokens;
        internal long TotalTokens;
        internal long CacheReadTokens;
        internal bool UsageValid = true;
        internal int Ended;
    }
}
