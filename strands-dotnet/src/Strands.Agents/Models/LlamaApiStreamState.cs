using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

internal sealed class LlamaApiStreamState
{
    private readonly Dictionary<string, ToolState> _tools = new(StringComparer.Ordinal);
    private bool _started;
    private string? _stopReason;
    private string? _currentTool;
    private JsonElement? _metrics;
    private string? _responseId;

    internal IReadOnlyList<ModelStreamEvent> Process(JsonElement frame)
    {
        var value = ProviderHttp.RequiredObject(frame, "event");
        var kind = ProviderHttp.RequiredString(value, "event_type");
        var delta = ProviderHttp.RequiredObject(value, "delta");
        var deltaType = ProviderHttp.RequiredString(delta, "type");
        var events = new List<ModelStreamEvent>();
        if (ProviderHttp.OptionalString(frame, "id") is { } id)
        {
            if (_responseId is not null && _responseId != id) throw new ModelException("Llama API changed response identity.");
            _responseId = id;
        }
        if (kind == "start")
        {
            if (_started || _stopReason is not null) throw new ModelException("Llama API repeated message start.");
            _started = true; events.Add(new ModelMessageStartEvent()); events.Add(new ModelContentBlockStartEvent(0));
        }
        else if (!_started) throw new ModelException("Llama API emitted content before start.");
        if (kind is not ("start" or "progress" or "complete" or "metrics")) throw new ModelException("Llama API returned an unknown event type.");
        if (_stopReason is not null && kind != "metrics") throw new ModelException("Llama API emitted content after completion.");
        if (deltaType == "text")
        {
            var text = ProviderHttp.OptionalString(delta, "text") ?? throw new ModelException("Llama API text delta requires text.");
            if (text.Length != 0)
            {
                if (_stopReason is not null) throw new ModelException("Llama API metrics contain post-completion content.");
                events.Add(new ModelContentBlockDeltaEvent(0, Text: text));
            }
        }
        else if (deltaType == "tool_call")
        {
            if (_stopReason is not null) throw new ModelException("Llama API emitted a tool after completion.");
            CollectTool(delta);
        }
        else throw new ModelException("Llama API returned an unsupported delta type.");
        if (value.TryGetProperty("metrics", out var metrics) && metrics.ValueKind != JsonValueKind.Null)
        {
            if (metrics.ValueKind != JsonValueKind.Array) throw new ModelException("Llama API metrics must be an array.");
            _ = UsageFor(metrics); _metrics = metrics.Clone();
        }
        var reason = ProviderHttp.OptionalString(value, "stop_reason");
        if (kind == "complete")
        {
            _stopReason = reason switch { "stop" => StopReasons.EndTurn, "tool_calls" => StopReasons.ToolUse, "length" => StopReasons.MaxTokens, _ => throw new ModelException("Llama API completion requires a known stop reason.") };
            events.Add(new ModelContentBlockStopEvent(0));
        }
        else if (reason is not null) throw new ModelException("Llama API stop reason must belong to completion.");
        return events;
    }

    private void CollectTool(JsonElement delta)
    {
        var id = ProviderHttp.OptionalString(delta, "id");
        if (id is not null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ModelException("Llama API tool identifier cannot be blank.");
            _currentTool = id;
        }
        if (_currentTool is null) throw new ModelException("Llama API tool delta has no correlated identifier.");
        if (!_tools.TryGetValue(_currentTool, out var tool)) { tool = new(_tools.Count + 1); _tools.Add(_currentTool, tool); }
        var function = ProviderHttp.RequiredObject(delta, "function");
        if (ProviderHttp.OptionalString(function, "name") is { } name)
        {
            if (string.IsNullOrWhiteSpace(name) || tool.Name is not null && tool.Name != name) throw new ModelException("Llama API tool name is missing or changed.");
            tool.Name = name;
        }
        if (ProviderHttp.OptionalString(function, "arguments") is { } arguments) tool.Arguments.Append(arguments);
    }

    internal IReadOnlyList<ModelStreamEvent> Complete()
    {
        if (!_started || _stopReason is null) throw new ModelException("Llama API stream ended before a complete response.");
        if (_tools.Count > 0 && _stopReason is not (StopReasons.ToolUse or StopReasons.MaxTokens) || _tools.Count == 0 && _stopReason == StopReasons.ToolUse)
            throw new ModelException("Llama API stop reason conflicts with its tool calls.");
        var events = new List<ModelStreamEvent>();
        foreach (var pair in _tools.OrderBy(pair => pair.Value.Index))
        {
            if (pair.Value.Name is null) throw new ModelException("Llama API tool call has no name.");
            var input = pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString(); ProviderHttp.ValidateToolInput(input, "Llama API");
            events.Add(new ModelContentBlockStartEvent(pair.Value.Index, new(pair.Key, pair.Value.Name, ProviderHttp.EmptyObject)));
            events.Add(new ModelContentBlockDeltaEvent(pair.Value.Index, ToolInput: input)); events.Add(new ModelContentBlockStopEvent(pair.Value.Index));
        }
        if (_metrics is { } metrics && UsageFor(metrics) is { } usage) events.Add(new ModelMetadataEvent(usage) { UsageReported = true });
        events.Add(new ModelMessageStopEvent(_stopReason) { Metadata = JsonSerializer.SerializeToElement(new { llamaApi = new { id = _responseId, metrics = _metrics } }) });
        return events;
    }

    private static Usage? UsageFor(JsonElement metrics)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var metric in metrics.EnumerateArray())
        {
            var name = ProviderHttp.RequiredString(metric, "metric");
            if (!metric.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number) throw new ModelException("Llama API metric values must be numeric.");
            if (name is not ("num_prompt_tokens" or "num_completion_tokens" or "num_total_tokens")) continue;
            if (counts.ContainsKey(name))
                throw new ModelException("Llama API token counts must be unique nonnegative integral Int64 values.");
            counts.Add(name, TokenCount(value));
        }
        if (counts.Count == 0) return null;
        if (counts.Count != 3) throw new ModelException("Llama API usage metrics must include input, output and total tokens.");
        return new(counts["num_prompt_tokens"], counts["num_completion_tokens"], ReportedTotalTokens: counts["num_total_tokens"]);
    }
    private static long TokenCount(JsonElement value)
    {
        // Decimal/double conversion can round a fractional JSON number into an integer. Inspect the exact coefficient and exponent instead.
        var raw = value.GetRawText(); var negative = raw[0] == '-';
        var exponentAt = raw.IndexOfAny(['e', 'E']); var end = exponentAt < 0 ? raw.Length : exponentAt;
        var dot = raw.IndexOf('.'); var fractionLength = dot < 0 ? 0 : end - dot - 1;
        var digits = raw.Substring(negative ? 1 : 0, end - (negative ? 1 : 0)).Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return 0;
        var exponent = 0;
        if (negative || exponentAt >= 0 && !int.TryParse(raw.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) throw InvalidCount();
        var scale = (long)exponent - fractionLength;
        if (scale < 0)
        {
            if (-scale > digits.Length) throw InvalidCount();
            var remaining = digits.Length - (int)-scale;
            for (var index = remaining; index < digits.Length; index++) if (digits[index] != '0') throw InvalidCount();
            digits = digits.Substring(0, remaining);
        }
        else
        {
            if (digits.Length + scale > 19) throw InvalidCount();
            digits += new string('0', (int)scale);
        }
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw InvalidCount();
    }
    private static ModelException InvalidCount() => new("Llama API token counts must be exact nonnegative integral Int64 values.");
    private sealed class ToolState(int index)
    {
        internal int Index { get; } = index;
        internal string? Name;
        internal StringBuilder Arguments { get; } = new();
    }
}
