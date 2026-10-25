using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

internal sealed class ChatCompletionsStreamState(string provider)
{
    private readonly Dictionary<int, ToolState> _tools = [];
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private bool _messageStarted;
    private bool _textStarted;
    private string? _stopReason;

    internal IReadOnlyList<ModelStreamEvent> Process(JsonElement chunk)
    {
        if (chunk.TryGetProperty("error", out _)) throw ProviderHttp.CreateError(provider, chunk);
        var events = new List<ModelStreamEvent>();
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw new ModelException($"{provider} stream event is missing its choices array.");
        foreach (var choice in choices.EnumerateArray())
        {
            if (ProviderHttp.RequiredIndex(choice) != 0) throw new ModelException($"{provider} multiple-choice streams are not supported.");
            if (_stopReason is not null) throw new ModelException($"{provider} sent a choice after the completion finished.");
            if (!_messageStarted) { events.Add(new ModelMessageStartEvent()); _messageStarted = true; }
            ProcessDelta(ProviderHttp.RequiredObject(choice, "delta"), events);
            if (ProviderHttp.OptionalString(choice, "finish_reason") is { } reason)
            {
                _stopReason = MapStopReason(reason);
                FinishBlocks(events);
            }
        }
        if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new ModelException($"{provider} usage must be an object.");
            var cached = usage.TryGetProperty("prompt_tokens_details", out var details) ? ProviderHttp.TokenCount(details, "cached_tokens") : 0;
            long? total = usage.TryGetProperty("total_tokens", out _) ? ProviderHttp.TokenCount(usage, "total_tokens") : null;
            events.Add(new ModelMetadataEvent(new(ProviderHttp.TokenCount(usage, "prompt_tokens"), ProviderHttp.TokenCount(usage, "completion_tokens"), cached, ReportedTotalTokens: total)));
        }
        return events;
    }

    private void ProcessDelta(JsonElement delta, List<ModelStreamEvent> events)
    {
        var role = ProviderHttp.OptionalString(delta, "role");
        if (role is not null and not "assistant") throw new ModelException($"{provider} returned a non-assistant response.");
        foreach (var unsupported in new[] { "function_call", "audio", "reasoning_content", "reasoning" })
            if (delta.TryGetProperty(unsupported, out var value) && value.ValueKind != JsonValueKind.Null)
                throw new ModelException($"{provider} stream contains unsupported '{unsupported}' content.");
        AddText(ProviderHttp.OptionalString(delta, "content"), events);
        if (ProviderHttp.OptionalString(delta, "refusal") is { Length: > 0 } refusal) AddText(refusal, events);
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null) return;
        if (calls.ValueKind != JsonValueKind.Array) throw new ModelException($"{provider} tool_calls must be an array.");
        foreach (var call in calls.EnumerateArray()) ProcessTool(call, events);
    }

    private void AddText(string? text, List<ModelStreamEvent> events)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!_textStarted) { events.Add(new ModelContentBlockStartEvent(0)); _textStarted = true; }
        events.Add(new ModelContentBlockDeltaEvent(0, Text: text));
    }

    private void ProcessTool(JsonElement call, List<ModelStreamEvent> events)
    {
        var wireIndex = ProviderHttp.RequiredIndex(call);
        if (wireIndex == int.MaxValue) throw new ModelException($"{provider} tool index exceeds the supported range.");
        if (!_tools.TryGetValue(wireIndex, out var tool)) { tool = new(wireIndex + 1, provider); _tools.Add(wireIndex, tool); }
        if (ProviderHttp.OptionalString(call, "type") is { } type && type != "function")
            throw new ModelException($"{provider} tool type '{type}' is not supported.");
        tool.SetIdentity(ProviderHttp.OptionalString(call, "id"), null);
        if (!call.TryGetProperty("function", out var function)) return;
        if (function.ValueKind != JsonValueKind.Object) throw new ModelException($"{provider} tool function must be an object.");
        tool.SetIdentity(null, ProviderHttp.OptionalString(function, "name"));
        var arguments = ProviderHttp.OptionalString(function, "arguments");
        if (arguments is not null) tool.Input.Append(arguments);
        if (!tool.Started && tool.Id.Length > 0 && tool.Name.Length > 0 && arguments is not null)
        {
            if (!_toolIds.Add(tool.Id)) throw new ModelException($"{provider} returned duplicate tool call identifiers.");
            tool.Started = true;
            events.Add(new ModelContentBlockStartEvent(tool.Index, new(tool.Id, tool.Name, ProviderHttp.EmptyObject)));
            if (tool.Input.Length > 0) events.Add(new ModelContentBlockDeltaEvent(tool.Index, ToolInput: tool.Input.ToString()));
        }
        else if (tool.Started && arguments is { Length: > 0 }) events.Add(new ModelContentBlockDeltaEvent(tool.Index, ToolInput: arguments));
    }

    private void FinishBlocks(List<ModelStreamEvent> events)
    {
        if (_textStarted) events.Add(new ModelContentBlockStopEvent(0));
        foreach (var tool in _tools.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!tool.Started) throw new ModelException($"{provider} returned an incomplete tool call.");
            ProviderHttp.ValidateToolInput(tool.Input.Length == 0 ? "{}" : tool.Input.ToString(), provider);
            events.Add(new ModelContentBlockStopEvent(tool.Index));
        }
        if (_tools.Count > 0 && _stopReason is not (StopReasons.ToolUse or StopReasons.MaxTokens))
            throw new ModelException($"{provider} returned tool calls with an incompatible finish reason.");
        if (_stopReason == StopReasons.ToolUse && _tools.Count == 0)
            throw new ModelException($"{provider} requested tools without any tool calls.");
    }

    internal ModelMessageStopEvent Complete()
    {
        if (!_messageStarted || _stopReason is null) throw new ModelException($"{provider} stream ended before a completion finish reason.");
        return new(_stopReason);
    }

    private static string MapStopReason(string reason) => reason switch
    {
        "stop" => StopReasons.EndTurn,
        "tool_calls" => StopReasons.ToolUse,
        "length" => StopReasons.MaxTokens,
        "content_filter" => StopReasons.ContentFiltered,
        _ => reason
    };

    private sealed class ToolState(int index, string provider)
    {
        internal int Index { get; } = index;
        internal string Id { get; private set; } = string.Empty;
        internal string Name { get; private set; } = string.Empty;
        internal StringBuilder Input { get; } = new();
        internal bool Started { get; set; }

        internal void SetIdentity(string? id, string? name)
        {
            if (Started && ((id is { Length: > 0 } && id != Id) || (name is { Length: > 0 } && name != Name)))
                throw new ModelException($"{provider} changed a tool identity after its arguments started.");
            if (id is { Length: > 0 }) Id = Started ? Id : Id + id;
            if (name is { Length: > 0 }) Name = Started ? Name : Name + name;
        }
    }
}
