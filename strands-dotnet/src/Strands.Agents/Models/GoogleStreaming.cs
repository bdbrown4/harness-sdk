using System.Text.Json;

namespace Strands.Agents.Models;

internal sealed class GoogleStreaming
{
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _responseMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _candidateMetadata = new(StringComparer.Ordinal);
    private readonly List<object> _metadataChunks = [];
    private bool _started;
    private bool _hasTools;
    private bool _promptBlocked;
    private string? _finishReason;
    private string? _responseId;
    private Usage? _usage;
    private int _index;

    internal IReadOnlyList<ModelStreamEvent> Process(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ModelException("Google stream data must be an object.");
        if (root.TryGetProperty("error", out _)) throw GoogleModel.CreateError(root);
        if (ProviderHttp.OptionalString(root, "responseId") is { } responseId)
        {
            if (_responseId is not null && _responseId != responseId) throw new ModelException("Google response identity changed across chunks.");
            _responseId = responseId;
        }
        var events = new List<ModelStreamEvent>();
        if (!_started) { events.Add(new ModelMessageStartEvent()); _started = true; }
        var responseMetadata = root.EnumerateObject().Where(item => item.Name != "candidates").ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        foreach (var property in responseMetadata) _responseMetadata[property.Key] = property.Value;
        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind != JsonValueKind.Null) _usage = UsageFrom(usage);
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind == JsonValueKind.Null) { Blocked(root); RecordMetadata(responseMetadata, []); return events; }
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 1) throw new ModelException("GoogleModel requires a single candidate.");
        if (candidates.GetArrayLength() == 0) { Blocked(root); RecordMetadata(responseMetadata, []); return events; }
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object) throw new ModelException("Google candidate must be an object.");
        if (candidate.TryGetProperty("index", out var candidateIndex) && (candidateIndex.ValueKind != JsonValueKind.Number || !candidateIndex.TryGetInt32(out var number) || number != 0)) throw new ModelException("Google candidate index must be zero.");
        var candidateMetadata = candidate.EnumerateObject().Where(item => item.Name != "content").ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        foreach (var property in candidateMetadata) _candidateMetadata[property.Key] = property.Value;
        RecordMetadata(responseMetadata, candidateMetadata);
        if (candidate.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind != JsonValueKind.Object) throw new ModelException("Google candidate content must be an object.");
            var role = ProviderHttp.OptionalString(content, "role");
            if (role is not null and not "model") throw new ModelException("Google output content must have model role.");
            if (content.TryGetProperty("parts", out var parts))
            {
                if (parts.ValueKind != JsonValueKind.Array) throw new ModelException("Google output parts must be an array.");
                if (_finishReason is not null && parts.GetArrayLength() > 0) throw new ModelException("Google emitted content after its finish reason.");
                foreach (var part in parts.EnumerateArray()) Append(part, events);
            }
        }
        var reason = ProviderHttp.OptionalString(candidate, "finishReason");
        if (reason is not null and not "" and not "FINISH_REASON_UNSPECIFIED")
        {
            if (_finishReason is not null && _finishReason != reason) throw new ModelException("Google finish reason changed.");
            _finishReason = reason;
        }
        return events;
    }

    internal IReadOnlyList<ModelStreamEvent> Complete()
    {
        if (!_started || _finishReason is null) throw new ModelException("Google stream ended without a finish reason.");
        var stop = _promptBlocked ? StopReasons.ContentFiltered : _finishReason switch
        {
            "MAX_TOKENS" => StopReasons.MaxTokens,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" => StopReasons.ContentFiltered,
            "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "MALFORMED_RESPONSE" => throw new ModelException("Google rejected the generated function call or response: " + _finishReason),
            "CONTINUATION" => "continuation",
            _ => _hasTools ? StopReasons.ToolUse : StopReasons.EndTurn
        };
        var events = new List<ModelStreamEvent>();
        if (_usage is not null) events.Add(new ModelMetadataEvent(_usage));
        var metadata = JsonSerializer.SerializeToElement(new { google = new { response = _responseMetadata, candidate = _candidateMetadata, chunks = _metadataChunks } });
        events.Add(new ModelMessageStopEvent(stop) { Metadata = metadata });
        return events;
    }

    private void Blocked(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) && ProviderHttp.OptionalString(feedback, "blockReason") is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED")
        {
            if (_finishReason is not null && _finishReason != reason) throw new ModelException("Google prompt block reason changed.");
            _finishReason = reason;
            _promptBlocked = true;
        }
    }

    private void RecordMetadata(Dictionary<string, JsonElement> response, Dictionary<string, JsonElement> candidate)
    {
        if (response.Count > 0 || candidate.Count > 0) _metadataChunks.Add(new { response, candidate });
    }

    private void Append(JsonElement part, List<ModelStreamEvent> events)
    {
        if (part.ValueKind != JsonValueKind.Object) throw new ModelException("Google output part must be an object.");
        string[] kinds = ["text", "inlineData", "functionCall", "functionResponse", "fileData", "executableCode", "codeExecutionResult", "toolCall", "toolResponse"];
        if (kinds.Count(kind => part.TryGetProperty(kind, out _)) > 1) throw new ModelException("A Google part cannot contain multiple payload types.");
        var index = _index;
        try { _index = checked(_index + 1); }
        catch (OverflowException error) { throw new ModelException("Google content indices exceeded the native index range.", error); }
        if (part.TryGetProperty("functionCall", out var function))
        {
            if (function.ValueKind != JsonValueKind.Object) throw new ModelException("Google functionCall must be an object.");
            if (function.TryGetProperty("partialArgs", out var partial) && partial.ValueKind != JsonValueKind.Null || function.TryGetProperty("willContinue", out var continued) && continued.ValueKind != JsonValueKind.False) throw new NotSupportedException("Partial Google function calls are not supported.");
            var name = ProviderHttp.RequiredString(function, "name");
            var suppliedId = ProviderHttp.OptionalString(function, "id");
            var id = string.IsNullOrEmpty(suppliedId) ? "tooluse_" + Guid.NewGuid().ToString("D") : suppliedId!;
            if (!_toolIds.Add(id)) throw new ModelException("Google function-call identifiers must be unique within a response.");
            var input = function.TryGetProperty("args", out var arguments) && arguments.ValueKind != JsonValueKind.Null ? arguments : ProviderHttp.EmptyObject;
            if (input.ValueKind != JsonValueKind.Object) throw new ModelException("Google function arguments must be an object.");
            var metadata = JsonSerializer.SerializeToElement(new { google = part, googleToolUseId = id });
            events.Add(new ModelContentBlockStartEvent(index, new(id, name, ProviderHttp.EmptyObject) { ProviderMetadata = metadata }));
            events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: input.GetRawText()));
            events.Add(new ModelContentBlockStopEvent(index)); _hasTools = true;
            return;
        }
        GoogleContentBlock block;
        try { block = new(part); }
        catch (ArgumentException error) { throw new ModelException("Google emitted an invalid content part.", error); }
        if (part.TryGetProperty("text", out var text))
        {
            var thought = part.TryGetProperty("thought", out var value) && value.ValueKind == JsonValueKind.True;
            events.Add(new ModelContentBlockStartEvent(index) { ContentBlock = thought ? new ReasoningBlock("") : new TextBlock("") });
            events.Add(thought ? new ModelContentBlockDeltaEvent(index, Reasoning: text.GetString()) : new ModelContentBlockDeltaEvent(index, Text: text.GetString()));
            events.Add(new ModelContentBlockStopEvent(index) { ContentBlock = block });
        }
        else
        {
            events.Add(new ModelContentBlockStartEvent(index) { ContentBlock = block });
            events.Add(new ModelContentBlockStopEvent(index));
        }
    }

    private static Usage UsageFrom(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ModelException("Google usageMetadata must be an object.");
        try
        {
            var input = checked(ProviderHttp.TokenCount(value, "promptTokenCount") + ProviderHttp.TokenCount(value, "toolUsePromptTokenCount"));
            var total = ProviderHttp.TokenCount(value, "totalTokenCount");
            var thoughts = ProviderHttp.TokenCount(value, "thoughtsTokenCount");
            var output = value.TryGetProperty("candidatesTokenCount", out var candidates) && candidates.ValueKind != JsonValueKind.Null
                ? checked(ProviderHttp.TokenCount(value, "candidatesTokenCount") + thoughts)
                : Math.Max(0, total - input);
            if (!value.TryGetProperty("totalTokenCount", out var reported) || reported.ValueKind == JsonValueKind.Null) total = checked(input + output);
            return new(input, output, ProviderHttp.TokenCount(value, "cachedContentTokenCount"), ReportedTotalTokens: total);
        }
        catch (OverflowException error) { throw new ModelException("Google token usage exceeded the native count range.", error); }
    }
}
