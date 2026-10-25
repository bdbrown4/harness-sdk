using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;
using Strands.Agents.Tasks;
using Strands.Agents.Telemetry;

namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly Tracer _tracer;
    private readonly AgentMeter _meter;
    private TraceSpan? _invocationTrace;
    private TraceSpan? _cycleTrace;
    private AgentCycle? _metricCycle;
    private bool _invocationInputRecorded;
    private ActivityContext? _memoryInvocationTraceContext;

    /// <summary>A detached snapshot of this agent's collected execution statistics.</summary>
    public AgentMetrics Metrics => _meter.Metrics;

    /// <summary>A detached collection of this invocation's local cycle traces.</summary>
    public IReadOnlyList<AgentTrace> Traces => _tracer.Traces;

    private void BeginInvocationTelemetry()
    {
        _meter.StartNewInvocation();
        _invocationInputRecorded = false;
        _invocationTrace = _tracer.StartAgentSpan(Name, Id, TelemetryModelId(Model));
        _memoryInvocationTraceContext = _invocationTrace.RecordingContext;
    }

    private void RecordInvocationInputTelemetry(string? prompt, InvocationOptions options)
    {
        if (_invocationInputRecorded) return;
        _invocationInputRecorded = true;
        var messages = new List<Message>();
        if ((options.InputMessage ?? (prompt is not null ? Message.User(prompt) : null)) is { } input) messages.Add(input);
        messages.AddRange(options.InputMessages);
        _invocationTrace?.RecordInput(new(messages, SystemPrompt, Tools.Select(RegisteredToolSpec).ToArray()) { SystemPromptContent = SystemPromptContent });
    }

    private static IReadOnlyDictionary<string, object?>? CopyTraceAttributes(IReadOnlyDictionary<string, object?>? values)
        => values is null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            values.ToDictionary(pair => pair.Key, pair => pair.Value is Array array ? array.Clone() : pair.Value, StringComparer.Ordinal));

    private void BeginCycleTelemetry()
    {
        CompleteCycleTelemetry();
        _metricCycle = _meter.StartCycle();
        _cycleTrace = _tracer.StartAgentLoopSpan(_metricCycle.CycleId);
    }

    private void CompleteCycleTelemetry(Exception? error = null)
    {
        var metric = _metricCycle;
        var trace = _cycleTrace;
        _metricCycle = null;
        _cycleTrace = null;
        try { metric?.End(); }
        catch (Exception) { /* Collection failures cannot replace execution results. */ }
        trace?.End(error: error);
    }

    private AgentResult CaptureResultTelemetry(AgentResult result, Exception? error = null)
    {
        CompleteCycleTelemetry(error);
        return result with { Metrics = _meter.Metrics, Traces = _tracer.Traces };
    }

    private void CompleteInvocationTelemetry(InvocationProgress progress, Exception? error, string? stopReason = null)
    {
        CompleteCycleTelemetry(error);
        var trace = _invocationTrace;
        _invocationTrace = null;
        if (trace is null) return;
        var metrics = _meter.Metrics;
        var usage = _tracer.UseLatestInvocationTokens ? metrics.LatestAgentInvocation?.Usage ?? new() : metrics.AccumulatedUsage;
        trace.End(progress.Message, usage, error, stopReason);
    }

    private void RecordModelTelemetry(ModelTurn turn)
    {
        try { _metricCycle?.Update(turn.UsageReported ? turn.Usage : null, turn.PerformanceMetrics); }
        catch (Exception) { /* Collection failures cannot replace a provider result or failure. */ }
    }

    private static string? TelemetryModelId(IModel model)
    {
        try { return (model as IModelMetadataProvider)?.Metadata?.ModelId; }
        catch (Exception) { return null; }
    }

    private async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> StreamObservedToolTerminalAsync(ExecuteToolContext context,
        ToolTerminalOutcome terminal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = context.ToolUse;
        var span = _tracer.StartToolCallSpan(call);
        var timer = Stopwatch.StartNew();
        ToolResultBlock? result = null;
        Exception? failure = null;
        var iterator = StreamToolBodyTerminalAsync(context, terminal, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool next;
                IMiddlewareEvent<ExecuteToolResult>? current = null;
                try
                {
                    next = await span.WithValueTaskContextAsync(() => iterator.MoveNextAsync()).ConfigureAwait(false);
                    if (next)
                    {
                        current = iterator.Current;
                        if (current is MiddlewareResultEvent<ExecuteToolResult> completed) result = completed.Result.Result;
                    }
                }
                catch (Exception error) { failure = error; throw; }
                if (!next) break;
                yield return current!;
            }
        }
        finally
        {
            try { await span.WithValueTaskContextAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (failure is null) { failure = cleanup; throw; }
                if (!ReferenceEquals(failure, cleanup)) AttachCleanupError(failure, cleanup);
            }
            finally
            {
                CompleteToolTelemetry(call, span, timer, result, failure);
            }
        }
    }

    private async ValueTask<ToolResultBlock> ObserveBackgroundToolBodyAsync(ToolUseBlock call, Func<ValueTask<ToolResultBlock>> body)
    {
        // Background work can outlive a foreground invocation and must not use its mutable cycle cursor.
        var tracer = new Tracer(_options.UsePythonToolErrorStatus, _options.TraceAttributes);
        var span = tracer.StartToolCallSpan(call);
        var timer = Stopwatch.StartNew();
        ToolResultBlock? result = null;
        Exception? failure = null;
        try { return result = await span.WithValueTaskContextAsync(body).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally { CompleteToolTelemetry(call, span, timer, result, failure); }
    }

    private void CompleteToolTelemetry(ToolUseBlock call, TraceSpan span, Stopwatch timer, ToolResultBlock? result, Exception? failure)
    {
        timer.Stop();
        try { _meter.RecordToolCall(call, timer.Elapsed, result?.Status == ToolResultStatus.Success); }
        catch (Exception) { /* Statistics cannot replace the actual execution result. */ }
        var observedError = failure is InterruptException ? null : failure ?? result?.Error;
        if (result is not null) span.EndToolCall(result, observedError);
        else span.End(error: observedError, stopReason: failure is InterruptException ? StopReasons.Interrupt : null);
    }
}
