using System.Diagnostics;
using Strands.Agents.Memory;

namespace Strands.Agents.Telemetry;

/// <summary>One operation's exported span and optional local execution trace.</summary>
public sealed class TraceSpan : IDisposable
{
    private readonly Activity? _activity;
    private readonly Action<TraceSpan>? _onEnd;
    private readonly bool _latestConventions;
    private readonly TraceConventions _conventions;
    private readonly string? _operation;
    private int _ended;

    internal TraceSpan(Activity? activity, AgentTrace? trace, TraceConventions conventions, string? operation, Action<TraceSpan>? onEnd = null, MemoryTelemetryProfile? memoryProfile = null)
    {
        _activity = activity;
        Trace = trace;
        _conventions = conventions;
        _latestConventions = conventions.Latest;
        _operation = operation;
        _onEnd = onEnd;
        MemoryProfile = memoryProfile;
    }
    /// <summary>The exported span context, or null when no listener samples this operation.</summary>
    public ActivityContext? Context => _activity?.Context;
    internal ActivityContext? RecordingContext => Volatile.Read(ref _ended) == 0 && _activity is { IsAllDataRequested: true } activity ? activity.Context : null;
    internal MemoryTelemetryProfile? MemoryProfile { get; }
    /// <summary>The local cycle, model, or tool trace, independent of exporter sampling.</summary>
    public AgentTrace? Trace { get; }
    /// <summary>Sets an exported span attribute without affecting application execution if a listener fails.</summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value.</param>
    public void SetAttribute(string name, object? value)
    {
        if (Volatile.Read(ref _ended) != 0) return;
        try { _activity?.SetTag(name, value); }
        catch (Exception) { /* Observability callbacks cannot fail the application operation. */ }
    }

    internal void RecordEvent(string name, string key, string value, bool attributesOnly)
    {
        if (Volatile.Read(ref _ended) != 0 || _activity?.IsAllDataRequested != true) return;
        try
        {
            if (attributesOnly) _activity.SetTag(key, value);
            else _activity.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection { { key, value } }));
        }
        catch (Exception) { /* Content listeners cannot affect the application operation. */ }
    }

    /// <summary>Runs a callback with this span as ambient, restoring the caller's context afterward.</summary>
    /// <typeparam name="T">The callback result type.</typeparam>
    /// <param name="action">The application callback.</param>
    /// <returns>The callback's result.</returns>
    public T WithContext<T>(Func<T> action)
    {
        Guard.NotNull(action, nameof(action));
        if (_activity is null) return action();
        var ambient = Activity.Current;
        RestoreAmbient(_activity);
        try { return action(); }
        finally { RestoreAmbient(ambient); }
    }

    /// <summary>Runs an asynchronous callback with this span as ambient across awaits.</summary>
    /// <typeparam name="T">The callback result type.</typeparam>
    /// <param name="action">The asynchronous application callback.</param>
    /// <returns>The callback's result, preserving its original failure.</returns>
    public async Task<T> WithContextAsync<T>(Func<Task<T>> action)
    {
        Guard.NotNull(action, nameof(action));
        if (_activity is null) return await action().ConfigureAwait(false);
        var ambient = Activity.Current;
        RestoreAmbient(_activity);
        try { return await action().ConfigureAwait(false); }
        finally { RestoreAmbient(ambient); }
    }

    internal async ValueTask<T> WithValueTaskContextAsync<T>(Func<ValueTask<T>> action)
    {
        Guard.NotNull(action, nameof(action));
        if (_activity is null) return await action().ConfigureAwait(false);
        var ambient = Activity.Current;
        RestoreAmbient(_activity);
        try { return await action().ConfigureAwait(false); }
        finally { RestoreAmbient(ambient); }
    }

    /// <summary>Records model-visible input using the selected semantic conventions and content policy.</summary>
    /// <param name="request">The immutable messages, instructions and available tool definitions.</param>
    public void RecordInput(ModelRequest request)
    {
        Guard.NotNull(request, nameof(request));
        if (Volatile.Read(ref _ended) != 0 || _activity?.IsAllDataRequested != true) return;
        try { TraceContent.Input(_activity, request, _conventions, _operation); }
        catch (Exception) { /* Content serialization is observational and cannot fail the model operation. */ }
    }

    /// <summary>Completes a tool operation with its correlated result.</summary>
    /// <param name="result">The actual tool result.</param>
    /// <param name="error">An application failure, when present.</param>
    public void EndToolCall(ToolResultBlock result, Exception? error = null)
    {
        Guard.NotNull(result, nameof(result));
        Complete(null, null, error, null, result);
    }

    internal void RecordToolInput(ToolUseBlock tool)
    {
        if (_activity?.IsAllDataRequested != true) return;
        try { TraceContent.ToolInput(_activity, tool, _conventions); }
        catch (Exception) { /* Tool argument serialization cannot affect execution. */ }
    }
    /// <summary>Completes this operation once and records its optional result, usage, and failure.</summary>
    /// <param name="output">The immutable output message.</param>
    /// <param name="usage">Token accounting for the operation.</param>
    /// <param name="error">The original application failure, when present.</param>
    /// <param name="stopReason">The provider or agent stop reason.</param>
    public void End(Message? output = null, Usage? usage = null, Exception? error = null, string? stopReason = null)
        => Complete(output, usage, error, stopReason, null);

    internal void EndContext(Exception? error) => Complete(null, null, error, null, null, contextCompletion: true);

    private void Complete(Message? output, Usage? usage, Exception? error, string? stopReason, ToolResultBlock? toolResult, bool contextCompletion = false)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        Trace?.End(message: output);
        var ambient = Activity.Current;
        try
        {
            if (_activity is null) return;
            if (!contextCompletion) _activity.SetTag("gen_ai.event.end_time", DateTimeOffset.UtcNow.ToString("O"));
            if (stopReason is not null) _activity.SetTag("gen_ai.response.finish_reasons", new[] { stopReason });
            if (!contextCompletion || error is not null)
                _activity.SetStatus(error is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                    contextCompletion && error is not null ? error.GetType().Name + ": " + error.Message : error?.Message);
            if (error is null && _conventions.UsePythonToolErrorStatus && toolResult is { Status: ToolResultStatus.Error })
            {
                var description = toolResult.ContentBlocks.Count == 0 ? toolResult.Content
                    : toolResult.ContentBlocks.OfType<TextBlock>().FirstOrDefault()?.Text ?? "tool returned error status";
                if (!string.IsNullOrEmpty(description)) _activity.SetStatus(ActivityStatusCode.Error, description);
            }
            if (error is not null)
            {
                if (!contextCompletion) _activity.SetTag("error.type", error.GetType().FullName);
                _activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", error.GetType().FullName },
                    { "exception.message", error.Message },
                    { "exception.stacktrace", error.StackTrace },
                }));
            }
            if (_activity.IsAllDataRequested)
            {
                if (_conventions.Langfuse && _operation == "invoke_agent") _activity.SetTag("langfuse.observation.type", "span");
                try
                {
                    if (output is not null) TraceContent.Output(_activity, output, stopReason, _conventions, _operation);
                    if (toolResult is not null) TraceContent.ToolOutput(_activity, toolResult, _conventions);
                }
                catch (Exception) { /* Content failures cannot hide status, usage, or span completion. */ }
            }
            if (usage is not null) SetUsage(usage);
        }
        catch (Exception) { /* Exporter or attribute failures must not replace an application error. */ }
        finally
        {
            try { _activity?.Stop(); }
            catch (Exception) { /* A failing listener must not prevent local completion or cursor cleanup. */ }
            finally
            {
                RestoreAmbient(ambient);
                try { _onEnd?.Invoke(this); }
                catch (Exception) { /* Cursor cleanup is internal and cannot replace the operation result. */ }
            }
        }
    }

    internal static void RestoreAmbient(Activity? activity)
    {
        try { Activity.Current = activity; }
        catch (Exception) { /* CurrentChanged observers run after the context value has been restored. */ }
    }

    private void SetUsage(Usage usage)
    {
        var prompt = checked(usage.InputTokens + usage.OutputTokens) == usage.TotalTokens ? usage.InputTokens
            : checked(usage.InputTokens + usage.CacheReadInputTokens + usage.CacheWriteInputTokens);
        _activity!.SetTag("gen_ai.usage.prompt_tokens", prompt);
        _activity.SetTag("gen_ai.usage.input_tokens", prompt);
        _activity.SetTag("gen_ai.usage.completion_tokens", usage.OutputTokens);
        _activity.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        _activity.SetTag("gen_ai.usage.total_tokens", usage.TotalTokens);
        if (usage.CacheReadInputTokens > 0)
        {
            _activity.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CacheReadInputTokens);
            if (!_latestConventions) _activity.SetTag("gen_ai.usage.cache_read_input_tokens", usage.CacheReadInputTokens);
        }
        if (usage.CacheWriteInputTokens > 0)
        {
            _activity.SetTag("gen_ai.usage.cache_creation.input_tokens", usage.CacheWriteInputTokens);
            if (!_latestConventions) _activity.SetTag("gen_ai.usage.cache_write_input_tokens", usage.CacheWriteInputTokens);
        }
    }
    /// <summary>Completes this operation if it has not already ended.</summary>
    public void Dispose() => End();
}
