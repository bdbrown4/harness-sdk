using System.Diagnostics;
using System.Text.Json;

namespace Strands.Agents.Memory;

internal sealed class ExtractionBinding(MemoryStore store, MemoryExtractionOptions options, IExtractor? extractor)
{
    internal MemoryStore Store { get; } = store;
    internal MemoryExtractionOptions Options { get; } = options;
    internal IExtractor? Extractor { get; } = extractor;
    // Only confirmed saves advance this mark: another binding must never trim an in-flight batch that can still fail.
    internal long Mark = -1;
    internal int Failures;
    internal int ProbeRequests;
    internal Task Chain = Task.CompletedTask;
}

internal sealed class ExtractionCoordinator(ExtractionBinding[] bindings, IModel defaultModel, MemoryTelemetry telemetry)
{
    private readonly object _gate = new();
    private readonly List<(long Sequence, Message Message)> _pending = [];
    private long _nextSequence;

    internal void Record(Message message)
    {
        var detached = new Message(message.Role, message.Content, message.TrackingId, message.Metadata);
        lock (_gate) _pending.Add((_nextSequence++, detached));
    }

    internal void Process(ExtractionBinding binding)
        => Process(binding, Activity.Current is { IsAllDataRequested: true } activity ? activity.Context : null);

    internal void Process(ExtractionBinding binding, ActivityContext? schedulingContext) => _ = Enqueue(binding, false, schedulingContext);

    private Task Enqueue(ExtractionBinding binding, bool force, ActivityContext? schedulingContext = null)
    {
        Task previous;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!force && binding.Failures >= 10 && ++binding.ProbeRequests % 3 != 0) return Task.CompletedTask;
            previous = binding.Chain;
            binding.Chain = completion.Task;
        }
        _ = RunQueuedAsync(previous, binding, completion, schedulingContext);
        return completion.Task;
    }

    private async Task RunQueuedAsync(Task previous, ExtractionBinding binding, TaskCompletionSource<bool> completion, ActivityContext? schedulingContext)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await ExtractAsync(binding, schedulingContext).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A background coordinator failure must be observed even when no caller flushes it.
            try { SdkLogging.Error(error, "Memory extraction scheduling failed for store '{Store}': {Error}", binding.Store.Name, error.Message); }
            catch (Exception) { /* A diagnostic sink must not leave the accepted background chain permanently incomplete. */ }
        }
        finally { completion.TrySetResult(true); }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var binding in bindings) _ = Enqueue(binding, true);
        while (true)
        {
            Task[] snapshot;
            lock (_gate) snapshot = bindings.Select(binding => binding.Chain).ToArray();
            await TaskCancellation.WaitAsync(Task.WhenAll(snapshot), cancellationToken).ConfigureAwait(false);
            lock (_gate)
                if (bindings.Select(binding => binding.Chain).SequenceEqual(snapshot)) return;
        }
    }

    private async Task ExtractAsync(ExtractionBinding binding, ActivityContext? schedulingContext)
    {
        (long Sequence, Message Message)[] fresh;
        long mark;
        lock (_gate)
        {
            mark = binding.Mark;
            fresh = _pending.Where(item => item.Sequence > mark).ToArray();
            if (fresh.Length == 0) return;
        }
        MemoryTelemetry.Operation? operation = null;
        Exception? saveError = null;
        int? entryCount = null;
        try
        {
            var excluded = new HashSet<string>(binding.Options.ExcludeContentBlocks, StringComparer.Ordinal);
            var filtered = fresh.Select(item => (item.Sequence, Content: item.Message.Content.Where(block => !IsExcluded(block, excluded)).ToArray(), item.Message))
                .Where(item => item.Content.Length > 0)
                .Select(item => (item.Sequence, Message: new Message(item.Message.Role, item.Content, item.Message.TrackingId, item.Message.Metadata))).ToArray();
            operation = telemetry.Extract(binding.Store.Name, filtered.Length, fresh.Length - filtered.Length, binding.Extractor?.GetType().Name, schedulingContext);
            entryCount = 0;
            if (filtered.Length > 0)
            {
                var messages = Array.AsReadOnly(filtered.Select(item => item.Message).ToArray());
                entryCount = await operation.RunActiveAsync(async () =>
                {
                    if (binding.Extractor is { } extractor)
                    {
                        var entries = await extractor.ExtractAsync(messages, new(defaultModel) { Tracer = telemetry.Tracer, TelemetryProfile = telemetry.Profile }, CancellationToken.None).ConfigureAwait(false);
                        if (entries is null || entries.Any(entry => entry is null)) throw new InvalidDataException("An extractor returned null entries.");
                        var failures = await Task.WhenAll(entries.Select(async entry =>
                        {
                            try { await ((IWritableMemoryStore)binding.Store).AddAsync(entry.Content, entry.Metadata, CancellationToken.None).ConfigureAwait(false); return (Exception?)null; }
                            catch (Exception error) { return error; }
                        })).ConfigureAwait(false);
                        if (failures.Any(error => error is not null)) throw new AggregateException("Failed to save extracted memory entries.", failures.Where(error => error is not null).Select(error => error!));
                        return entries.Count;
                    }
                    await ((IMessageMemoryStore)binding.Store).AddMessagesAsync(messages, Array.AsReadOnly(filtered.Select(item => item.Sequence).ToArray()), CancellationToken.None).ConfigureAwait(false);
                    return messages.Count;
                }).ConfigureAwait(false);
                lock (_gate) { binding.Failures = 0; binding.ProbeRequests = 0; }
            }
            lock (_gate) binding.Mark = fresh[fresh.Length - 1].Sequence;
        }
        catch (Exception error)
        {
            saveError = error;
            entryCount = null;
            lock (_gate) binding.Failures++;
            try { SdkLogging.Warning(error, "Memory extraction failed for store '{Store}'; retaining its pending messages: {Error}", binding.Store.Name, error.Message); }
            catch (Exception) { /* Diagnostics cannot replace the saved outcome or prevent pending-message retry. */ }
        }
        finally
        {
            lock (_gate)
            {
                var minimum = bindings.Min(item => item.Mark);
                _pending.RemoveAll(item => item.Sequence <= minimum);
            }
            operation?.CompleteExtraction(entryCount, saveError);
        }
    }

    private static bool IsExcluded(ContentBlock block, HashSet<string> excluded)
    {
        if (excluded.Contains(Kind(block))) return true;
        // Native replay objects retain provider fields, but semantic filters still apply to their underlying content.
        return block switch
        {
            Models.ResponsesReasoningBlock => excluded.Contains("reasoningContent"),
            Models.ResponsesContentBlock => excluded.Contains("text"),
            Models.GoogleContentBlock google when google.Part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True => excluded.Contains("reasoningContent"),
            Models.GoogleContentBlock google when google.Part.TryGetProperty("text", out _) => excluded.Contains("text"),
            Models.AnthropicContentBlock anthropic when anthropic.Content.GetProperty("type").GetString() == "text" => excluded.Contains("text"),
            _ => false
        };
    }

    private static string Kind(ContentBlock block) => block switch
    {
        TextBlock => "text",
        ToolUseBlock => "toolUse",
        ToolResultBlock => "toolResult",
        ReasoningBlock or RedactedReasoningBlock => "reasoningContent",
        CachePointBlock => "cachePoint",
        ImageBlock => "image",
        AudioBlock => "audio",
        DocumentBlock => "document",
        JsonBlock => "json",
        ResourceLinkBlock => "resourceLink",
        Models.GoogleContentBlock => "google",
        Models.AnthropicContentBlock => "anthropic",
        CitationsBlock => "citations",
        Models.ResponsesContentBlock or Models.ResponsesOutputItemBlock or Models.ResponsesReasoningBlock => "openaiResponses",
        _ => block.GetType().Name
    };
}
