using System.Text.Json;

namespace Strands.Agents.Models;

internal static class GoogleFormatting
{
    internal static Dictionary<string, object?> Request(ModelRequest request, JsonElement? parameters, JsonElement? generation, IReadOnlyList<JsonElement> builtIns)
        => Request(request, parameters, generation, builtIns, useVertexAi: false);

    internal static Dictionary<string, object?> Request(ModelRequest request, JsonElement? parameters, JsonElement? generation, IReadOnlyList<JsonElement> builtIns, bool useVertexAi)
    {
        Guard.NotNull(request, nameof(request));
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var body = ProviderConfiguration.Parameters(parameters);
        var config = Generation(parameters, generation);
        if (config.Count > 0) body["generationConfig"] = config;
        body["contents"] = Messages(request.Messages);
        if (request.SystemPrompt is { } instructions) body["systemInstruction"] = new { parts = new[] { new { text = instructions } } };

        var tools = new List<JsonElement>();
        if (body.TryGetValue("tools", out var native))
        {
            if (native is not JsonElement { ValueKind: JsonValueKind.Array } array) throw new ArgumentException("Google native tools must be an array.", nameof(parameters));
            tools.AddRange(array.EnumerateArray().Select(item => item.Clone()));
        }
        if (request.Tools is { Count: > 0 } specifications) tools.Add(JsonSerializer.SerializeToElement(new { functionDeclarations = specifications.Select(tool => new { name = tool.Name, description = tool.Description, parametersJsonSchema = tool.InputSchema }).ToArray() }));
        tools.AddRange(builtIns);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hasBuiltIns = false;
        foreach (var tool in tools)
        {
            if (tool.ValueKind != JsonValueKind.Object) throw new ArgumentException("Google tools must be objects.", nameof(parameters));
            if (tool.TryGetProperty("functionDeclarations", out var declarations))
            {
                if (declarations.ValueKind != JsonValueKind.Array) throw new ArgumentException("Google functionDeclarations must be an array.", nameof(parameters));
                foreach (var declaration in declarations.EnumerateArray())
                    if (!names.Add(ProviderHttp.RequiredString(declaration, "name"))) throw new ArgumentException("Google function names must be unique.", nameof(request));
            }
            hasBuiltIns |= tool.EnumerateObject().Any(property => property.Name != "functionDeclarations");
        }
        if (tools.Count > 0) body["tools"] = tools;
        var toolConfig = body.TryGetValue("toolConfig", out var explicitConfig)
            ? ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(explicitConfig is JsonElement element ? element : throw new ArgumentException("Google toolConfig must be an object.", nameof(parameters))))
            : new Dictionary<string, object?>();
        if (explicitConfig is null && request.ToolChoice is { } choice)
        {
            if (!Enum.IsDefined(typeof(ToolChoiceMode), choice.Mode) || choice.Mode != ToolChoiceMode.Tool && choice.Name is not null) throw new ArgumentException("Invalid Google tool selection.", nameof(request));
            if (choice.Mode == ToolChoiceMode.Any && names.Count == 0) throw new ArgumentException("Required selection needs a function declaration.", nameof(request));
            if (choice.Mode == ToolChoiceMode.Tool && (choice.Name is null || !names.Contains(choice.Name))) throw new ArgumentException("Named selection needs a supplied function.", nameof(request));
            toolConfig["functionCallingConfig"] = choice.Mode == ToolChoiceMode.Tool
                ? new { mode = "ANY", allowedFunctionNames = new[] { choice.Name } }
                : new { mode = choice.Mode switch { ToolChoiceMode.Auto => "AUTO", ToolChoiceMode.Any => "ANY", ToolChoiceMode.None => "NONE", _ => throw new ArgumentException("Invalid Google tool selection.", nameof(request)) } };
        }
        if (!useVertexAi && hasBuiltIns && names.Count > 0 && !toolConfig.ContainsKey("includeServerSideToolInvocations")) toolConfig["includeServerSideToolInvocations"] = true;
        if (toolConfig.TryGetValue("functionCallingConfig", out var functionConfiguration))
        {
            var function = JsonSerializer.SerializeToElement(functionConfiguration);
            if (function.ValueKind != JsonValueKind.Object) throw new ArgumentException("Google functionCallingConfig must be an object.", nameof(parameters));
            if (function.TryGetProperty("streamFunctionCallArguments", out var partial) && partial.ValueKind != JsonValueKind.False) throw new NotSupportedException("Partial Google function arguments are not yet supported; leave streamFunctionCallArguments disabled.");
        }
        if (toolConfig.Count > 0) body["toolConfig"] = toolConfig;
        return body;
    }

    internal static Dictionary<string, object?> Generation(JsonElement? parameters, JsonElement? generation)
    {
        var body = ProviderConfiguration.Parameters(parameters);
        var config = body.TryGetValue("generationConfig", out var configured) && configured is JsonElement value
            ? ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(value)) : new Dictionary<string, object?>();
        foreach (var item in ProviderConfiguration.Parameters(generation)) config[item.Key] = item.Value;
        if (config.TryGetValue("candidateCount", out var candidates) && candidates is JsonElement count && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number != 1)) throw new NotSupportedException("GoogleModel supports one candidate per invocation.");
        if (config.TryGetValue("maxOutputTokens", out var maximum) && maximum is JsonElement tokens && (tokens.ValueKind != JsonValueKind.Number || !tokens.TryGetInt64(out var limit) || limit <= 0)) throw new ArgumentException("Google maxOutputTokens must be positive.", nameof(generation));
        return config;
    }

    internal static List<object> Messages(IReadOnlyList<Message> messages)
    {
        var result = new List<object>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            Guard.NotNull(message, nameof(messages));
            var parts = new List<object>();
            foreach (var block in MediaContent.ExpandDocuments(message.Content))
            {
                if (block is ToolUseBlock call)
                {
                    if (message.Role != Role.Assistant) throw new ArgumentException("Google function calls must be assistant content.", nameof(messages));
                    names[call.ToolUseId] = call.Name; parts.Add(FunctionCall(call));
                }
                else if (block is ToolResultBlock output)
                {
                    if (message.Role != Role.User) throw new ArgumentException("Google function responses must be user content.", nameof(messages));
                    parts.Add(FunctionResult(output, names.TryGetValue(output.ToolUseId, out var name) ? name : output.ToolUseId));
                }
                else parts.Add(Input(block));
            }
            if (parts.Count > 0) result.Add(new { role = message.Role == Role.Assistant ? "model" : "user", parts });
        }
        if (result.Count == 0) throw new ArgumentException("Google requires at least one nonempty message.", nameof(messages));
        return result;
    }

    private static object Input(ContentBlock block) => block switch
    {
        TextBlock text => new { text = text.Text },
        CitationsBlock citations => new { text = citations.Text },
        ReasoningBlock reasoning => Reasoning(reasoning),
        JsonBlock json => new { text = json.Json.GetRawText() },
        ImageBlock image => Image(image),
        AudioBlock audio => new { inlineData = new { mimeType = audio.MediaType, data = MediaContent.InlineData(audio, "Google") } },
        VideoBlock video => new { inlineData = new { mimeType = video.MediaType, data = MediaContent.InlineData(video, "Google") } },
        DocumentBlock document => new { inlineData = new { mimeType = document.MediaType, data = MediaContent.InlineData(document, "Google"), displayName = document.Name } },
        ResourceLinkBlock link => new { text = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, mimeType = link.MediaType, description = link.Description }) },
        GoogleContentBlock google => google.Part,
        _ => throw new NotSupportedException($"Google cannot represent {block.GetType().Name} as a native part.")
    };

    private static object Image(ImageBlock image)
    {
        MediaContent.Snapshot(image);
        return image.Url is { } url
            ? new { fileData = new { mimeType = image.MediaType, fileUri = url.AbsoluteUri } }
            : new { inlineData = new { mimeType = image.MediaType, data = MediaContent.InlineData(image, "Google") } };
    }

    private static object Reasoning(ReasoningBlock reasoning)
    {
        var result = new Dictionary<string, object?> { ["text"] = reasoning.Text, ["thought"] = true };
        if (reasoning.Signature is { } signature) result["thoughtSignature"] = signature;
        return result;
    }

    private static object FunctionCall(ToolUseBlock call)
    {
        if (call.Input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Google function input must be an object.", nameof(call));
        var part = new Dictionary<string, object?>();
        var function = new Dictionary<string, object?>();
        if (call.ProviderMetadata is { } metadata && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("google", out var original))
        {
            if (ProviderHttp.RequiredString(metadata, "googleToolUseId") != call.ToolUseId) throw new ArgumentException("Google tool metadata changed call identity.", nameof(call));
            var previous = ProviderHttp.RequiredObject(original, "functionCall");
            var previousInput = previous.TryGetProperty("args", out var arguments) ? arguments : ProviderHttp.EmptyObject;
            var previousId = ProviderHttp.OptionalString(previous, "id");
            if (ProviderHttp.RequiredString(previous, "name") != call.Name || previousId is not null && previousId != call.ToolUseId || !JsonElement.DeepEquals(previousInput, call.Input)) throw new ArgumentException("Google tool metadata disagrees with the authoritative call.", nameof(call));
            part = ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(original));
            function = ProviderConfiguration.Parameters(previous);
        }
        function["id"] = call.ToolUseId; function["name"] = call.Name; function["args"] = call.Input;
        part["functionCall"] = function;
        return part;
    }

    private static object FunctionResult(ToolResultBlock result, string name)
    {
        var output = new List<object>();
        var parts = new List<object>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in MediaContent.ExpandDocuments(result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)]))
        {
            switch (block)
            {
                case TextBlock text: output.Add(new { text = text.Text }); break;
                case JsonBlock json: output.Add(new { json = json.Json }); break;
                case ResourceLinkBlock link: output.Add(new { text = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, mimeType = link.MediaType, description = link.Description }) }); break;
                case ImageBlock image: Media(image.MediaType, MediaContent.InlineData(image, "Google"), "image." + image.MediaType.Substring(image.MediaType.IndexOf('/') + 1)); break;
                case DocumentBlock document: Media(document.MediaType, MediaContent.InlineData(document, "Google"), document.Name ?? "document"); break;
                default: throw new NotSupportedException($"Google cannot represent {block.GetType().Name} in a function response.");
            }
        }
        if (result.StructuredContent is { } structured && !result.ContentBlocks.OfType<JsonBlock>().Any(item => JsonElement.DeepEquals(item.Json, structured))) output.Add(new { json = structured });
        var function = new Dictionary<string, object?> { ["id"] = result.ToolUseId, ["name"] = name, ["response"] = new Dictionary<string, object?> { [result.Status == ToolResultStatus.Error ? "error" : "output"] = output } };
        if (parts.Count > 0) function["parts"] = parts;
        return new { functionResponse = function };

        void Media(string mimeType, string data, string displayName)
        {
            var unique = displayName; var suffix = 0;
            var dot = displayName.LastIndexOf('.');
            var stem = dot > 0 ? displayName.Substring(0, dot) : displayName;
            var extension = dot > 0 ? displayName.Substring(dot) : "";
            while (!usedNames.Add(unique)) unique = stem + "-" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture) + extension;
            parts.Add(new { inlineData = new { mimeType, data, displayName = unique } });
            output.Add(new Dictionary<string, string> { ["$ref"] = unique });
        }
    }
}
