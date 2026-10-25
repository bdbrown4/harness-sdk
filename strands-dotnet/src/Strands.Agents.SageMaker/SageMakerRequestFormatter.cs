using System.Text.Encodings.Web;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

internal static class SageMakerRequestFormatter
{
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static Dictionary<string, object?> Payload(ModelRequest request, SageMakerModelOptions options)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.Messages is null) throw new ArgumentNullException(nameof(request.Messages));
        if (request.ToolChoice is not null) SdkLogging.Warning("SageMaker ignores requested tool choice, matching the pinned provider.");
        if (options.CacheConfig is not null) SdkLogging.Warning("SageMaker ignores prompt cache controls.");
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages)
        {
            if (message is null) throw new ArgumentException("Messages cannot contain null.", nameof(request));
            foreach (var block in message.Content) ValidateMedia(block);
            foreach (var result in message.Content.OfType<ToolResultBlock>())
            {
                var blocks = result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) };
                var parts = blocks.Select(block => block switch { TextBlock text => text.Text, JsonBlock json => JsonSerializer.Serialize(json.Json, TextJson), _ => JsonSerializer.Serialize(block, block.GetType(), TextJson) }).ToList();
                if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(block => block.Json.GetRawText() == structured.GetRawText())) parts.Add(JsonSerializer.Serialize(structured, TextJson));
                var content = string.Join(" ", parts);
                messages.Add(options.Payload.ToolResultsAsUserMessages
                    ? new { role = "user", content = $"Tool call ID '{result.ToolUseId}' returned: {content}" }
                    : (object)new { role = "tool", tool_call_id = result.ToolUseId, content });
            }
            var visible = new List<object>();
            foreach (var block in message.Content.Where(block => block is not (ToolUseBlock or ToolResultBlock)))
            {
                if (block is ReasoningBlock or RedactedReasoningBlock or CachePointBlock) { SdkLogging.Warning("SageMaker skips {BlockType} in chat history, matching the pinned provider.", block.GetType().Name); continue; }
                visible.Add(Content(block));
            }
            var calls = message.Content.OfType<ToolUseBlock>().Select(call => new { id = call.ToolUseId, type = "function", function = new { name = call.Name, arguments = call.Input.GetRawText() } }).ToArray();
            if (visible.Count > 0 || calls.Length > 0 || message.Content.Count == 0)
            {
                var formatted = new Dictionary<string, object?> { ["role"] = message.Role == Role.Assistant ? "assistant" : "user" };
                if (visible.Count > 0 || calls.Length == 0) formatted["content"] = visible;
                if (calls.Length > 0) formatted["tool_calls"] = calls;
                messages.Add(formatted);
            }
        }
        var payload = new Dictionary<string, object?> { ["messages"] = messages, ["stream"] = options.Payload.Stream };
        if (options.Payload.MaxTokens is { } maximum) payload["max_tokens"] = maximum;
        if (options.Payload.Temperature is { } temperature) payload["temperature"] = temperature;
        if (options.Payload.TopP is { } topP) payload["top_p"] = topP;
        if (options.Payload.TopK is { } topK) payload["top_k"] = topK;
        if (options.Payload.Stop is { } stop) payload["stop"] = stop;
        payload["tools"] = (request.Tools ?? []).Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray();
        foreach (var extra in ProviderConfiguration.Parameters(options.Payload.AdditionalArguments)) payload[extra.Key] = extra.Value;
        var tools = JsonSerializer.SerializeToElement(payload["tools"]);
        if (tools.ValueKind == JsonValueKind.Null || tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0) { payload.Remove("tools"); payload.Remove("tool_choice"); }
        else if (tools.ValueKind == JsonValueKind.Array) payload["tool_choice"] = "auto";
        else throw new ArgumentException("SageMaker tools must be a JSON array.", nameof(options));
        return payload;
    }
    private static object Content(ContentBlock block) => block switch
    {
        TextBlock text => new { type = "text", text = text.Text },
        CitationsBlock citations => new { type = "text", text = citations.Text },
        JsonBlock json => new { type = "text", text = json.Json.GetRawText() },
        ImageBlock image => new { type = "image_url", image_url = new { url = ProviderConfiguration.DataUrl(image), detail = "auto" } },
        AudioBlock audio => new { type = "input_audio", input_audio = new { data = MediaContent.InlineData(audio, "SageMaker"), format = ProviderConfiguration.AudioFormat(audio) } },
        DocumentBlock document => new { type = "file", file = new { file_data = $"data:{document.MediaType};base64,{MediaContent.InlineData(document, "SageMaker")}", filename = document.Name } },
        VideoBlock video => new { type = "video_url", video_url = new { url = MediaContent.InlineData(video, "SageMaker"), detail = "auto" } },
        ResourceLinkBlock link => new { type = "text", text = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, description = link.Description, mimeType = link.MediaType }) },
        _ => throw new NotSupportedException($"SageMaker cannot represent {block.GetType().Name} input.")
    };
    private static void ValidateMedia(ContentBlock block)
    {
        MediaContent.RejectS3(block, "SageMaker");
        if (block is GuardContentBlock) throw new NotSupportedException("SageMaker does not support selectively guarded content.");
        if (block is ToolResultBlock result)
            foreach (var content in result.ContentBlocks) ValidateMedia(content);
    }
}
