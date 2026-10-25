using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

internal sealed class RealtimeState(bool transcriptionEnabled)
{
    internal HashSet<string> ActiveResponses { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> PendingTools { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> PendingInputIds { get; } = new(StringComparer.Ordinal);
    internal bool ResponsePending;
    internal bool ResponseRequested;
    internal bool InputAudioPending;
    private readonly Dictionary<string, bool> _transcripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _audio = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenToolCalls = new(StringComparer.Ordinal);

    internal void RestorePending(IEnumerable<string> ids)
    {
        foreach (var id in ids) { PendingTools.Add(id); _seenToolCalls.Add(id); }
    }

    internal IReadOnlyList<IBidiOutputEvent> Process(JsonElement root)
    {
        var type = RealtimeJson.Text(root, "type");
        var events = new List<IBidiOutputEvent>();
        switch (type)
        {
            case "response.created":
                var response = RealtimeJson.Object(root, "response"); var id = RealtimeJson.Text(response, "id");
                if (!ActiveResponses.Add(id)) return events;
                ResponseRequested = false; ResponsePending = PendingInputIds.Count > 0;
                events.Add(new BidiResponseStartEvent(id)); break;
            case "input_audio_buffer.speech_started":
                InputAudioPending = true;
                if (transcriptionEnabled) StartTranscript(RealtimeJson.Text(root, "item_id"), Role.User, events);
                break;
            case "input_audio_buffer.committed":
                InputAudioPending = false; ResponseRequested = true;
                if (transcriptionEnabled && RealtimeJson.OptionalText(root, "item_id") is { } committed) StartTranscript(committed, Role.User, events);
                break;
            case "conversation.item.added":
                var item = RealtimeJson.Object(root, "item"); PendingInputIds.Remove(RealtimeJson.Text(item, "id"));
                if (transcriptionEnabled && RealtimeJson.OptionalText(item, "role") == "user" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(part => RealtimeJson.OptionalText(part, "type") == "input_audio")) StartTranscript(RealtimeJson.Text(item, "id"), Role.User, events);
                break;
            case "response.output_audio.delta":
                var audioResponse = RealtimeJson.Text(root, "response_id"); StartAudio(audioResponse, events);
                events.Add(new BidiAudioDeltaEvent(RealtimeJson.Text(root, "delta"), _audio[audioResponse], 24000, 1, "pcm")); break;
            case "response.content_part.added":
                if (RealtimeJson.OptionalText(RealtimeJson.Object(root, "part"), "type") == "audio") StartAudio(RealtimeJson.Text(root, "response_id"), events);
                break;
            case "response.output_audio.done": StopAudio(RealtimeJson.Text(root, "response_id"), events); break;
            case "response.output_text.delta":
            case "response.output_audio_transcript.delta":
                var text = RealtimeJson.OptionalText(root, "delta") ?? "";
                if (text.Length == 0) break;
                var textResponse = RealtimeJson.Text(root, "response_id");
                var textOnly = type == "response.output_text.delta";
                var contentId = textOnly ? textResponse + ":text" : textResponse;
                var partId = RealtimeJson.Text(root, "item_id") + ":" + RealtimeJson.Count(root, "content_index");
                var prior = _parts.TryGetValue(contentId, out var previous);
                if (prior && previous != partId) text = "\n\n" + text;
                _parts[contentId] = partId;
                if (textOnly) { if (!prior) events.Add(new BidiTextStartEvent(contentId)); events.Add(new BidiTextDeltaEvent(text, contentId)); }
                else Transcript(contentId, Role.Assistant, text, events);
                break;
            case "conversation.item.input_audio_transcription.delta":
                Transcript(RealtimeJson.Text(root, "item_id"), Role.User, RealtimeJson.OptionalText(root, "delta") ?? "", events); break;
            case "conversation.item.input_audio_transcription.segment":
                var segment = RealtimeJson.Object(root, "segment");
                var role = RealtimeJson.OptionalText(segment, "role") ?? "user";
                if (role is not ("user" or "assistant")) throw new ModelException("Invalid Realtime transcript speaker.");
                Transcript(RealtimeJson.Text(root, "item_id"), role == "user" ? Role.User : Role.Assistant, RealtimeJson.OptionalText(segment, "text") ?? "", events); break;
            case "conversation.item.input_audio_transcription.completed":
                StopTranscript(RealtimeJson.Text(root, "item_id"), Role.User, RealtimeJson.OptionalText(root, "transcript") ?? "", events); break;
            case "conversation.item.input_audio_transcription.failed": throw new ModelException(RealtimeJson.OptionalText(RealtimeJson.Object(root, "error"), "message") ?? "Realtime transcription failed.");
            case "response.output_item.done":
                var output = RealtimeJson.Object(root, "item");
                if (RealtimeJson.OptionalText(output, "type") == "function_call" && RealtimeJson.OptionalText(output, "status") == "completed")
                {
                    var toolId = RealtimeJson.Text(output, "call_id");
                    using var input = JsonDocument.Parse(RealtimeJson.Text(output, "arguments"));
                    if (input.RootElement.ValueKind != JsonValueKind.Object) throw new ModelException("Realtime tool arguments must be a JSON object.");
                    try { _ = RealtimeJson.Copy(input.RootElement); }
                    catch (ArgumentException invalidArguments) { throw new ModelException("Realtime tool arguments contain ambiguous JSON fields.", invalidArguments); }
                    var call = new ToolUseBlock(toolId, RealtimeJson.Text(output, "name"), input.RootElement);
                    if (!_seenToolCalls.Add(toolId) || !PendingTools.Add(toolId)) throw new ModelException("Realtime repeated a tool identifier.");
                    events.Add(new BidiToolUseBlocksEvent([call]));
                }
                break;
            case "response.done":
                var done = RealtimeJson.Object(root, "response"); var completedId = RealtimeJson.Text(done, "id");
                if (ActiveResponses.Remove(completedId)) Complete(done, completedId, events);
                break;
            case "error":
                var error = RealtimeJson.Object(root, "error"); var code = RealtimeJson.OptionalText(error, "code");
                if (code == "conversation_already_has_active_response") { ResponseRequested = false; ResponsePending = true; }
                else if (code != "response_cancel_not_active") events.Add(new BidiNativeEvent(root));
                break;
            case "session.created":
            case "session.updated":
            case "response.output_text.done":
            case "response.output_audio_transcript.done":
            case "conversation.item.done":
            case "conversation.item.retrieved":
            case "response.output_item.added":
            case "response.content_part.done":
            case "response.function_call_arguments.delta":
            case "response.function_call_arguments.done":
            case "input_audio_buffer.cleared": break;
            default: events.Add(new BidiNativeEvent(root)); break;
        }
        return events;
    }
    private void Complete(JsonElement response, string id, List<IBidiOutputEvent> events)
    {
        if (RealtimeJson.OptionalText(response, "status") == "cancelled" && response.TryGetProperty("status_details", out var details) && RealtimeJson.OptionalText(details, "reason") == "turn_detected") events.Add(new BidiBargeInEvent());
        StopAudio(id, events);
        var output = response.TryGetProperty("output", out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
        var parts = output.Where(item => RealtimeJson.OptionalText(item, "type") == "message" && RealtimeJson.OptionalText(item, "role") == "assistant")
            .SelectMany(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToArray() : []);
        var contentParts = parts.ToArray();
        var transcripts = contentParts.Where(part => RealtimeJson.OptionalText(part, "type") == "output_audio").Select(part => RealtimeJson.OptionalText(part, "transcript") ?? "").ToArray();
        if (transcripts.Length > 0 || _parts.ContainsKey(id)) StopTranscript(id, Role.Assistant, string.Join("\n\n", transcripts), events);
        _parts.Remove(id);
        var text = contentParts.Where(part => RealtimeJson.OptionalText(part, "type") == "output_text").Select(part => RealtimeJson.OptionalText(part, "text") ?? "").ToArray();
        var contentId = id + ":text";
        if (text.Length > 0 || _parts.ContainsKey(contentId))
        {
            if (!_parts.ContainsKey(contentId)) { events.Add(new BidiTextStartEvent(contentId)); events.Add(new BidiTextDeltaEvent(string.Join("\n\n", text), contentId)); }
            events.Add(new BidiTextStopEvent(contentId));
        }
        _parts.Remove(contentId);
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new ModelException("Realtime usage must be an object.");
            var counts = new Usage(RealtimeJson.Count(usage, "input_tokens"), RealtimeJson.Count(usage, "output_tokens"), ReportedTotalTokens: usage.TryGetProperty("total_tokens", out _) ? RealtimeJson.Count(usage, "total_tokens") : null);
            try { _ = counts.TotalTokens; }
            catch (OverflowException error) { throw new ModelException("Realtime token totals exceed the supported integer range.", error); }
            events.Add(new BidiUsageEvent(counts, TokenDetails(usage, "input_token_details"), TokenDetails(usage, "output_token_details")));
        }
        events.Add(new BidiResponseStopEvent(id));
    }
    private static IReadOnlyDictionary<string, long>? TokenDetails(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var details) || details.ValueKind == JsonValueKind.Null) return null;
        if (details.ValueKind != JsonValueKind.Object) throw new ModelException("Realtime token details must be an object.");
        var output = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in new[] { ("text_tokens", "text"), ("audio_tokens", "audio"), ("image_tokens", "image"), ("video_tokens", "video"), ("cached_tokens", "cache_read"), ("reasoning_tokens", "reasoning") })
            if (details.TryGetProperty(item.Item1, out _)) output[item.Item2] = RealtimeJson.Count(details, item.Item1);
        return output.Count == 0 ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(output);
    }
    private void StartAudio(string id, List<IBidiOutputEvent> events)
    {
        if (_audio.ContainsKey(id)) return;
        var contentId = Guid.NewGuid().ToString(); _audio.Add(id, contentId); events.Add(new BidiAudioStartEvent(contentId));
    }
    private void StopAudio(string id, List<IBidiOutputEvent> events)
    {
        if (!_audio.TryGetValue(id, out var contentId)) return;
        _audio.Remove(id); events.Add(new BidiAudioStopEvent(contentId));
    }
    private void StartTranscript(string id, Role role, List<IBidiOutputEvent> events)
    {
        if (_transcripts.ContainsKey(id)) return;
        _transcripts.Add(id, false); events.Add(new BidiTranscriptStartEvent(role, id));
    }
    private void Transcript(string id, Role role, string text, List<IBidiOutputEvent> events)
    {
        if (text.Length == 0) return;
        StartTranscript(id, role, events); _transcripts[id] = true; events.Add(new BidiTranscriptDeltaEvent(text, role, id));
    }
    private void StopTranscript(string id, Role role, string text, List<IBidiOutputEvent> events)
    {
        StartTranscript(id, role, events);
        if (text.Length > 0 && !_transcripts[id]) events.Add(new BidiTranscriptDeltaEvent(text, role, id));
        _transcripts.Remove(id); events.Add(new BidiTranscriptStopEvent(role, id));
    }
}
