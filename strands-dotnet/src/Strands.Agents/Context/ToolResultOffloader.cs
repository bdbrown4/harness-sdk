using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Context;

/// <summary>Projects smaller successful tool results into model requests and exposes original history through retrieve_context.</summary>
/// <remarks>This opt-in component retains full durable history; it does not implement automatic summarization or overflow recovery. Results with StructuredContent remain unchanged. Retrieval resolves references only against the invoking agent's history.</remarks>
public sealed class ToolResultOffloader : IPlugin
{
    private readonly ToolResultOffloadOptions _options;
    private readonly IReadOnlyList<ITool> _tools;

    /// <summary>Creates an opt-in offloader. Durable messages and application state remain unchanged.</summary>
    /// <param name="options">Preview, threshold, retrieval, and regex limits; null selects defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A budget is invalid or the threshold cannot exceed the preview.</exception>
    public ToolResultOffloader(ToolResultOffloadOptions? options = null)
    {
        _options = options ?? new();
        if (_options.PreviewTokens < 0 || _options.PreviewTokens > int.MaxValue / 4) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.ThresholdTokens <= _options.PreviewTokens) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaxRetrievalTokens <= 0 || _options.MaxRetrievalTokens > int.MaxValue / 4) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.RegexTimeout <= TimeSpan.Zero || _options.RegexTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options));
        _tools = Array.AsReadOnly<ITool>([new ContextRetrievalTool(_options)]);
    }
    /// <inheritdoc/>
    public string Name => "strands:tool-result-offloader";
    /// <inheritdoc/>
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        agent.Hooks.Register<BeforeModelCallEvent>((value, ct) =>
        {
            value.Request = Project(value.Agent, value.Request, ct);
            return default;
        });
        return default;
    }
    /// <inheritdoc/>
    public IReadOnlyList<ITool> GetTools() => _tools;

    private ModelRequest Project(Agent agent, ModelRequest request, CancellationToken cancellationToken)
    {
        var history = agent.Messages;
        var messages = new Message[request.Messages.Count];
        var changed = false;
        for (var i = 0; i < messages.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = request.Messages[i];
            if (message.Role != Role.User || !message.Content.Any(block => block is ToolResultBlock)) { messages[i] = message; continue; }
            var historyIndex = ContextReference.FindMessage(history, message);
            if (historyIndex < 0) { messages[i] = message; continue; }
            var blocks = message.Content.ToArray();
            var messageChanged = false;
            for (var j = 0; j < blocks.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blocks[j] is not ToolResultBlock result || result.Status != ToolResultStatus.Success || result.StructuredContent is not null) continue;
                if (ContextReference.IsRetrievalResult(history, historyIndex, result.ToolUseId)) continue;
                var replacement = Offload(result, history[historyIndex], historyIndex, j);
                if (replacement is null) continue;
                blocks[j] = replacement;
                messageChanged = true;
            }
            messages[i] = messageChanged ? new(message.Role, blocks, message.TrackingId, message.Metadata) : message;
            changed |= messageChanged;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return changed ? request with { Messages = Array.AsReadOnly(messages) } : request;
    }

    private ToolResultBlock? Offload(ToolResultBlock result, Message message, int messageIndex, int blockIndex)
    {
        var original = result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)];
        var textual = original.OfType<TextBlock>().Select(block => block.Text).ToList();
        var json = original.OfType<JsonBlock>().Select(block => block.Json.GetRawText()).ToList();
        var estimatedTokens = textual.Sum(text => (long)Math.Ceiling(text.Length / 4d)) + json.Sum(text => (long)Math.Ceiling(text.Length / 2d));
        if (estimatedTokens <= _options.ThresholdTokens) return null;
        var fullText = string.Join("\n", original.Select(block => block switch { TextBlock text => text.Text, JsonBlock value => value.Json.GetRawText(), _ => null }).Where(text => text is not null));
        var previewChars = _options.PreviewTokens * 4;
        if (fullText.Length <= previewChars) return null;
        var headChars = (int)Math.Floor(previewChars * 0.6);
        var tailChars = previewChars - headChars;
        var parts = new[] { fullText.Substring(0, headChars), $"[... {(fullText.Length - previewChars).ToString("N0", CultureInfo.InvariantCulture)} chars elided ...]", tailChars == 0 ? "" : fullText.Substring(fullText.Length - tailChars) };
        var refs = Enumerable.Range(0, original.Count).Select(index => ContextReference.Create(message, messageIndex, blockIndex, index));
        var preview = $"[Truncated: {textual.Count + json.Count} blocks, ~{Math.Ceiling(fullText.Length / 4d).ToString("N0", CultureInfo.InvariantCulture)} tokens]\n\n" + string.Join("\n\n", parts.Where(part => part.Length > 0)) + "\n\n[Stashed] " + string.Join(" ", refs.Select(reference => "[" + reference + "]"));
        // Native providers prefer ContentBlocks over Content; snapshots duplicate display text.
        var originalDisplay = string.Concat(original.Select(block => block switch { TextBlock text => text.Text, JsonBlock value => value.Json.GetRawText(), _ => null }));
        var previewSize = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(preview));
        if (previewSize >= Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(originalDisplay))) return null;
        // JSON-capable providers send typed JSON directly. Escaping its raw text would
        // overestimate the original and could approve a larger text replacement.
        // Omit original block wrappers and use minimal string escaping/numeric spelling
        // so this bound remains conservative across native and text-only providers.
        var originalNativeSize = original.Sum(block => block switch
        {
            TextBlock text => MinimumJsonStringSize(text.Text) - 2,
            JsonBlock value => MinimumJsonSize(value.Json),
            _ => 0L
        });
        if (previewSize >= originalNativeSize) return null;
        var content = new ContentBlock[] { new TextBlock(preview) }.Concat(original.Where(block => block is not (TextBlock or JsonBlock))).ToArray();
        return new ToolResultBlock(result.ToolUseId, content, result.Status);
    }

    private static long MinimumJsonStringSize(string text)
    {
        long size = Encoding.UTF8.GetByteCount(text) + 2L;
        foreach (var character in text)
        {
            if (character is '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t') size++;
            else if (character < ' ') size += 5;
        }
        return size;
    }

    private static long MinimumJsonSize(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                long objectSize = 2;
                // Native object maps commonly retain the final duplicate property.
                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    properties[property.Name] = property.Value;
                var propertyCount = 0;
                foreach (var property in properties)
                {
                    objectSize += MinimumJsonStringSize(property.Key) + 1 + MinimumJsonSize(property.Value);
                    if (propertyCount++ > 0) objectSize++;
                }
                return objectSize;
            case JsonValueKind.Array:
                long arraySize = 2;
                var elements = 0;
                foreach (var element in value.EnumerateArray())
                {
                    arraySize += MinimumJsonSize(element);
                    if (elements++ > 0) arraySize++;
                }
                return arraySize;
            case JsonValueKind.String: return MinimumJsonStringSize(value.GetString()!);
            case JsonValueKind.True: return 4;
            case JsonValueKind.False: return 5;
            case JsonValueKind.Null: return 4;
            // Native providers may normalize exponent, decimal, and integer spellings.
            case JsonValueKind.Number: return 1;
            default: return 0;
        }
    }
}
