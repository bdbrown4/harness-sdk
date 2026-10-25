using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

internal sealed class SageMakerChunkState(int maximumBytes)
{
    private readonly ChatCompletionsStreamState _chat = new("SageMaker");
    private readonly ChatCompletionsResponseNormalizer _normalizer = new("SageMaker");
    private readonly Dictionary<int, List<ModelStreamEvent>> _tools = [];
    private int _nextIndex;
    private int? _textIndex;
    private int? _reasoningIndex;
    private bool _finished;
    private Usage _usage = new();
    private long _bufferedToolBytes;

    internal IReadOnlyList<ModelStreamEvent> Process(JsonElement root, bool complete = false)
    {
        var envelope = Object(root);
        if (root.TryGetProperty("error", out _)) throw ProviderHttp.CreateError("SageMaker", root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new ModelException("SageMaker response requires a choices array.");
        if (choices.GetArrayLength() > 1) throw new ModelException("SageMaker supports exactly one response choice.");
        var reasoning = string.Empty;
        if (_finished)
        {
            if (choices.GetArrayLength() == 1) ValidatePostFinish(choices[0], complete);
            envelope["choices"] = Array.Empty<object>();
        }
        else if (choices.GetArrayLength() == 1)
        {
            var choice = Object(choices[0]);
            var content = ProviderHttp.RequiredObject(choices[0], complete ? "message" : "delta");
            var delta = Object(content);
            reasoning = ProviderHttp.OptionalString(content, "reasoning_content") ?? string.Empty;
            if (reasoning.Length == 0) reasoning = ProviderHttp.OptionalString(content, "reasoning") ?? string.Empty;
            delta.Remove("reasoning_content"); delta.Remove("reasoning");
            if (content.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
            {
                var items = calls.ValueKind == JsonValueKind.Object ? new[] { calls } : calls.ValueKind == JsonValueKind.Array ? calls.EnumerateArray().ToArray() : throw new ModelException("SageMaker tool calls must be objects or arrays.");
                delta["tool_calls"] = items.Select(tool =>
                {
                    var item = Object(tool);
                    var function = ProviderHttp.RequiredObject(tool, "function");
                    var formatted = Object(function);
                    if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object) formatted["arguments"] = arguments.GetRawText();
                    item["function"] = formatted;
                    return item;
                }).ToArray();
            }
            choice[complete ? "message" : "delta"] = delta;
            if (ProviderHttp.OptionalString(choices[0], "finish_reason") is { } reason)
            {
                _finished = true;
                if (reason == "stop" && (_tools.Count > 0 || content.TryGetProperty("tool_calls", out var generated) && generated.ValueKind != JsonValueKind.Null)) choice["finish_reason"] = "tool_calls";
            }
            if ((!root.TryGetProperty("usage", out var rootUsage) || rootUsage.ValueKind == JsonValueKind.Null) && choices[0].TryGetProperty("usage", out var localUsage)) envelope["usage"] = localUsage.Clone();
            envelope["choices"] = new[] { choice };
        }
        var normalized = _normalizer.Normalize(JsonSerializer.SerializeToElement(envelope), complete);
        var shared = _chat.Process(normalized);
        var result = new List<ModelStreamEvent>();
        var stops = new List<ModelStreamEvent>();
        var metadata = new List<ModelStreamEvent>();
        foreach (var item in shared)
        {
            var index = Index(item);
            if (index is > 0)
            {
                _bufferedToolBytes = checked(_bufferedToolBytes + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item, item.GetType())));
                if (_bufferedToolBytes > maximumBytes) throw new ModelException("SageMaker's buffered tool data exceeds MaximumResponseBytes.");
                if (!_tools.TryGetValue(index.Value, out var events)) _tools[index.Value] = events = [];
                events.Add(item);
            }
            else if (item is ModelMetadataEvent usage) metadata.Add(new ModelMetadataEvent(UsageDelta(usage.Usage, normalized.GetProperty("usage"))));
            else if (item is ModelContentBlockStopEvent) stops.Add(Remapping(item, _textIndex ?? throw new ModelException("SageMaker closed text before opening it.")));
            else if (index is 0)
            {
                _textIndex ??= _nextIndex++;
                result.Add(Remapping(item, _textIndex.Value));
            }
            else result.Add(item);
        }
        if (reasoning.Length > 0)
        {
            if (_reasoningIndex is null) { _reasoningIndex = _nextIndex++; result.Add(new ModelContentBlockStartEvent(_reasoningIndex.Value)); }
            result.Add(new ModelContentBlockDeltaEvent(_reasoningIndex.Value, Reasoning: reasoning));
        }
        if (_finished && _reasoningIndex is { } reasoningIndex && stops.Count > 0) result.Add(new ModelContentBlockStopEvent(reasoningIndex));
        result.AddRange(stops);
        result.AddRange(metadata);
        return result;
    }

    internal IReadOnlyList<ModelStreamEvent> Complete()
    {
        var stop = _chat.Complete();
        var events = new List<ModelStreamEvent>();
        if (_reasoningIndex is { } reasoning && _textIndex is null) events.Add(new ModelContentBlockStopEvent(reasoning));
        foreach (var tool in _tools.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            var index = _nextIndex++;
            events.AddRange(tool.Select(item => Remapping(item, index)));
        }
        if (_textIndex is null && _tools.Count == 0) { events.Add(new ModelContentBlockStartEvent(_nextIndex)); events.Add(new ModelContentBlockStopEvent(_nextIndex)); }
        events.Add(stop);
        return events;
    }
    private static int? Index(ModelStreamEvent value) => value switch { ModelContentBlockStartEvent start => start.Index, ModelContentBlockDeltaEvent delta => delta.Index, ModelContentBlockStopEvent stop => stop.Index, _ => null };
    private Usage UsageDelta(Usage next, JsonElement raw)
    {
        var input = raw.TryGetProperty("prompt_tokens", out _) ? next.InputTokens : _usage.InputTokens;
        var output = raw.TryGetProperty("completion_tokens", out _) ? next.OutputTokens : _usage.OutputTokens;
        var cached = raw.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("cached_tokens", out _) ? next.CacheReadInputTokens : _usage.CacheReadInputTokens;
        var reported = next.ReportedTotalTokens ?? _usage.ReportedTotalTokens;
        var current = new Usage(input, output, cached, ReportedTotalTokens: reported);
        long total;
        try { total = current.TotalTokens; }
        catch (OverflowException error) { throw new ModelException("SageMaker usage counters exceed the supported range.", error); }
        if (input < _usage.InputTokens || output < _usage.OutputTokens || cached < _usage.CacheReadInputTokens || total < _usage.TotalTokens) throw new ModelException("SageMaker cumulative usage counters moved backwards.");
        var delta = new Usage(input - _usage.InputTokens, output - _usage.OutputTokens, cached - _usage.CacheReadInputTokens, ReportedTotalTokens: reported is null ? null : total - _usage.TotalTokens);
        _usage = current;
        return delta;
    }
    private static void ValidatePostFinish(JsonElement choice, bool complete)
    {
        var delta = ProviderHttp.RequiredObject(choice, complete ? "message" : "delta");
        if (ProviderHttp.OptionalString(delta, "role") is { } role && role != "assistant") throw new ModelException("SageMaker returned a non-assistant response after completion.");
        if (ProviderHttp.OptionalString(choice, "finish_reason") is { } finish && finish != "stop") throw new ModelException("SageMaker changed its finish reason after completion.");
        foreach (var property in delta.EnumerateObject())
        {
            if (property.Name == "role" || property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Name is "content" or "reasoning" or "reasoning_content" or "refusal" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length == 0) continue;
            if (property.Name == "tool_calls" && property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0) continue;
            throw new ModelException("SageMaker sent output after its completion finish reason.");
        }
    }
    private static ModelStreamEvent Remapping(ModelStreamEvent value, int index) => value switch { ModelContentBlockStartEvent start => start with { Index = index }, ModelContentBlockDeltaEvent delta => delta with { Index = index }, ModelContentBlockStopEvent stop => stop with { Index = index }, _ => value };
    private static Dictionary<string, object?> Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ModelException("SageMaker response fields must be JSON objects.");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (result.ContainsKey(property.Name)) throw new ModelException("SageMaker returned duplicate JSON fields.");
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }
}
