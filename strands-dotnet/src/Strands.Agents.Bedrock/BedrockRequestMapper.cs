using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using NativeContent = Amazon.BedrockRuntime.Model.ContentBlock;
using NativeMessage = Amazon.BedrockRuntime.Model.Message;
using NativeToolResult = Amazon.BedrockRuntime.Model.ToolResultBlock;
using NativeToolUse = Amazon.BedrockRuntime.Model.ToolUseBlock;

namespace Strands.Agents.Bedrock;

internal sealed class BedrockRequestMapper : IDisposable
{
    private readonly List<MemoryStream> _sources = [];
    private static readonly ToolSpec HistoryTool = new("noop", "This is a fake tool that MUST be completely ignored.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""));

    internal ConverseStreamRequest Map(ModelRequest request, BedrockModelOptions options)
    {
        if (request.DynamicTrailingBlocks < 0) throw new ArgumentOutOfRangeException(nameof(request), "The dynamic suffix cannot be negative.");
        var lastUser = request.Messages.LastOrDefault(message => message.Role == Role.User);
        if (request.DynamicTrailingBlocks > 0 && (lastUser is null || request.DynamicTrailingBlocks > lastUser.Content.Count(block => block is not Strands.Agents.CachePointBlock)))
            throw new ArgumentOutOfRangeException(nameof(request), "The dynamic suffix must fit within the final user message content.");
        var native = new ConverseStreamRequest
        {
            ModelId = options.ModelId,
            Messages = options.FilterRejectedReasoningContent ? MapFilteredMessages(request.Messages, options) : request.Messages.Select(message => new NativeMessage
            {
                Role = message.Role == Role.User ? ConversationRole.User : ConversationRole.Assistant,
                Content = message.Content.Select(block => MapContent(block, options)).ToList()
            }).ToList(),
            System = MapSystem(request),
            InferenceConfig = options.MaxTokens is not null || options.Temperature is not null || options.TopP is not null || options.StopSequences is not null ? new InferenceConfiguration
            {
                MaxTokens = options.MaxTokens,
                Temperature = (float?)options.Temperature,
                TopP = (float?)options.TopP,
                StopSequences = options.StopSequences?.ToList()
            } : null
        };
        var tools = request.Tools;
        if (tools is not { Count: > 0 } && request.ToolChoice?.Mode != ToolChoiceMode.None &&
            request.Messages.Any(message => message.Content.Any(block => block is Strands.Agents.ToolUseBlock or Strands.Agents.ToolResultBlock)))
            tools = [HistoryTool];
        if (tools is { Count: > 0 } && request.ToolChoice?.Mode != ToolChoiceMode.None)
        {
            native.ToolConfig = new ToolConfiguration
            {
                Tools = tools.Select(tool => new Tool
                {
                    ToolSpec = MapToolSpecification(tool, options.StrictTools)
                }).ToList(),
                ToolChoice = request.ToolChoice?.Mode switch
                {
                    ToolChoiceMode.Auto => new Amazon.BedrockRuntime.Model.ToolChoice { Auto = new AutoToolChoice() },
                    ToolChoiceMode.Any => new Amazon.BedrockRuntime.Model.ToolChoice { Any = new AnyToolChoice() },
                    ToolChoiceMode.Tool => new Amazon.BedrockRuntime.Model.ToolChoice { Tool = new SpecificToolChoice { Name = request.ToolChoice.Name } },
                    _ => null
                }
            };
        }
        if (options.AdditionalModelRequestFields is { } fields)
        {
            if (request.ToolChoice?.Mode is ToolChoiceMode.Any or ToolChoiceMode.Tool && fields.TryGetProperty("thinking", out _))
            {
                var remaining = fields.EnumerateObject().Where(property => property.Name != "thinking").ToDictionary(property => property.Name, property => property.Value);
                if (remaining.Count > 0) native.AdditionalModelRequestFields = ToDocument(JsonSerializer.SerializeToElement(remaining));
            }
            else native.AdditionalModelRequestFields = ToDocument(fields);
        }
        native.AdditionalModelResponseFieldPaths = options.AdditionalModelResponseFieldPaths?.ToList();
        native.RequestMetadata = options.RequestMetadata?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        native.PromptVariables = options.PromptVariables?.ToDictionary(pair => pair.Key, pair => new PromptVariableValues { Text = pair.Value }, StringComparer.Ordinal);
        if (options.PerformanceLatency is { } latency) native.PerformanceConfig = new PerformanceConfiguration { Latency = latency };
        if (options.ServiceTier is { } tier) native.ServiceTier = new ServiceTier { Type = tier };
        if (options.Guardrail is { } guardrail)
        {
            native.GuardrailConfig = new GuardrailStreamConfiguration
            {
                GuardrailIdentifier = guardrail.Identifier,
                GuardrailVersion = guardrail.Version,
                Trace = guardrail.Trace,
                StreamProcessingMode = guardrail.StreamProcessingMode
            };
            if (guardrail.GuardLatestUserMessage) SelectLatestGuardContent(native.Messages);
        }
        if (options.Cache is { } cache) ApplyCacheProfile(native, request, options.ModelId, cache);
        else if (request.SystemPromptContent is not null)
        {
            if (options.CacheSystemPrompt && native.System is { Count: > 0 } && native.System.All(block => block.CachePoint is null))
                native.System.Add(new SystemContentBlock { CachePoint = Cache(options.CacheTtl) });
            if (options.CacheTools && native.ToolConfig is not null) native.ToolConfig.Tools.Add(new Tool { CachePoint = Cache(options.CacheTtl) });
            if (options.CacheLastMessage) ManageMessageCache(native.Messages, request.DynamicTrailingBlocks, options.CacheTtl);
        }
        else
        {
            if (options.CacheSystemPrompt && native.System is { Count: > 0 }) native.System.Add(new SystemContentBlock { CachePoint = Cache(options.CacheTtl) });
            if (options.CacheTools && native.ToolConfig is not null) native.ToolConfig.Tools.Add(new Tool { CachePoint = Cache(options.CacheTtl) });
            if (options.CacheLastMessage) ManageMessageCache(native.Messages, request.DynamicTrailingBlocks, options.CacheTtl);
        }
        return native;
    }

    private List<SystemContentBlock>? MapSystem(ModelRequest request)
    {
        if (request.SystemPromptContent is not { } content)
            return request.SystemPrompt is null ? null : [new SystemContentBlock { Text = request.SystemPrompt }];
        if (content.Count == 0) return null;
        return content.Select(block => block switch
        {
            TextBlock text => new SystemContentBlock { Text = text.Text },
            Strands.Agents.CachePointBlock point => new SystemContentBlock { CachePoint = Cache(point.Ttl) },
            GuardContentBlock guard => new SystemContentBlock { GuardContent = GuardContent(guard) },
            _ => throw new ModelException("Bedrock system content accepts only text, cache-point and guard blocks.")
        }).ToList();
    }

    private static readonly string[] ModelsRejectingReasoningText =
    [
        "amazon.nova-lite", "amazon.nova-pro", "google.gemma", "meta.llama", "mistral.devstral",
        "mistral.ministral", "mistral.mistral-large", "nvidia.nemotron-nano-9b", "nvidia.nemotron-super",
        "openai.gpt-5", "openai.gpt-6", "qwen.qwen3-235b", "qwen.qwen3-coder", "writer.palmyra", "xai.grok"
    ];

    private List<NativeMessage> MapFilteredMessages(IReadOnlyList<Strands.Agents.Message> messages, BedrockModelOptions options)
    {
        var mapped = new List<NativeMessage>();
        foreach (var message in messages)
        {
            var content = new List<NativeContent>();
            foreach (var block in message.Content)
                if (!RejectsReasoningBlock(block, options.ModelId)) content.Add(MapContent(block, options));
            if (content.Count > 0)
                mapped.Add(new NativeMessage { Role = message.Role == Role.User ? ConversationRole.User : ConversationRole.Assistant, Content = content });
        }
        return mapped;
    }

    private static bool RejectsReasoningBlock(Strands.Agents.ContentBlock block, string modelId)
    {
        if (block is not ReasoningBlock && block is not RedactedReasoningBlock) return false;
        // Preserve pinned Python precedence: DeepSeek precedes opaque blocks/IDs; Anthropic precedes opaque IDs.
        if (modelId.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (block is not ReasoningBlock reasoning) return false;
        var signed = !string.IsNullOrEmpty(reasoning.Signature);
        if (modelId.IndexOf("anthropic", StringComparison.OrdinalIgnoreCase) >= 0 || modelId.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
            return !signed;
        if (modelId.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) || modelId.IndexOf('.') < 0) return false;
        return ModelsRejectingReasoningText.Any(model => modelId.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) || signed;
    }

    private static void SelectLatestGuardContent(IReadOnlyList<NativeMessage> messages)
    {
        var selected = messages.LastOrDefault(message => message.Role == ConversationRole.User && message.Content.Any(block => block.Text is not null || block.Image is not null));
        if (selected is null) return;
        for (var index = 0; index < selected.Content.Count; index++)
        {
            var block = selected.Content[index];
            if (block.Text is { } text)
            {
                if (string.IsNullOrWhiteSpace(text)) SdkLogging.Warning("Bedrock latest-message guard selection preserves blank text because guardrails reject blank guard content.");
                else selected.Content[index] = new NativeContent { GuardContent = new GuardrailConverseContentBlock { Text = new GuardrailConverseTextBlock { Text = text } } };
            }
            else if (block.Image is { } image)
            {
                if (image.Format?.Value is "png" or "jpeg" && image.Source?.Bytes is { } bytes)
                    selected.Content[index] = new NativeContent { GuardContent = new GuardrailConverseContentBlock { Image = new GuardrailConverseImageBlock { Format = new GuardrailConverseImageFormat(image.Format.Value), Source = new GuardrailConverseImageSource { Bytes = bytes } } } };
                else SdkLogging.Warning("Bedrock latest-message guard selection leaves an image unwrapped because guardrails require inline PNG/JPEG bytes.");
            }
        }
    }

    private static void ApplyCacheProfile(ConverseStreamRequest native, ModelRequest request, string modelId, BedrockCacheOptions cache)
    {
        var enabled = cache.Strategy switch
        {
            BedrockCacheStrategy.Auto => modelId.IndexOf("anthropic", StringComparison.Ordinal) >= 0 || modelId.IndexOf("claude", StringComparison.Ordinal) >= 0,
            BedrockCacheStrategy.AutoIgnoreCase => ContainsAsciiIgnoreCase(modelId, "anthropic") || ContainsAsciiIgnoreCase(modelId, "claude"),
            BedrockCacheStrategy.Anthropic => true,
            _ => throw new ArgumentOutOfRangeException(nameof(cache), "Unknown Bedrock cache strategy.")
        };
        if (!enabled) return;
        if (cache.Tools && native.ToolConfig is not null)
            native.ToolConfig.Tools.Add(new Tool { CachePoint = Cache(ResolveCacheTtl(cache.ToolsTtl, cache.Ttl)) });
        if (cache.SystemPrompt && (request.SystemPromptContent is not null || !string.IsNullOrEmpty(request.SystemPrompt)) && native.System is { Count: > 0 })
        {
            var ttl = ResolveCacheTtl(cache.SystemPromptTtl, cache.Ttl);
            var toolsPoint = native.ToolConfig?.Tools.FirstOrDefault(tool => tool.CachePoint is not null)?.CachePoint;
            if (ttl is not null && cache.SystemPromptTtl is null && toolsPoint is not null &&
                !string.Equals(toolsPoint.Ttl?.Value, ttl, StringComparison.Ordinal)) ttl = null;
            if (native.System.All(block => block.CachePoint is null)) native.System.Add(new SystemContentBlock { CachePoint = Cache(ttl) });
            foreach (var block in native.System)
                if (block.CachePoint is { } point && string.IsNullOrEmpty(point.Ttl?.Value))
                    point.Ttl = ttl is null ? null : new CacheTTL(ttl);
        }
        if (cache.Messages) ManageMessageCache(native.Messages, request.DynamicTrailingBlocks, ResolveCacheTtl(cache.MessagesTtl, cache.Ttl));
    }

    private static string? ResolveCacheTtl(string? section, string? shared)
        => !string.IsNullOrEmpty(section) ? section : !string.IsNullOrEmpty(shared) ? shared : null;

    private static bool ContainsAsciiIgnoreCase(string value, string pattern)
    {
        for (var start = 0; start <= value.Length - pattern.Length; start++)
        {
            var index = 0;
            for (; index < pattern.Length; index++)
            {
                var letter = value[start + index];
                if (letter is >= 'A' and <= 'Z') letter = (char)(letter + ('a' - 'A'));
                if (letter != pattern[index]) break;
            }
            if (index == pattern.Length) return true;
        }
        return false;
    }

    private static void ManageMessageCache(IReadOnlyList<NativeMessage> messages, int dynamicTrailingBlocks, string? ttl)
    {
        var target = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].Role == ConversationRole.User) { target = index; break; }
        for (var index = 0; index < messages.Count; index++)
            if (index != target) messages[index].Content.RemoveAll(block => block.CachePoint is not null);
        if (target < 0) return;
        var content = messages[target].Content;
        var placed = content.FindIndex(block => block.CachePoint is not null);
        if (placed >= 0)
        {
            for (var index = content.Count - 1; index > placed; index--)
                if (content[index].CachePoint is not null) content.RemoveAt(index);
            if (HonorBoundary(content, placed, ttl) || content.Count == 0) return;
        }
        var boundary = content.Count - dynamicTrailingBlocks;
        if (dynamicTrailingBlocks == 0)
        {
            var document = content.FindIndex(IsNonPdfDocument);
            if (document >= 0) boundary = document;
        }
        if (boundary <= 0) return;
        content.Insert(boundary, new NativeContent { CachePoint = Cache(ttl) });
        HonorBoundary(content, boundary, ttl);
    }

    private static bool HonorBoundary(List<NativeContent> content, int placed, string? ttl)
    {
        var point = content[placed];
        if (placed == 0) { content.RemoveAt(placed); return false; }
        point.CachePoint.Ttl = string.IsNullOrEmpty(ttl) ? null : new CacheTTL(ttl);
        var boundary = placed;
        while (boundary > 0 && IsNonPdfDocument(content[boundary - 1])) boundary--;
        if (boundary == placed) return true;
        content.RemoveAt(placed);
        if (boundary == 0) return false;
        content.Insert(boundary, point);
        return true;
    }

    private static bool IsNonPdfDocument(NativeContent block) => block.Document is { } document && document.Format?.Value != "pdf";

    private NativeContent MapContent(Strands.Agents.ContentBlock block, BedrockModelOptions options) => block switch
    {
        TextBlock text => new NativeContent { Text = text.Text },
        CitationsBlock citations => new NativeContent { CitationsContent = BedrockCitations.Map(citations) },
        Strands.Agents.ToolUseBlock tool => new NativeContent { ToolUse = new NativeToolUse { ToolUseId = tool.ToolUseId, Name = tool.Name, Input = ToDocument(tool.Input) } },
        Strands.Agents.ToolResultBlock result => new NativeContent { ToolResult = MapResult(result, options) },
        ReasoningBlock reasoning => new NativeContent { ReasoningContent = new ReasoningContentBlock { ReasoningText = new ReasoningTextBlock { Text = reasoning.Text, Signature = options.FilterRejectedReasoningContent && string.IsNullOrEmpty(reasoning.Signature) ? null : reasoning.Signature } } },
        RedactedReasoningBlock redacted => new NativeContent { ReasoningContent = new ReasoningContentBlock { RedactedContent = Source(redacted.Data) } },
        Strands.Agents.ImageBlock image => new NativeContent { Image = Image(image) },
        Strands.Agents.AudioBlock audio => new NativeContent { Audio = new Amazon.BedrockRuntime.Model.AudioBlock { Format = AudioFormat(audio.MediaType), Source = audio.Location is { } audioLocation ? new AudioSource { S3Location = Location(audioLocation) } : new AudioSource { Bytes = Source(audio.Data) } } },
        Strands.Agents.VideoBlock video => new NativeContent { Video = Video(video) },
        GuardContentBlock guard => new NativeContent { GuardContent = GuardContent(guard) },
        Strands.Agents.DocumentBlock document => new NativeContent { Document = Document(document) },
        Strands.Agents.CachePointBlock cache => new NativeContent { CachePoint = Cache(cache.Ttl) },
        _ => throw new ModelException("Bedrock cannot represent conversation content '" + block.GetType().Name + "'.")
    };

    private NativeToolResult MapResult(Strands.Agents.ToolResultBlock result, BedrockModelOptions options)
    {
        var convertJson = options.ConvertNovaToolResultJsonToText && options.ModelId.IndexOf("amazon.nova", StringComparison.Ordinal) >= 0;
        var content = !result.HasExplicitContent && result.ContentBlocks.Count == 0 ? new List<ToolResultContentBlock> { new() { Text = result.Content } } : result.ContentBlocks.Select(block => MapResultContent(block, convertJson)).ToList();
        if (result.StructuredContent is { } structured) content.Add(MapResultJson(structured, convertJson));
        return new NativeToolResult
        {
            ToolUseId = result.ToolUseId,
            Content = content,
            Status = (options.IncludeToolResultStatus ?? options.ModelId.IndexOf("anthropic.claude", StringComparison.OrdinalIgnoreCase) >= 0) ? new Amazon.BedrockRuntime.ToolResultStatus(result.Status == Strands.Agents.ToolResultStatus.Error ? "error" : "success") : null
        };
    }

    private ToolResultContentBlock MapResultContent(Strands.Agents.ContentBlock block, bool convertJson) => block switch
    {
        TextBlock text => new ToolResultContentBlock { Text = text.Text },
        JsonBlock json => MapResultJson(json.Json, convertJson),
        Strands.Agents.ImageBlock image => new ToolResultContentBlock { Image = Image(image) },
        Strands.Agents.DocumentBlock document => new ToolResultContentBlock { Document = Document(document) },
        Strands.Agents.VideoBlock video => new ToolResultContentBlock { Video = Video(video) },
        _ => throw new ModelException("Bedrock cannot represent tool result content '" + block.GetType().Name + "'.")
    };

    // AWS omits a null Document union member; a null string retains presence and writes JSON null.
    // This value is only for marshalling, not general-purpose Document comparison or storage.
    private static ToolResultContentBlock MapResultJson(JsonElement json, bool convertJson) => convertJson
        ? new ToolResultContentBlock { Text = json.GetRawText() }
        : new ToolResultContentBlock { Json = json.ValueKind == JsonValueKind.Null ? new Document((string)null!) : ToDocument(json) };

    private Amazon.BedrockRuntime.Model.ImageBlock Image(Strands.Agents.ImageBlock image)
    {
        if (image.Url is not null) throw new ModelException("Bedrock image sources require bytes or an S3 location; HTTP URLs are unsupported.");
        return new()
        {
            Format = image.MediaType.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => new ImageFormat("jpeg"),
                "image/png" => new ImageFormat("png"),
                "image/gif" => new ImageFormat("gif"),
                "image/webp" => new ImageFormat("webp"),
                _ => throw new ModelException("Bedrock does not support image media type '" + image.MediaType + "'.")
            },
            Source = image.Location is { } location ? new ImageSource { S3Location = Location(location) } : new ImageSource { Bytes = Source(image.Data) }
        };
    }

    private Amazon.BedrockRuntime.Model.VideoBlock Video(Strands.Agents.VideoBlock video)
    {
        var format = video.MediaType.ToLowerInvariant() switch
        {
            "video/3gp" or "video/3g2" or "video/3gpp" or "video/3gpp2" or "video/three_gp" => "three_gp",
            "video/x-matroska" => "mkv",
            "video/quicktime" => "mov",
            "video/x-flv" => "flv",
            "video/x-ms-wmv" => "wmv",
            _ when video.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => video.MediaType.Substring(6).ToLowerInvariant(),
            _ => throw new ModelException("Bedrock requires a video media type.")
        };
        if (!new[] { "mkv", "mov", "mp4", "webm", "flv", "mpeg", "mpg", "wmv", "three_gp" }.Contains(format)) throw new ModelException("Bedrock does not support video media type '" + video.MediaType + "'.");
        return new()
        {
            Format = new VideoFormat(format),
            Source = video.Location is { } location ? new VideoSource { S3Location = Location(location) } : new VideoSource { Bytes = Source(video.Data) }
        };
    }

    private GuardrailConverseContentBlock GuardContent(GuardContentBlock guard) => guard.Image is { } image
        ? new() { Image = new GuardrailConverseImageBlock { Format = new GuardrailConverseImageFormat(image.MediaType == "image/png" ? "png" : "jpeg"), Source = new GuardrailConverseImageSource { Bytes = Source(image.Data) } } }
        : new() { Text = new GuardrailConverseTextBlock { Text = guard.Text, Qualifiers = guard.Qualifiers.Select(value => value switch { GuardQualifier.GroundingSource => "grounding_source", GuardQualifier.Query => "query", GuardQualifier.GuardContent => "guard_content", _ => throw new ModelException("Unknown guard qualifier.") }).ToList() } };

    private static Amazon.BedrockRuntime.Model.S3Location Location(Strands.Agents.S3Location location) => new() { Uri = location.Uri, BucketOwner = location.BucketOwner };

    private static Amazon.BedrockRuntime.AudioFormat AudioFormat(string mediaType)
    {
        var format = mediaType.ToLowerInvariant() switch
        {
            "audio/x-wav" or "audio/wave" => "wav",
            "audio/x-m4a" => "m4a",
            _ when mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => mediaType.Substring(6).ToLowerInvariant(),
            _ => throw new ModelException("Bedrock requires an audio media type.")
        };
        if (!new[] { "aac", "flac", "m4a", "mka", "mkv", "mp3", "mp4", "mpeg", "mpga", "ogg", "opus", "pcm", "wav", "webm", "x-aac" }.Contains(format))
            throw new ModelException("Bedrock does not support audio media type '" + mediaType + "'.");
        return new(format);
    }

    private Amazon.BedrockRuntime.Model.DocumentBlock Document(Strands.Agents.DocumentBlock document) => new()
    {
        Name = document.Name ?? "Document",
        Citations = document.CitationsEnabled is { } enabled ? new CitationsConfig { Enabled = enabled } : null,
        Context = document.Context,
        Format = new DocumentFormat(document.MediaType.ToLowerInvariant() switch
        {
            "application/pdf" => "pdf",
            "text/csv" => "csv",
            "application/msword" => "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "text/html" => "html",
            "text/markdown" => "md",
            "text/plain" => "txt",
            "application/vnd.ms-excel" => "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            _ => throw new ModelException("Bedrock does not support document media type '" + document.MediaType + "'.")
        }),
        Source = document.Location is { } location ? new DocumentSource { S3Location = Location(location) }
            : document.Content is { } content ? new DocumentSource { Content = content.Select(block => new DocumentContentBlock { Text = block.Text }).ToList() }
            : new DocumentSource { Bytes = document.Text is { } text ? Source(Encoding.UTF8.GetBytes(text)) : Source(document.Data) }
    };

    private static Amazon.BedrockRuntime.Model.CachePointBlock Cache(string? ttl) => new() { Type = CachePointType.Default, Ttl = ttl is null ? null : new CacheTTL(ttl) };
    private MemoryStream Source(string data)
        => Source(Convert.FromBase64String(data));
    private MemoryStream Source(byte[] data)
    {
        var source = new MemoryStream(data, writable: false);
        _sources.Add(source);
        return source;
    }
    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
    private static Document ToDocument(JsonElement json) => JsonSerializer.Deserialize<Document>(json.GetRawText());

    private static ToolSpecification MapToolSpecification(ToolSpec tool, bool strict)
    {
        var schema = tool.InputSchema;
        if (strict)
        {
            schema = BedrockStrictSchema.Apply(schema, out var warnings);
            if (warnings.Count > 0) SdkLogging.Warning("Bedrock strict tool '{Tool}' retains unsupported or unresolved schema keywords: {Keywords}.", tool.Name, string.Join(", ", warnings));
        }
        return new() { Name = tool.Name, Description = tool.Description, InputSchema = new() { Json = ToDocument(schema) }, Strict = strict ? true : null };
    }
}
