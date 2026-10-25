using System.Text.Json;

namespace Strands.Agents.Models;

internal static class LlamaApiFormatting
{
    internal static Dictionary<string, object?> Format(ModelRequest request, LlamaApiModelOptions options)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages)
        {
            if (message is null || message.Role is not (Role.User or Role.Assistant)) throw new ArgumentException("Llama API messages require a valid role.", nameof(request));
            if (message.Role == Role.Assistant)
            {
                var text = new List<string>(); var projected = new List<object>(); var calls = new List<object>();
                foreach (var block in message.Content)
                    if (block is ToolUseBlock call)
                    {
                        Guard.NotNullOrWhiteSpace(call.ToolUseId, nameof(call.ToolUseId)); Guard.NotNullOrWhiteSpace(call.Name, nameof(call.Name));
                        if (call.Input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool input must be an object.", nameof(request));
                        calls.Add(new { id = call.ToolUseId, function = new { name = call.Name, arguments = call.Input.GetRawText() } });
                    }
                    else if (options.AssistantContentPolicy == LlamaApiAssistantContentPolicy.PythonFirstBlock) projected.Add(UserContent(block));
                    else if (block is TextBlock plain) text.Add(plain.Text);
                    else throw Unsupported(block);
                object content;
                if (options.AssistantContentPolicy == LlamaApiAssistantContentPolicy.PythonFirstBlock)
                {
                    if (projected.Count == 0 && calls.Count == 0) continue;
                    content = projected.Count == 0 ? "" : projected[0];
                }
                else
                {
                    var joined = string.Concat(text);
                    if (joined.Length == 0 && calls.Count == 0) continue;
                    content = joined.Length == 0 ? "" : new { type = "text", text = joined };
                }
                var assistant = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content };
                if (calls.Count != 0) assistant.Add("tool_calls", calls);
                messages.Add(assistant);
            }
            else
            {
                var content = message.Content.Where(block => block is not ToolResultBlock).Select(UserContent).ToArray();
                if (content.Length != 0) messages.Add(new { role = "user", content });
                foreach (var result in message.Content.OfType<ToolResultBlock>())
                {
                    var blocks = result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)];
                    var output = blocks.Select(block => block is JsonBlock json ? UserContent(new TextBlock(json.Json.GetRawText())) : UserContent(block)).ToList();
                    if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(block => JsonElement.DeepEquals(block.Json, structured))) output.Add(new { type = "text", text = structured.GetRawText() });
                    messages.Add(new { role = "tool", tool_call_id = result.ToolUseId, content = output });
                }
            }
        }
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
            ["tools"] = request.Tools?.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray() ?? []
        };
        if (options.RepetitionPenalty is { } repetition) body.Add("repetition_penalty", repetition);
        if (options.Temperature is { } temperature) body.Add("temperature", temperature);
        if (options.TopP is { } topP) body.Add("top_p", topP);
        if (options.MaxCompletionTokens is { } maximum) body.Add("max_completion_tokens", maximum);
        if (options.TopK is { } topK) body.Add("top_k", topK);
        if (request.ToolChoice is { } choice)
        {
            if (options.HonorToolChoice)
            {
                ProviderConfiguration.ValidateToolChoice(request);
                body.Add("tool_choice", choice.Mode switch
                {
                    ToolChoiceMode.Auto => "auto",
                    ToolChoiceMode.Any => "required",
                    ToolChoiceMode.None => "none",
                    ToolChoiceMode.Tool => (object)new { type = "function", function = new { name = choice.Name } },
                    _ => throw new ArgumentException("Unknown tool choice.", nameof(request))
                });
            }
            else
                try { SdkLogging.Warning("ToolChoice was provided to this provider but is not supported by the pinned Llama API adapter; set HonorToolChoice to enable native wire support."); } catch (Exception) { }
        }
        return body;
    }
    private static object UserContent(ContentBlock block)
    {
        if (block is TextBlock text) return new { type = "text", text = text.Text };
        if (block is ImageBlock image && image.Location is null && image.Url is null)
            return new { type = "image_url", image_url = new { url = $"data:{image.MediaType};base64,{MediaContent.InlineData(image, "Llama API")}" } };
        throw Unsupported(block);
    }
    private static NotSupportedException Unsupported(ContentBlock block) => new($"Llama API cannot represent {block.GetType().Name} in this message position.");
}
