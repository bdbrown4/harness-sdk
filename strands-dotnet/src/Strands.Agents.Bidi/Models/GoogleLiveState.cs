using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

internal sealed class GoogleLiveState
{
    internal Dictionary<string, string> ToolNames { get; } = new(StringComparer.Ordinal);
    internal string? ResumeHandle { get; private set; }
    private string? _responseId, _outputTranscriptId, _textId, _audioId, _inputId, _lastActivityId;
    private bool _reasoning, _interrupted;
    private readonly HashSet<string> _inputIds = new(StringComparer.Ordinal);
    private List<string> _responseInputs = [];
    internal GoogleLiveState Copy()
    {
        var copy = new GoogleLiveState
        {
            ResumeHandle = ResumeHandle,
            _responseId = _responseId,
            _outputTranscriptId = _outputTranscriptId,
            _textId = _textId,
            _audioId = _audioId,
            _inputId = _inputId,
            _lastActivityId = _lastActivityId,
            _reasoning = _reasoning,
            _interrupted = _interrupted,
            _responseInputs = new(_responseInputs)
        };
        copy._inputIds.UnionWith(_inputIds);
        foreach (var pair in ToolNames) copy.ToolNames.Add(pair.Key, pair.Value);
        return copy;
    }
    internal IReadOnlyList<IBidiOutputEvent> Process(JsonElement native)
    {
        if (native.TryGetProperty("error", out var error)) throw new ModelException("Gemini Live returned an error: " + error.GetRawText());
        if (native.TryGetProperty("goAway", out var goAway)) throw new BidiConnectionTimeoutException("Gemini Live announced GoAway: " + goAway.GetRawText());
        var events = new List<IBidiOutputEvent>();
        var opaque = native.EnumerateObject().Any(field => field.Name is not ("serverContent" or "toolCall" or "toolCallCancellation" or "usageMetadata" or "voiceActivity" or "sessionResumptionUpdate" or "setupComplete"));
        if (native.TryGetProperty("sessionResumptionUpdate", out var resumption))
        {
            RequireObject(resumption); if (Flag(resumption, "resumable") && Text(resumption, "newHandle") is { Length: > 0 } handle) ResumeHandle = handle;
        }
        if (native.TryGetProperty("voiceActivity", out var activity))
        {
            RequireObject(activity);
            if (Text(activity, "voiceActivityType") == "ACTIVITY_START") { if (_inputId is null || _inputId == _lastActivityId) StartInput(events); _lastActivityId = _inputId; }
        }
        var content = native.TryGetProperty("serverContent", out var server) ? server : default;
        var interrupted = false; var complete = false; var generated = false;
        if (content.ValueKind != JsonValueKind.Undefined)
        {
            RequireObject(content); interrupted = Flag(content, "interrupted"); complete = Flag(content, "turnComplete"); generated = Flag(content, "generationComplete");
            opaque |= content.EnumerateObject().Any(field => field.Name is not ("interrupted" or "turnComplete" or "generationComplete" or "inputTranscription" or "outputTranscription" or "modelTurn"));
            if (interrupted) events.Add(new BidiBargeInEvent());
            if (content.TryGetProperty("inputTranscription", out var input))
            {
                RequireObject(input);
                if (Text(input, "text") is { Length: > 0 } text)
                {
                    if (_inputId is null) { StartInput(events); if (_responseId is not null && !_interrupted && !interrupted) _responseInputs.Add(_inputId!); }
                    events.Add(new BidiTranscriptDeltaEvent(text, Role.User, _inputId!));
                }
                if (Flag(input, "finished") && _inputId is { } finished) StopInput(events, finished);
            }
            if (content.TryGetProperty("outputTranscription", out var output))
            {
                RequireObject(output);
                if (Text(output, "text") is { Length: > 0 } text)
                {
                    if (_outputTranscriptId is null) { _outputTranscriptId = Id(); events.Add(new BidiTranscriptStartEvent(Role.Assistant, _outputTranscriptId)); }
                    events.Add(new BidiTranscriptDeltaEvent(text, Role.Assistant, _outputTranscriptId));
                }
            }
            if (content.TryGetProperty("modelTurn", out var turn))
            {
                RequireObject(turn);
                if (turn.TryGetProperty("parts", out var parts))
                {
                    RequireArray(parts);
                    foreach (var part in parts.EnumerateArray())
                    {
                        RequireObject(part);
                        opaque |= part.EnumerateObject().Any(field => field.Name is not ("text" or "thought" or "inlineData"));
                        if (Text(part, "text") is { Length: > 0 } text)
                        {
                            var reasoning = Flag(part, "thought");
                            if (_textId is null || _reasoning != reasoning) { StopText(events); _reasoning = reasoning; _textId = Id(); events.Add(reasoning ? new BidiReasoningStartEvent(_textId) : new BidiTextStartEvent(_textId)); }
                            events.Add(reasoning ? new BidiReasoningDeltaEvent(text, _textId) : new BidiTextDeltaEvent(text, _textId));
                        }
                        if (part.TryGetProperty("inlineData", out var audio))
                        {
                            RequireObject(audio); var mime = Text(audio, "mimeType"); var data = Text(audio, "data");
                            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mime, out var format) || !string.Equals(format.MediaType, "audio/pcm", StringComparison.OrdinalIgnoreCase) || data is null || format.Parameters.Any(parameter => parameter.Name.Equals("rate", StringComparison.OrdinalIgnoreCase) && parameter.Value?.Trim('"') != "24000" || parameter.Name.Equals("channels", StringComparison.OrdinalIgnoreCase) && parameter.Value?.Trim('"') != "1")) throw new ModelException("Gemini Live output requires mono PCM audio at 24000Hz.");
                            try { _ = Convert.FromBase64String(data); } catch (FormatException invalid) { throw new ModelException("Invalid Gemini Live audio base64.", invalid); }
                            if (_audioId is null) { _audioId = Id(); events.Add(new BidiAudioStartEvent(_audioId)); }
                            events.Add(new BidiAudioDeltaEvent(data, _audioId, 24000, 1, "pcm"));
                        }
                    }
                }
            }
        }
        if (native.TryGetProperty("toolCall", out var tools))
        {
            RequireObject(tools);
            if (!tools.TryGetProperty("functionCalls", out var calls)) throw new ModelException("Gemini Live toolCall requires functionCalls.");
            RequireArray(calls); var uses = new List<ToolUseBlock>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls.EnumerateArray())
            {
                RequireObject(call); var id = Text(call, "id"); var name = Text(call, "name");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !seen.Add(id!) || ToolNames.ContainsKey(id!)) throw new ModelException("Gemini Live calls require unique identifiers and names.");
                var args = call.TryGetProperty("args", out var input) && input.ValueKind != JsonValueKind.Null ? input : JsonSerializer.SerializeToElement(new { }); RequireObject(args);
                uses.Add(new(id!, name!, args.Clone()));
            }
            foreach (var call in uses) ToolNames.Add(call.ToolUseId, call.Name);
            if (uses.Count > 0) events.Add(new BidiToolUseBlocksEvent(uses));
        }
        if (native.TryGetProperty("toolCallCancellation", out var cancellation))
        {
            RequireObject(cancellation);
            if (!cancellation.TryGetProperty("ids", out var ids)) throw new ModelException("Gemini Live cancellation requires identifiers."); RequireArray(ids);
            foreach (var id in ids.EnumerateArray()) { if (id.ValueKind != JsonValueKind.String) throw new ModelException("Invalid cancelled tool identifier."); ToolNames.Remove(id.GetString()!); }
            events.Add(new BidiNativeEvent(native));
        }
        if (native.TryGetProperty("usageMetadata", out var usage)) events.Add(UsageEvent(usage));
        if (opaque && !events.Any(value => value is BidiNativeEvent)) events.Add(new BidiNativeEvent(native));
        var wrapped = events.Where(value => value is BidiTranscriptStartEvent { Role: Role.User }).ToList();
        if (_responseId is null && events.Any(value => value is BidiAudioDeltaEvent or BidiTextDeltaEvent or BidiReasoningDeltaEvent or BidiToolUseBlocksEvent or BidiTranscriptDeltaEvent { Role: Role.Assistant }))
        {
            _responseId = Id(); _responseInputs = _inputId is null ? [] : [_inputId]; wrapped.Add(new BidiResponseStartEvent(_responseId));
        }
        wrapped.AddRange(events.Where(value => value is not BidiTranscriptStartEvent { Role: Role.User }));
        if (interrupted && _responseId is not null) _interrupted = true;
        if (_audioId is not null && (interrupted || complete || generated)) { wrapped.Add(new BidiAudioStopEvent(_audioId)); _audioId = null; }
        if (complete)
        {
            var inputs = _responseId is null ? (_inputId is null ? [] : new[] { _inputId }) : _responseInputs.ToArray();
            foreach (var id in inputs) if (_inputIds.Contains(id)) StopInput(wrapped, id);
            if (_responseId is not null)
            {
                StopText(wrapped);
                if (_outputTranscriptId is not null) wrapped.Add(new BidiTranscriptStopEvent(Role.Assistant, _outputTranscriptId));
                wrapped.Add(new BidiResponseStopEvent(_responseId)); _responseId = null; _responseInputs.Clear(); _outputTranscriptId = null; _interrupted = false;
            }
        }
        return wrapped;
    }
    private void StartInput(List<IBidiOutputEvent> events) { _inputId = Id(); _inputIds.Add(_inputId); events.Add(new BidiTranscriptStartEvent(Role.User, _inputId)); }
    private void StopInput(List<IBidiOutputEvent> events, string id) { _inputIds.Remove(id); if (_inputId == id) _inputId = null; events.Add(new BidiTranscriptStopEvent(Role.User, id)); }
    private void StopText(List<IBidiOutputEvent> events) { if (_textId is null) return; events.Add(_reasoning ? new BidiReasoningStopEvent(_textId) : new BidiTextStopEvent(_textId)); _textId = null; }
    private static BidiUsageEvent UsageEvent(JsonElement usage)
    {
        RequireObject(usage); var input = Details(usage, "promptTokensDetails"); var output = Details(usage, "responseTokensDetails");
        if (usage.TryGetProperty("cachedContentTokenCount", out _)) input["cache_read"] = Count(usage, "cachedContentTokenCount");
        if (usage.TryGetProperty("thoughtsTokenCount", out _)) output["reasoning"] = Count(usage, "thoughtsTokenCount");
        return new(new Usage(Count(usage, "promptTokenCount"), Count(usage, "responseTokenCount"), ReportedTotalTokens: Count(usage, "totalTokenCount")), input.Count == 0 ? null : input, output.Count == 0 ? null : output);
    }
    private static Dictionary<string, long> Details(JsonElement usage, string key)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!usage.TryGetProperty(key, out var details) || details.ValueKind == JsonValueKind.Null) return result;
        RequireArray(details);
        foreach (var item in details.EnumerateArray()) { RequireObject(item); var modality = Text(item, "modality"); if (modality is "TEXT" or "AUDIO" or "IMAGE" or "VIDEO") result[modality.ToLowerInvariant()] = Count(item, "tokenCount"); }
        return result;
    }
    private static string Id() => Guid.NewGuid().ToString();
    private static void RequireObject(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw new ModelException("Gemini Live requires an object."); }
    private static void RequireArray(JsonElement value) { if (value.ValueKind != JsonValueKind.Array) throw new ModelException("Gemini Live requires an array."); }
    private static string? Text(JsonElement value, string key) { if (!value.TryGetProperty(key, out var text) || text.ValueKind == JsonValueKind.Null) return null; if (text.ValueKind != JsonValueKind.String) throw new ModelException("Gemini Live field '" + key + "' requires text."); return text.GetString(); }
    private static bool Flag(JsonElement value, string key) { if (!value.TryGetProperty(key, out var flag) || flag.ValueKind == JsonValueKind.Null) return false; if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ModelException("Gemini Live field '" + key + "' requires a boolean."); return flag.GetBoolean(); }
    private static long Count(JsonElement value, string key) { if (!value.TryGetProperty(key, out var count) || count.ValueKind == JsonValueKind.Null) return 0; if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var number) || number < 0) throw new ModelException("Gemini Live usage requires nonnegative integer counts."); return number; }
}
