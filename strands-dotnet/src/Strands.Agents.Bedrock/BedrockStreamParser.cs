using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Amazon.Runtime.EventStreams.Internal;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Bedrock;

internal sealed class BedrockStreamParser(BedrockModelOptions options) : IDisposable
{
    private readonly Dictionary<int, Block> _blocks = [];
    private bool _started;
    private bool _stopped;
    private bool _metadata;
    private string? _stopReason;
    private bool _sawGuardrailTrace;
    private bool _redactionEmitted;
    private long _buffered;

    internal IReadOnlyList<ModelStreamEvent> Map(IEventStreamEvent item)
    {
        List<ModelStreamEvent> events = [];
        switch (item)
        {
            case InitialResponseEvent:
                break;
            case MessageStartEvent start:
                if (_started || start.Role?.Value != "assistant") throw new ModelException("Bedrock returned an invalid or repeated message role.");
                _started = true;
                events.Add(new ModelMessageStartEvent());
                break;
            case ContentBlockStartEvent start:
                RequireActive();
                var startMembers = (start.Start?.ToolUse is not null ? 1 : 0) + (start.Start?.Image is not null ? 1 : 0) + (start.Start?.ToolResult is not null ? 1 : 0);
                if (startMembers != 1) throw new ModelException("Bedrock content starts must contain exactly one content variant.");
                var index = Index(start.ContentBlockIndex);
                if (_blocks.ContainsKey(index)) throw new ModelException("Bedrock repeated a content block index.");
                var block = Add(index);
                if (start.Start?.ToolUse is { } tool)
                {
                    if (string.IsNullOrWhiteSpace(tool.ToolUseId) || string.IsNullOrWhiteSpace(tool.Name)) throw new ModelException("Bedrock omitted a tool identity.");
                    block.Kind = "tool";
                    block.Started = true;
                    events.Add(new ModelContentBlockStartEvent(index, new Strands.Agents.ToolUseBlock(tool.ToolUseId, tool.Name, JsonSerializer.SerializeToElement(new { }))));
                }
                else if (start.Start?.Image is { } image)
                {
                    block.Kind = "image";
                    block.MediaType = image.Format?.Value switch
                    {
                        "jpeg" => "image/jpeg",
                        "png" => "image/png",
                        "gif" => "image/gif",
                        "webp" => "image/webp",
                        _ => throw new ModelException("Bedrock returned an unsupported image format.")
                    };
                }
                else if (start.Start?.ToolResult is not null) throw new ModelException("Bedrock server-side streamed tool results cannot be represented by this model contract.");
                else throw new ModelException("Bedrock returned an empty content block start.");
                break;
            case ContentBlockDeltaEvent delta:
                RequireActive();
                var deltaIndex = Index(delta.ContentBlockIndex);
                var content = delta.Delta ?? throw new ModelException("Bedrock omitted a content delta.");
                var members = (content.Text is not null ? 1 : 0) + (content.ToolUse is not null ? 1 : 0) + (content.ReasoningContent is not null ? 1 : 0) + (content.Image is not null ? 1 : 0) + (content.Citation is not null ? 1 : 0) + (content.ToolResult is not null ? 1 : 0);
                if (members != 1) throw new ModelException("Bedrock content deltas must contain exactly one content variant.");
                if (content.ToolResult is not null) throw new ModelException("Bedrock streamed server-side tool results require an unsupported content contract.");
                var kind = content.Text is not null || content.Citation is not null ? "text" : content.ToolUse is not null ? "tool" : content.Image is not null ? "image" : content.ReasoningContent!.RedactedContent is not null ? "redacted" : "reasoning";
                if (!_blocks.TryGetValue(deltaIndex, out var current))
                {
                    if (kind is "tool" or "image") throw new ModelException("Bedrock omitted the start of a tool or image block.");
                    current = Add(deltaIndex);
                    current.Kind = kind;
                }
                if (current.Closed || current.Kind != kind) throw new ModelException("Bedrock changed or reopened a content block.");
                if (kind == "redacted")
                {
                    if (content.ReasoningContent!.Text is not null || content.ReasoningContent.Signature is not null) throw new ModelException("Bedrock mixed opaque and signed reasoning.");
                    var part = Append(current, content.ReasoningContent.RedactedContent!);
                    if (!current.Started) { current.Started = true; events.Add(new ModelContentBlockStartEvent(deltaIndex)); }
                    events.Add(new ModelContentBlockDeltaEvent(deltaIndex) { RedactedReasoningBytes = part });
                }
                else if (kind == "image")
                {
                    if (content.Image!.Error is not null || content.Image.Source?.Bytes is not { } imageBytes) throw new ModelException("Bedrock returned an invalid or failed image delta.");
                    Append(current, imageBytes);
                }
                else
                {
                    if (!current.Started) { current.Started = true; events.Add(new ModelContentBlockStartEvent(deltaIndex)); }
                    if (kind == "reasoning" && content.ReasoningContent!.Text is null && content.ReasoningContent.Signature is null) throw new ModelException("Bedrock returned empty reasoning content.");
                    if (content.Citation is { } citation)
                    {
                        if (BedrockCitations.Read(citation) is { } normalized)
                            events.Add(new ModelContentBlockDeltaEvent(deltaIndex) { Citations = [normalized] });
                    }
                    else events.Add(new ModelContentBlockDeltaEvent(deltaIndex, content.Text, content.ToolUse?.Input, content.ReasoningContent?.Text, content.ReasoningContent?.Signature));
                }
                break;
            case ContentBlockStopEvent stop:
                RequireActive();
                var stopIndex = Index(stop.ContentBlockIndex);
                if (!_blocks.TryGetValue(stopIndex, out var ending) || ending.Closed) throw new ModelException("Bedrock stopped an unknown or closed content block.");
                Strands.Agents.ContentBlock? completed = null;
                if (ending.Kind is "redacted" or "image")
                {
                    var data = Convert.ToBase64String(ending.Bytes?.ToArray() ?? []);
                    if (ending.Kind == "redacted") completed = new RedactedReasoningBlock(data);
                    else events.Add(new ModelContentBlockStartEvent(stopIndex) { ContentBlock = new Strands.Agents.ImageBlock(ending.MediaType!, data) });
                    _buffered -= ending.Bytes?.Length ?? 0;
                    ending.Bytes?.Dispose();
                    ending.Bytes = null;
                }
                ending.Closed = true;
                events.Add(new ModelContentBlockStopEvent(stopIndex) { ContentBlock = completed });
                break;
            case MessageStopEvent stop:
                RequireActive();
                if (_blocks.Values.Any(block => !block.Closed) || string.IsNullOrWhiteSpace(stop.StopReason?.Value)) throw new ModelException("Bedrock stopped a message with open blocks or no stop reason.");
                if (_blocks.Values.Any(block => block.Kind == "tool") != (stop.StopReason!.Value == StopReasons.ToolUse)) throw new ModelException("Bedrock tool content and stop reason disagree.");
                _stopped = true;
                _stopReason = stop.StopReason!.Value;
                events.Add(new ModelMessageStopEvent(stop.StopReason!.Value));
                break;
            case ConverseStreamMetadataEvent metadata:
                if (!_stopped || _metadata) throw new ModelException("Bedrock returned misplaced or repeated usage metadata.");
                _metadata = true;
                _sawGuardrailTrace = metadata.Trace?.Guardrail is not null;
                events.AddRange(Redaction(metadata.Trace?.Guardrail));
                var usage = metadata.Usage ?? throw new ModelException("Bedrock omitted token usage from its metadata.");
                if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadInputTokens < 0 || usage.CacheWriteInputTokens < 0 || usage.TotalTokens < 0) throw new ModelException("Bedrock reported negative token usage.");
                ModelPerformanceMetrics? timing = null;
                if (metadata.Metrics?.LatencyMs is { } latencyMilliseconds)
                {
                    if (latencyMilliseconds < 0 || latencyMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
                        throw new ModelException("Bedrock reported invalid streaming latency.");
                    timing = new(TimeSpan.FromTicks(latencyMilliseconds * TimeSpan.TicksPerMillisecond));
                }
                events.Add(new ModelMetadataEvent(new Usage(usage.InputTokens ?? 0, usage.OutputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.CacheWriteInputTokens ?? 0, usage.TotalTokens))
                { PerformanceMetrics = timing });
                break;
            default:
                throw new ModelException("Bedrock returned an unsupported event '" + item.GetType().Name + "'.");
        }
        return events;
    }

    internal IReadOnlyList<ModelStreamEvent> Complete()
    {
        if (!_started || !_stopped) throw new ModelException("Bedrock response ended before messageStop.");
        return _sawGuardrailTrace ? [] : Redaction(null);
    }

    private IReadOnlyList<ModelStreamEvent> Redaction(GuardrailTraceAssessment? trace)
    {
        if (_redactionEmitted || options.Guardrail is not { } guardrail) return [];
        var blocked = trace is null ? _stopReason == "guardrail_intervened" : HasBlockedPolicy(trace);
        if (!blocked || !guardrail.RedactInput && !guardrail.RedactOutput) return [];
        _redactionEmitted = true;
        return [new ModelRedactionEvent(guardrail.RedactInput ? guardrail.InputReplacement : null, guardrail.RedactOutput ? guardrail.OutputReplacement : null)
        { RedactedOutput = guardrail.RedactOutput && trace?.ModelOutput is { } output ? string.Concat(output) : null }];
    }

    private static bool HasBlockedPolicy(GuardrailTraceAssessment trace) =>
        trace.InputAssessment?.Values.Any(HasBlockedAssessment) == true || trace.OutputAssessments?.Values.Any(values => values?.Any(HasBlockedAssessment) == true) == true;

    private static bool HasBlockedAssessment(GuardrailAssessment? assessment) =>
        assessment?.ContentPolicy?.Filters?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.TopicPolicy?.Topics?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.WordPolicy?.CustomWords?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.WordPolicy?.ManagedWordLists?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.SensitiveInformationPolicy?.PiiEntities?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.SensitiveInformationPolicy?.Regexes?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true ||
        assessment?.ContextualGroundingPolicy?.Filters?.Any(value => Blocked(value?.Action?.Value, value?.Detected)) == true;

    private static bool Blocked(string? action, bool? detected) => action == "BLOCKED" && detected == true;
    private void RequireActive() { if (!_started || _stopped) throw new ModelException("Bedrock content arrived outside an active message."); }
    private static int Index(int? index) => index is >= 0 ? index.Value : throw new ModelException("Bedrock omitted or returned a negative content block index.");
    private Block Add(int index)
    {
        if (_blocks.Count >= options.MaximumContentBlocks) throw new ModelException("Bedrock exceeded the content block limit.");
        var block = new Block();
        _blocks.Add(index, block);
        return block;
    }
    private byte[] Append(Block block, MemoryStream bytes)
    {
        if (bytes.Length > options.MaximumBufferedContentBytes - _buffered) throw new ModelException("Bedrock exceeded the buffered binary content limit.");
        block.Bytes ??= new MemoryStream();
        var part = bytes.ToArray();
        block.Bytes.Write(part, 0, part.Length);
        _buffered += part.Length;
        return part;
    }
    public void Dispose() { foreach (var block in _blocks.Values) block.Bytes?.Dispose(); }
    private sealed class Block
    {
        internal string? Kind;
        internal string? MediaType;
        internal bool Started;
        internal bool Closed;
        internal MemoryStream? Bytes;
    }
}
