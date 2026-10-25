using System.Text;
using System.Text.Json;

namespace Strands.Agents;

internal sealed class ModelStreamAccumulator
{
    private readonly SortedDictionary<int, BlockBuffer> _blocks = [];
    private bool _started;
    private string? _stopReason;
    private JsonElement? _metadata;
    private string? _outputRedaction;
    public Usage Usage { get; private set; } = new();
    public bool UsageReported { get; private set; }
    public Telemetry.ModelPerformanceMetrics? PerformanceMetrics { get; private set; }
    public IReadOnlyDictionary<string, JsonElement>? ModelState { get; private set; }
    public string? InputRedaction { get; private set; }

    public void Process(ModelStreamEvent item)
    {
        if (item is ModelMetadataEvent metadata)
        {
            if (metadata.Usage is null) throw new ModelException("Model usage cannot be null.");
            if (metadata.PerformanceMetrics is { } timing && (timing.Latency < TimeSpan.Zero || timing.TimeToFirstToken < TimeSpan.Zero))
                throw new ModelException("Model performance timings cannot be negative.");
            try
            {
                if (metadata.Usage.InputTokens < 0 || metadata.Usage.OutputTokens < 0 || metadata.Usage.CacheReadInputTokens < 0 || metadata.Usage.CacheWriteInputTokens < 0 || metadata.Usage.TotalTokens < 0)
                    throw new ModelException("Model usage counts cannot be negative.");
                if (!metadata.UsageReported && (metadata.Usage.InputTokens != 0 || metadata.Usage.OutputTokens != 0 ||
                    metadata.Usage.CacheReadInputTokens != 0 || metadata.Usage.CacheWriteInputTokens != 0 || metadata.Usage.TotalTokens != 0))
                    throw new ModelException("Unreported model usage must use a zero placeholder.");
                var combined = metadata.UsageReported ? Usage + metadata.Usage : Usage;
                _ = combined.TotalTokens;
                Usage = combined;
                UsageReported |= metadata.UsageReported;
                if (metadata.PerformanceMetrics is { } reported) PerformanceMetrics = reported;
            }
            catch (OverflowException error) { throw new ModelException("Model usage exceeds the supported token-count range.", error); }
            return;
        }
        if (item is ModelRedactionEvent redaction)
        {
            if (!_started || redaction.InputReplacement is null && redaction.OutputReplacement is null)
                throw new ModelException("Model redaction must follow message start and provide an input or output replacement.");
            if (redaction.InputReplacement is not null && InputRedaction is not null || redaction.OutputReplacement is not null && _outputRedaction is not null)
                throw new ModelException("A model turn cannot repeat input or output redaction.");
            InputRedaction ??= redaction.InputReplacement;
            _outputRedaction ??= redaction.OutputReplacement;
            return;
        }
        if (_stopReason is not null) throw new ModelException("The model emitted content after message stop.");
        if (item is ModelMessageStartEvent start)
        {
            if (_started || start.Role != Role.Assistant) throw new ModelException("A model turn must start exactly one assistant message.");
            _started = true;
            return;
        }
        if (!_started) throw new ModelException("The model emitted content before message start.");
        switch (item)
        {
            case ModelContentBlockStartEvent blockStart:
                var toolUse = blockStart.ToolUse is { } tool ? (ToolUseBlock)Message.CloneBlock(tool) : null;
                var content = blockStart.ContentBlock is { } initial ? Message.CloneBlock(initial) : null;
                if (content is ToolResultBlock || toolUse is not null && content is not null)
                    throw new ModelException("An assistant content start must contain exactly one block representation.");
                if (blockStart.Index < 0 || _blocks.ContainsKey(blockStart.Index))
                    throw new ModelException("A model content block index must be nonnegative and unique.");
                _blocks.Add(blockStart.Index, new(toolUse, content));
                break;
            case ModelContentBlockDeltaEvent delta:
                GetOpenBlock(delta.Index).Append(delta);
                break;
            case ModelContentBlockStopEvent stop:
                GetOpenBlock(stop.Index).Close(stop.ContentBlock);
                break;
            case ModelMessageStopEvent stop:
                if (string.IsNullOrWhiteSpace(stop.StopReason) || _blocks.Values.Any(block => !block.Closed))
                    throw new ModelException("A model message must close every content block and provide a stop reason.");
                _stopReason = stop.StopReason;
                if (stop.Metadata is { ValueKind: not JsonValueKind.Object }) throw new ModelException("Model response metadata must be a JSON object.");
                _metadata = stop.Metadata?.Clone();
                ModelState = stop.ModelState is { } state ? ModelStateValues.Clone(state) : null;
                break;
            default:
                throw new ModelException($"Unknown model stream event: {item.GetType().Name}.");
        }
    }

    public ContentBlock GetCompletedBlock(int index)
    {
        if (!_blocks.TryGetValue(index, out var block) || !block.Closed) throw new ModelException("The requested model content block is not complete.");
        return Message.CloneBlock(block.Build());
    }

    public (Message Message, string StopReason) Complete()
    {
        if (_stopReason is null) throw new ModelException("The model stream ended without a message stop event.");
        var message = new Message(Role.Assistant, _blocks.Values.Select(block => block.Build()).ToArray(), metadata: _metadata);
        var tools = message.Content.OfType<ToolUseBlock>().ToArray();
        if (tools.Length == 0 && _stopReason == StopReasons.ToolUse || tools.Length > 0 && _stopReason is not (StopReasons.ToolUse or StopReasons.MaxTokens))
            throw new ModelException("Tool-use content and the model stop reason disagree.");
        if (tools.Select(tool => tool.ToolUseId).Distinct(StringComparer.Ordinal).Count() != tools.Length)
            throw new ModelException("Tool-use identifiers must be unique within a model turn.");
        if (_outputRedaction is { } replacement)
        {
            if (tools.Length > 0) throw new ModelException("Output redaction cannot erase emitted tool-call correlation.");
            message = new(Role.Assistant, [new TextBlock(replacement)], message.TrackingId, _metadata);
        }
        return (message, _stopReason);
    }

    private BlockBuffer GetOpenBlock(int index)
    {
        if (!_blocks.TryGetValue(index, out var block) || block.Closed)
            throw new ModelException("A model content block must be started and remain open before it is updated.");
        return block;
    }

    private sealed class BlockBuffer
    {
        private readonly ToolUseBlock? _toolUse;
        private ContentBlock? _fixedContent;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _json = new();
        private readonly StringBuilder _reasoning = new();
        private readonly StringBuilder _signature = new();
        private readonly List<Citation> _citations = [];
        private readonly List<byte> _redacted = [];
        private bool _hasRedacted;
        private bool _hasText;
        private bool _hasReasoning;
        public BlockBuffer(ToolUseBlock? toolUse, ContentBlock? content)
        {
            _toolUse = toolUse ?? content as ToolUseBlock;
            switch (content)
            {
                case TextBlock text: _text.Append(text.Text); _hasText = true; break;
                case ReasoningBlock reasoning:
                    _reasoning.Append(reasoning.Text); _signature.Append(reasoning.Signature); _hasReasoning = true;
                    break;
                case ToolUseBlock: break;
                default: _fixedContent = content; break;
            }
        }
        public bool Closed { get; private set; }
        public void Close(ContentBlock? final)
        {
            if (final is not null)
            {
                var built = Build();
                var compatible = (built, final) switch
                {
                    (TextBlock text, Models.ResponsesContentBlock response) => text.Text == response.Text,
                    (ReasoningBlock reasoning, Models.ResponsesReasoningBlock response) => reasoning.Text == response.Text && reasoning.Signature is null,
                    (TextBlock text, Models.GoogleContentBlock google) => text.Text == google.Text && google.Reasoning.Length == 0,
                    (TextBlock text, Models.AnthropicContentBlock anthropic) => anthropic.Content.GetProperty("type").GetString() == "text" && text.Text == anthropic.Text,
                    (TextBlock text, CitationsBlock citations) => text.Text == citations.Text,
                    (CitationsBlock original, CitationsBlock replacement) => original.Text == replacement.Text && JsonSerializer.Serialize(original.Citations) == JsonSerializer.Serialize(replacement.Citations),
                    (ReasoningBlock reasoning, Models.GoogleContentBlock google) => reasoning.Text == google.Reasoning && google.Text.Length == 0 && reasoning.Signature is null,
                    (ToolUseBlock original, ToolUseBlock replacement) => original.ToolUseId == replacement.ToolUseId && original.Name == replacement.Name && JsonElement.DeepEquals(original.Input, replacement.Input),
                    _ => JsonSerializer.Serialize<ContentBlock>(built) == JsonSerializer.Serialize<ContentBlock>(final)
                };
                if (!compatible) throw new ModelException("Completed model content cannot rewrite emitted content or tool correlation.");
                _fixedContent = Message.CloneBlock(final);
            }
            Closed = true;
        }

        public void Append(ModelContentBlockDeltaEvent delta)
        {
            var redacted = delta.RedactedReasoningBytes;
            var hasOtherContent = delta.Text is not null || delta.ToolInput is not null || delta.Reasoning is not null || delta.Signature is not null || delta.Citations.Count > 0;
            if (redacted is not null)
            {
                if (_fixedContent is not null || _toolUse is not null || _hasText || _hasReasoning || hasOtherContent)
                    throw new ModelException("Opaque reasoning cannot share a model content block with other content.");
                _hasRedacted = true;
                _redacted.AddRange(redacted);
                return;
            }
            if (_hasRedacted && hasOtherContent)
                throw new ModelException("Opaque reasoning cannot share a model content block with other content.");
            if (_fixedContent is not null || _toolUse is not null && (delta.Text is not null || delta.Reasoning is not null || delta.Signature is not null || delta.Citations.Count > 0) ||
                _toolUse is null && delta.ToolInput is not null || delta.Citations.Count > 0 && (_hasReasoning || delta.Reasoning is not null || delta.Signature is not null))
                throw new ModelException("A model delta does not match its content block type.");
            _hasText |= delta.Text is not null || delta.Citations.Count > 0;
            _hasReasoning |= delta.Reasoning is not null || delta.Signature is not null;
            if (_hasText && _hasReasoning) throw new ModelException("Text and reasoning cannot share one content block.");
            _text.Append(delta.Text);
            _json.Append(delta.ToolInput);
            _reasoning.Append(delta.Reasoning);
            _signature.Append(delta.Signature);
            _citations.AddRange(delta.Citations);
        }

        public ContentBlock Build()
        {
            if (_fixedContent is not null) return _fixedContent;
            if (_hasRedacted) return new RedactedReasoningBlock(Convert.ToBase64String(_redacted.ToArray()));
            if (_citations.Count > 0) return new CitationsBlock(_citations, _text.Length == 0 ? [] : [new(_text.ToString())]);
            if (_toolUse is null)
                return _hasReasoning ? new ReasoningBlock(_reasoning.ToString(), _signature.Length == 0 ? null : _signature.ToString()) : new TextBlock(_text.ToString());
            if (string.IsNullOrWhiteSpace(_toolUse.ToolUseId) || string.IsNullOrWhiteSpace(_toolUse.Name))
                throw new ModelException("Tool requests require a nonempty identifier and name.");
            try
            {
                using var document = _json.Length == 0 ? null : JsonDocument.Parse(_json.ToString());
                var input = document is null ? _toolUse.Input.Clone() : document.RootElement.Clone();
                if (input.ValueKind != JsonValueKind.Object) throw new ModelException("Tool input must be a JSON object.");
                return _toolUse with { Input = input };
            }
            catch (JsonException error)
            {
                throw new ModelException("The model emitted invalid tool-input JSON.", error);
            }
        }
    }
}
