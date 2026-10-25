using System.Text.Json;

namespace Strands.Agents.Models;

internal static class LlamaCppFormatting
{
    private static readonly HashSet<string> SupportedParameters = new(StringComparer.Ordinal)
    {
        "grammar", "json_schema", "repeat_penalty", "top_k", "min_p", "typical_p", "tfs_z", "top_a", "mirostat",
        "mirostat_lr", "mirostat_ent", "penalty_last_n", "n_probs", "min_keep", "ignore_eos", "logit_bias", "cache_prompt",
        "slot_id", "samplers", "temperature", "max_tokens", "top_p", "frequency_penalty", "presence_penalty", "stop", "seed",
        "n", "logprobs", "top_logprobs", "response_format"
    };
    internal static void ValidateParameters(JsonElement? parameters)
    {
        if (parameters is not { } values) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            if (!names.Add(property.Name) || !SupportedParameters.Contains(property.Name)) throw new ArgumentException($"Unsupported or duplicate llama.cpp parameter '{property.Name}'.", nameof(parameters));
            if (property.Name == "max_tokens" && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var maximum) || maximum <= 0)) throw new ArgumentException("max_tokens must be a positive integer.", nameof(parameters));
            if (property.Name == "n" && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var choices) || choices != 1)) throw new ArgumentException("The native model stream supports exactly one response choice.", nameof(parameters));
        }
    }
    internal static Dictionary<string, object?> Request(ModelRequest request, LlamaCppModelOptions options, JsonElement? parameters, JsonElement? schema)
    {
        var body = ProviderConfiguration.Parameters(parameters);
        body["messages"] = Messages(request);
        body["model"] = options.ModelId;
        body["stream"] = true;
        body["stream_options"] = new { include_usage = true };
        body["tools"] = Tools(request.Tools ?? []);
        if (options.CacheConfig is not null && !body.ContainsKey("cache_prompt")) body["cache_prompt"] = true;
        if (schema is { } constraint) { body["json_schema"] = constraint; body["cache_prompt"] = true; }
        if (request.ToolChoice is not null) SdkLogging.Warning("llama.cpp ignores per-call tool choice, matching the pinned provider.");
        if (options.CacheConfig is not null) SdkLogging.Warning("llama.cpp has no per-section prompt cache controls.");
        return body;
    }
    internal static object[] Tools(IReadOnlyList<ToolSpec> tools) => tools.Select(tool => (object)new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray();
    internal static List<object> Messages(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages)
        {
            var contents = message.Content.Where(value => value is not (ToolUseBlock or ToolResultBlock)).Select(Content).ToArray();
            var calls = message.Content.OfType<ToolUseBlock>().Select(call => new { id = call.ToolUseId, type = "function", function = new { name = call.Name, arguments = call.Input.GetRawText() } }).ToArray();
            if (contents.Length > 0 || calls.Length > 0)
            {
                var ordinary = new Dictionary<string, object?> { ["role"] = message.Role == Role.User ? "user" : "assistant", ["content"] = contents };
                if (calls.Length > 0) ordinary["tool_calls"] = calls;
                messages.Add(ordinary);
            }
            foreach (var result in message.Content.OfType<ToolResultBlock>())
            {
                var parts = result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)];
                var content = parts.Select(part => Content(part is JsonBlock json ? new TextBlock(json.Json.GetRawText()) : part)).ToArray();
                if (content.Length > 0 || result.HasExplicitContent) messages.Add(new { role = "tool", tool_call_id = result.ToolUseId, content });
            }
        }
        return messages;
    }
    private static object Content(ContentBlock block) => block switch
    {
        TextBlock text => new { type = "text", text = text.Text },
        CitationsBlock citations => new { type = "text", text = citations.Text },
        ImageBlock image => new { type = "image_url", image_url = new { detail = "auto", format = image.MediaType, url = image.Url?.AbsoluteUri ?? "data:" + image.MediaType + ";base64," + MediaContent.InlineData(image, "llama.cpp") } },
        AudioBlock audio => new { type = "input_audio", input_audio = new { data = MediaContent.InlineData(audio, "llama.cpp"), format = AudioFormat(audio.MediaType) } },
        DocumentBlock document => new { type = "file", file = new { file_data = "data:" + document.MediaType + ";base64," + MediaContent.InlineData(document, "llama.cpp"), filename = document.Name ?? "document" } },
        _ => throw new NotSupportedException($"llama.cpp cannot represent {block.GetType().Name} input.")
    };
    private static string AudioFormat(string mediaType) => mediaType switch
    {
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        _ when mediaType.StartsWith("audio/", StringComparison.Ordinal) && mediaType.Length > 6 => mediaType.Substring(6),
        _ => throw new NotSupportedException($"llama.cpp audio needs an audio media type, received {mediaType}.")
    };
}
