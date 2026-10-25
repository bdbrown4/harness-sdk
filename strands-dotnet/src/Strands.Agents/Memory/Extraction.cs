using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Memory;

/// <summary>A fact distilled from a transcript, ready for a writable memory backend.</summary>
public sealed record ExtractionResult
{
    /// <summary>Creates a fact with detached, optional object-valued metadata.</summary>
    public ExtractionResult(string content, JsonElement? metadata = null)
    {
        Guard.NotNull(content, nameof(content));
        if (metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Extraction metadata must be a JSON object.", nameof(metadata));
        Content = content;
        Metadata = metadata?.Clone();
    }
    /// <summary>The extracted fact.</summary>
    public string Content { get; }
    /// <summary>Optional detached backend metadata.</summary>
    public JsonElement? Metadata { get; }
}

/// <summary>The agent's auxiliary model, falling back to its main model.</summary>
/// <param name="DefaultModel">The model to use when an extractor has no explicitly configured model.</param>
public sealed record ExtractionContext(IModel DefaultModel)
{
    /// <summary>An optional borrowed tracer supplied by the memory manager or a standalone caller.</summary>
    public Telemetry.Tracer? Tracer { get; init; }
    internal MemoryTelemetryProfile? TelemetryProfile { get; init; }
}

/// <summary>Distills discrete facts from a detached message batch.</summary>
public interface IExtractor
{
    /// <summary>Extracts facts. Standalone calls may omit context when the extractor has its own model.</summary>
    ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default);
}

/// <summary>Automatic extraction for one writable memory store. Null store configuration disables extraction.</summary>
public record MemoryExtractionOptions
{
    /// <summary>Triggers attached independently for each store and agent. Defaults to every five completed invocation attempts.</summary>
    public IReadOnlyList<ExtractionTrigger> Triggers { get; init; } = [new IntervalTrigger(5)];
    /// <summary>An explicit client-side extractor. Otherwise message-capable stores receive raw batches; entry-only stores use ModelExtractor.</summary>
    public IExtractor? Extractor { get; init; }
    /// <summary>Content kind names removed from extraction batches. Empty includes all; defaults exclude toolUse and toolResult.</summary>
    public IReadOnlyList<string> ExcludeContentBlocks { get; init; } = ["toolUse", "toolResult"];
}

/// <summary>Model and prompt overrides for direct fact extraction.</summary>
public sealed record ModelExtractorOptions
{
    /// <summary>An explicit model, taking precedence over the agent auxiliary model.</summary>
    public IModel? Model { get; init; }
    /// <summary>Optional system instructions replacing the default durable-fact extraction guidance.</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>The extractor tracing policy; null inherits the manager policy or defaults to TypeScript. Python also traces standalone calls and activates the model span during provider work.</summary>
    public MemoryTelemetryProfile? TelemetryProfile { get; init; }
}

/// <summary>Extracts durable facts through one direct auxiliary model call.</summary>
public sealed partial class ModelExtractor(ModelExtractorOptions? options = null) : IExtractor
{
    private readonly ModelExtractorOptions _options = ValidateTelemetryOptions(options ?? new());
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(messages, nameof(messages));
        cancellationToken.ThrowIfCancellationRequested();
        var model = _options.Model ?? context?.DefaultModel ?? throw new InvalidOperationException("No extraction model was configured and no default model is available.");
        if (messages.Count == 0) return [];
        var transcript = string.Join("\n", messages.Select(message => (message.Role == Role.User ? "user" : "assistant") + ": " + string.Join("\n", message.Content.Select(block => block switch
        { TextBlock text => text.Text, CitationsBlock citations => citations.Text, Models.ResponsesContentBlock response => response.Text, Models.GoogleContentBlock google => google.Text, Models.AnthropicContentBlock anthropic => anthropic.Text, _ => "" }).Where(text => text.Length > 0))));
        var request = new ModelRequest([Message.User("Extract facts from the following transcript:\n\n" + transcript)], _options.SystemPrompt ?? DefaultPrompt);
        var accumulator = new ModelStreamAccumulator();
        using var trace = StartModelTrace(model, request, context);
        try
        {
            var attempt = trace.Run(() => new Models.ModelAttempt(model, request, cancellationToken));
            try { while (await trace.RunAsync(attempt.MoveNextAsync).ConfigureAwait(false)) accumulator.Process(attempt.Current); }
            finally { await trace.RunAsync(async () => { await attempt.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
            if (attempt.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            cancellationToken.ThrowIfCancellationRequested();
            var completed = accumulator.Complete();
            var response = completed.Message;
            if (response.Content.OfType<ToolUseBlock>().Any()) throw new ModelException("A memory extractor must return facts without requesting tools.");
            trace.Complete(response, accumulator, completed.StopReason);
            var entries = Parse(response.Text.Trim());
            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }
        catch (Exception error)
        {
            var failure = cancellationToken.IsCancellationRequested && (error is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken)
                ? new OperationCanceledException("Memory model extraction was cancelled.", error, cancellationToken) : error;
            trace.End(error: failure);
            if (!ReferenceEquals(error, failure)) throw failure;
            throw;
        }
    }

    private static IReadOnlyList<ExtractionResult> Parse(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end < start)
        { SdkLogging.Warning("Memory extractor returned no JSON array; skipping output."); return []; }
        try
        {
            using var document = JsonDocument.Parse(output.Substring(start, end - start + 1));
            var results = new List<ExtractionResult>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
                var fact = content.GetString()!.Trim();
                if (fact.Length == 0) continue;
                JsonElement? metadata = item.TryGetProperty("metadata", out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
                results.Add(new(fact, metadata));
            }
            return results;
        }
        catch (JsonException error)
        { SdkLogging.Warning(error, "Memory extractor returned malformed JSON; skipping output: {Error}", error.Message); return []; }
    }

    private const string DefaultPrompt = "You extract durable facts worth remembering across future conversations from a transcript.\n\nReturn ONLY a JSON array of objects, each: {\"content\": string}. Each object is one discrete, self-contained fact (a preference, decision, or stable detail about the user or task). Do not include transient chit-chat, questions, or anything already obvious. If there is nothing worth remembering, return [].";
}
