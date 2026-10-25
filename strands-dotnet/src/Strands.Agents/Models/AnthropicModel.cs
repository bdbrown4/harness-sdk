using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A native streaming adapter for Anthropic's Messages API, including signed thinking and tool calls.</summary>
/// <remarks>The caller owns the HttpClient. Provider pause_turn responses continue up to ten times; failed requests are never automatically retried.</remarks>
public sealed class AnthropicModel : IModel, IModelMetadataProvider, IModelTokenCounter, IConfigurableModel<AnthropicModelOptions>
{
    /// <inheritdoc />
    public ModelMetadata Metadata { get { var options = GetConfig(); return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    private readonly HttpClient _client;
    private readonly AnthropicModelOptions _options;
    private readonly Uri _endpoint;
    private readonly JsonElement? _parameters;
    private readonly string[] _betas;
    private readonly string[]? _stopSequences;
    private readonly JsonElement[] _serverTools;
    private AnthropicModel? _configured;
    /// <inheritdoc/>
    public AnthropicModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with
        {
            Params = current._parameters,
            Betas = Array.AsReadOnly(current._betas),
            StopSequences = current._stopSequences is null ? null : Array.AsReadOnly(current._stopSequences),
            AnthropicTools = Array.AsReadOnly(current._serverTools)
        };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<AnthropicModelOptions, AnthropicModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new AnthropicModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }

    /// <summary>Creates a Messages adapter using a caller-owned HTTP client.</summary>
    /// <param name="client">The HTTP client used for every request; the adapter never disposes it.</param>
    /// <param name="options">The explicit credentials, model identifier, endpoint, and generation settings.</param>
    /// <exception cref="ArgumentNullException">The client or options is null.</exception>
    /// <exception cref="ArgumentException">Credentials, the model identifier, API version, or endpoint is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The token limit, thinking budget, or temperature is outside its supported range.</exception>
    public AnthropicModel(HttpClient client, AnthropicModelOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        Guard.NotNullOrWhiteSpace(options.ApiVersion, nameof(options.ApiVersion));
        if (options.MaxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive.");
        if (options.ThinkingBudgetTokens is { } budget && (budget < 1024 || budget >= options.MaxTokens))
            throw new ArgumentOutOfRangeException(nameof(options), "ThinkingBudgetTokens must be at least 1024 and less than MaxTokens.");
        if (options.Temperature is { } temperature && (!Guard.IsFinite(temperature) || temperature is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Temperature must be between 0 and 1.");
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        _parameters = ProviderConfiguration.Snapshot(options.Params);
        _serverTools = options.AnthropicTools?.Select(tool =>
        {
            if (tool.ValueKind != JsonValueKind.Object || tool.TryGetProperty("input_schema", out _))
                throw new ArgumentException("AnthropicTools accepts server tools only; register function tools through the agent tool registry.", nameof(options));
            return tool.Clone();
        }).ToArray() ?? throw new ArgumentNullException(nameof(options.AnthropicTools));
        _betas = options.Betas?.ToArray() ?? throw new ArgumentNullException(nameof(options.Betas));
        if (_betas.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Beta identifiers cannot be empty.", nameof(options));
        _stopSequences = options.StopSequences?.ToArray();
        if (_stopSequences?.Any(string.IsNullOrEmpty) == true) throw new ArgumentException("Stop sequences cannot be empty.", nameof(options));
        _client = client;
        _options = options;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "messages", "https://api.anthropic.com/v1/messages");
    }

    /// <summary>Counts with the provider when enabled, otherwise using the local input estimate.</summary>
    /// <remarks>Native count responses are limited to 64 KiB. Recoverable count failures use the estimate; cancellation and owned cleanup failures remain errors.</remarks>
    public async ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        Guard.NotNull(request.Messages, nameof(request.Messages));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured) return await configured.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
        if (!_options.UseNativeTokenCount) return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        var cleanupFailed = false;
        try
        {
            var formatted = FormatRequest(request);
            var body = new Dictionary<string, object?>();
            foreach (var field in new[] { "model", "messages", "system", "tools", "tool_choice" })
                if (formatted.TryGetValue(field, out var fieldValue)) body[field] = fieldValue;
            var endpoint = new UriBuilder(_endpoint);
            endpoint.Path = endpoint.Path.TrimEnd('/') + "/count_tokens";
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, endpoint.Uri) { Content = ProviderHttp.JsonContent(body) };
            outgoing.Headers.Add("x-api-key", _options.ApiKey);
            outgoing.Headers.Add("anthropic-version", _options.ApiVersion);
            if (_betas.Length > 0) outgoing.Headers.Add("anthropic-beta", string.Join(",", _betas));
            outgoing.Headers.Accept.Add(new("application/json"));
            cancellationToken.ThrowIfCancellationRequested();
            var response = await ProviderHttp.SendAsync(_client, outgoing, "Anthropic", cancellationToken, ["application/json"]).ConfigureAwait(false);
            Exception? primary = null;
            try
            {
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                using var document = await ProviderHttp.ReadJsonAsync(stream, "Anthropic token count", 64 * 1024, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("input_tokens", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
                    throw new ModelException("Anthropic count_tokens must return a nonnegative integer input_tokens.");
                cancellationToken.ThrowIfCancellationRequested();
                return count;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { response.Dispose(); }
                catch (Exception cleanup)
                {
                    cleanupFailed = true;
                    if (primary is null) throw;
                    AttachCleanup(primary, cleanup);
                }
            }
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException || cancellationToken.IsCancellationRequested || cleanupFailed || error.Data["Strands.CleanupErrors"] is not null) throw;
            return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        }
    }

    /// <summary>Streams indexed text, thinking/signature, tool input, usage, and stop events.</summary>
    /// <param name="request">The conversation, system instructions, and available tool specifications.</param>
    /// <param name="cancellationToken">Cancellation for the HTTP request and incremental response reads.</param>
    /// <returns>Model events ending with usage and the provider's final stop reason.</returns>
    /// <exception cref="ModelException">The provider fails or its stream is incomplete or unsupported.</exception>
    /// <exception cref="NotSupportedException">A request contains a block that the adapter cannot represent.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        var body = FormatRequest(request);
        var offset = 0;
        var usage = new Usage();
        for (var continuations = 0; ; continuations++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new StreamState(offset);
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(body) })
            {
                httpRequest.Headers.Add("x-api-key", _options.ApiKey);
                httpRequest.Headers.Add("anthropic-version", _options.ApiVersion);
                if (_betas.Length > 0) httpRequest.Headers.Add("anthropic-beta", string.Join(",", _betas));
                httpRequest.Headers.Accept.Add(new("text/event-stream"));
                var response = await ProviderHttp.SendAsync(_client, httpRequest, "Anthropic", cancellationToken).ConfigureAwait(false);
                IAsyncEnumerator<SseFrame>? frames = null;
                Exception? primary = null;
                try
                {
                    try
                    {
                        var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                        frames = ServerSentEvents.ReadAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    }
                    catch (Exception error) { primary = error; throw; }
                    while (true)
                    {
                        IReadOnlyList<ModelStreamEvent> events;
                        try
                        {
                            if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new ModelException("Anthropic stream ended without message_stop.");
                            var frame = frames.Current;
                            using var document = ProviderHttp.Parse(frame.Data, "Anthropic");
                            var root = document.RootElement;
                            var type = ProviderHttp.RequiredString(root, "type");
                            if (frame.Event is { Length: > 0 } && frame.Event != type)
                                throw new ModelException("Anthropic event name does not match its data type.");
                            events = state.Process(type, root);
                        }
                        catch (Exception error) { primary = error; throw; }
                        foreach (var item in events)
                            if (continuations == 0 || item is not ModelMessageStartEvent) yield return item;
                        if (state.Completed) break;
                    }
                }
                finally
                {
                    Exception? cleanup = null;
                    try { if (frames is not null) await frames.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { cleanup = error; }
                    try { response.Dispose(); }
                    catch (Exception error) { if (cleanup is null) cleanup = error; else AttachCleanup(cleanup, error); }
                    if (cleanup is not null)
                    {
                        if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                        else AttachCleanup(primary, cleanup);
                    }
                }
            }
            if (!state.Completed) throw new ModelException("Anthropic stream ended without message_stop.");
            try { usage += state.Usage; }
            catch (OverflowException error) { throw new ModelException("Anthropic continuation usage exceeds the supported token-count range.", error); }
            if (state.StopReason != "pause_turn")
            {
                yield return new ModelMetadataEvent(usage);
                yield return new ModelMessageStopEvent(state.StopReason!);
                yield break;
            }
            if (continuations >= 10) throw new ModelException("Anthropic server-side tool turn did not complete after 10 continuations.");
            var messages = (List<Dictionary<string, object?>>)body["messages"]!;
            messages.Add(new() { ["role"] = "assistant", ["content"] = state.NativeContent() });
            offset = state.NextIndex;
        }
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        ProviderConfiguration.ValidateToolChoice(request);
        var cache = _options.CacheConfig;
        var target = -1;
        if (cache?.Messages == true)
            for (var index = request.Messages.Count - 1; index >= 0; index--)
                if (request.Messages[index].Role == Role.User && request.Messages[index].Content.Any(block => block is not CachePointBlock)) { target = index; break; }
        if (request.DynamicTrailingBlocks < 0 || target >= 0 && request.DynamicTrailingBlocks > request.Messages[target].Content.Count(block => block is not CachePointBlock))
            throw new ArgumentOutOfRangeException(nameof(request), "The dynamic suffix must fit within the final user message content.");
        var messages = request.Messages.Select((message, index) => new Dictionary<string, object?>
        {
            ["role"] = message.Role == Role.User ? "user" : "assistant",
            ["content"] = FormatContent(message.Content, message.Role, cache, index == target, index == target ? request.DynamicTrailingBlocks : 0)
        }).ToList();
        var body = ProviderConfiguration.Parameters(_parameters);
        body["model"] = _options.ModelId;
        body["max_tokens"] = _options.MaxTokens;
        body["messages"] = messages;
        body["stream"] = true;
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            body["system"] = cache?.SystemPrompt == true
                ? new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = request.SystemPrompt, ["cache_control"] = ProviderConfiguration.CacheControl(cache.SystemPromptTtl ?? cache.Ttl) } }
                : request.SystemPrompt;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.ThinkingBudgetTokens is { } budget) body["thinking"] = new { type = "enabled", budget_tokens = budget };
        if (_options.TopP is { } topP) body["top_p"] = topP;
        if (_stopSequences is not null) body["stop_sequences"] = _stopSequences;
        var tools = request.Tools?.Select(tool => new Dictionary<string, object?> { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = tool.InputSchema }).ToList() ?? [];
        if (request.ToolChoice?.Mode is null or ToolChoiceMode.Auto)
            tools.AddRange(_serverTools.Select(tool => ProviderConfiguration.Parameters(tool)));
        if (_parameters is { } parameters && parameters.TryGetProperty("tools", out var nativeTools) && request.ToolChoice?.Mode is null or ToolChoiceMode.Auto)
        {
            if (nativeTools.ValueKind != JsonValueKind.Array) throw new ArgumentException("Anthropic Params.tools must be an array.", nameof(request));
            foreach (var tool in nativeTools.EnumerateArray()) tools.Add(ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(tool)));
        }
        body.Remove("tools");
        if (tools.Count > 0)
        {
            if (cache?.Tools == true) tools[tools.Count - 1]["cache_control"] = ProviderConfiguration.CacheControl(cache.ToolsTtl ?? cache.Ttl);
            body["tools"] = tools;
        }
        if (request.ToolChoice is { } choice) body["tool_choice"] = choice.Mode switch
        {
            ToolChoiceMode.Auto => new { type = "auto" },
            ToolChoiceMode.Any => new { type = "any" },
            ToolChoiceMode.None => new { type = "none" },
            ToolChoiceMode.Tool => (object)new { type = "tool", name = choice.Name },
            _ => throw new ArgumentException("Unknown tool-choice mode.", nameof(request))
        };
        return body;
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(value => ReferenceEquals(value, cleanup))) errors.Add(cleanup);
    }

    private static IReadOnlyList<Dictionary<string, object?>> FormatContent(IReadOnlyList<ContentBlock> blocks, Role role, CacheConfig? cache, bool target, int skipTrailing = 0)
    {
        var content = new List<Dictionary<string, object?>>();
        var placed = false;
        var honored = false;
        foreach (var block in blocks)
        {
            if (block is CachePointBlock point)
            {
                if (cache?.Messages != true) MarkCache(content, point.Ttl);
                else if (target && !honored)
                {
                    honored = true;
                    placed = MarkCache(content, point.Ttl ?? cache.MessagesTtl ?? cache.Ttl);
                }
                continue;
            }
            content.Add(FormatBlock(block, role));
        }
        if (target && !placed) MarkCache(content, cache?.MessagesTtl ?? cache?.Ttl, skipTrailing);
        return content;
    }

    private static bool MarkCache(IReadOnlyList<Dictionary<string, object?>> content, string? ttl, int skipTrailing = 0)
    {
        for (var index = content.Count - 1 - skipTrailing; index >= 0; index--)
        {
            if (content[index]["type"] is not string type || type is "thinking" or "redacted_thinking") continue;
            content[index]["cache_control"] = ProviderConfiguration.CacheControl(ttl);
            return true;
        }
        return false;
    }

    private static Dictionary<string, object?> FormatBlock(ContentBlock block, Role role)
    {
        switch (block)
        {
            case CitationsBlock citations:
                if (citations.ProviderMetadata is { } metadata && metadata.TryGetProperty("anthropic", out var source))
                {
                    var replay = new AnthropicContentBlock(source);
                    if (role != Role.Assistant || source.GetProperty("type").GetString() != "text" || replay.Text != citations.Text)
                        throw new ArgumentException("Anthropic citation replay must match its assistant text.", nameof(block));
                    return replay.Content.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
                }
                return new() { ["type"] = "text", ["text"] = citations.Text };
            case AnthropicContentBlock native when role == Role.Assistant:
                return native.Content.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
            case TextBlock text: return new() { ["type"] = "text", ["text"] = text.Text };
            case JsonBlock json: return new() { ["type"] = "text", ["text"] = json.Json.GetRawText() };
            case ImageBlock image:
                ProviderConfiguration.DataUrl(image);
                return new() { ["type"] = "image", ["source"] = image.Url is { } url ? new { type = "url", url = url.AbsoluteUri } : (object)new { type = "base64", media_type = image.MediaType, data = MediaContent.InlineData(image, "Anthropic") } };
            case DocumentBlock document when document.MediaType == "application/pdf":
                return FormatDocument(document, new { type = "base64", media_type = document.MediaType, data = MediaContent.InlineData(document, "Anthropic") });
            case DocumentBlock document when document.MediaType.StartsWith("text/", StringComparison.Ordinal) && (document.CitationsEnabled is not null || document.Context is not null):
                return FormatDocument(document, new { type = "text", media_type = "text/plain", data = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(MediaContent.InlineData(document, "Anthropic"))) });
            case DocumentBlock document when document.MediaType.StartsWith("text/", StringComparison.Ordinal):
                return new() { ["type"] = "text", ["text"] = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(MediaContent.InlineData(document, "Anthropic"))) };
            case ResourceLinkBlock link:
                return new() { ["type"] = "text", ["text"] = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, description = link.Description, mimeType = link.MediaType }) };
            case ToolUseBlock call when role == Role.Assistant && call.Input.ValueKind == JsonValueKind.Object:
                return new() { ["type"] = "tool_use", ["id"] = call.ToolUseId, ["name"] = call.Name, ["input"] = call.Input };
            case ToolResultBlock result when role == Role.User:
                var content = result.ContentBlocks.Count > 0 ? FormatContent(result.ContentBlocks, Role.User, null, false).ToList() : [];
                if (result.ContentBlocks.Count == 0 && !result.HasExplicitContent) content.Add(FormatBlock(new TextBlock(result.Content), Role.User));
                if (result.StructuredContent is { } structured && !result.ContentBlocks.OfType<JsonBlock>().Any(json => json.Json.GetRawText() == structured.GetRawText())) content.Add(FormatBlock(new JsonBlock(structured), Role.User));
                object value = content.Count == 1 && (string?)content[0]["type"] == "text" ? content[0]["text"]! : content;
                return new() { ["type"] = "tool_result", ["tool_use_id"] = result.ToolUseId, ["content"] = value, ["is_error"] = result.Status == ToolResultStatus.Error };
            case ReasoningBlock thinking when role == Role.Assistant && !string.IsNullOrEmpty(thinking.Signature):
                return new() { ["type"] = "thinking", ["thinking"] = thinking.Text, ["signature"] = thinking.Signature };
            case RedactedReasoningBlock redacted when role == Role.Assistant:
                return new() { ["type"] = "redacted_thinking", ["data"] = redacted.Data };
            default: throw new NotSupportedException($"Anthropic cannot represent {block.GetType().Name} in a {role} message. Thinking replay requires a signature.");
        }
    }

    private static Dictionary<string, object?> FormatDocument(DocumentBlock document, object source)
    {
        var content = new Dictionary<string, object?> { ["type"] = "document", ["source"] = source };
        if (document.Name is not null) content["title"] = document.Name;
        if (document.Context is not null) content["context"] = document.Context;
        if (document.CitationsEnabled is { } enabled) content["citations"] = new { enabled };
        return content;
    }

    private sealed class StreamState(int offset)
    {
        private readonly Dictionary<int, BlockState> _blocks = [];
        private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
        private bool _started;
        private string? _stopReason;
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheWrite;
        internal bool Completed { get; private set; }
        internal string? StopReason => _stopReason;
        internal Usage Usage { get; private set; } = new();
        internal int NextIndex => _blocks.Count == 0 ? offset : checked(offset + _blocks.Keys.Max() + 1);
        internal JsonElement[] NativeContent() => _blocks.OrderBy(value => value.Key).Select(value => value.Value.NativeContent()).ToArray();

        internal IReadOnlyList<ModelStreamEvent> Process(string type, JsonElement root)
        {
            var events = new List<ModelStreamEvent>();
            if (type == "error") throw ProviderHttp.CreateError("Anthropic", root);
            if (type == "ping") return events;
            if (type == "message_start")
            {
                if (_started) throw new ModelException("Anthropic sent duplicate message_start events.");
                var message = ProviderHttp.RequiredObject(root, "message");
                if (ProviderHttp.RequiredString(message, "role") != "assistant") throw new ModelException("Anthropic returned a non-assistant response.");
                if (message.TryGetProperty("content", out var content) && (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 0))
                    throw new ModelException("Anthropic message_start contained unsupported initial content.");
                _started = true;
                if (message.TryGetProperty("usage", out var usage)) UpdateUsage(usage);
                events.Add(new ModelMessageStartEvent());
                return events;
            }
            if (!_started) throw new ModelException("Anthropic sent an event before message_start.");
            switch (type)
            {
                case "content_block_start": StartBlock(root, events); break;
                case "content_block_delta": AddDelta(root, events); break;
                case "content_block_stop": StopBlock(root, events); break;
                case "message_delta":
                    var delta = ProviderHttp.RequiredObject(root, "delta");
                    if (ProviderHttp.OptionalString(delta, "stop_reason") is { } reason)
                    {
                        if (_stopReason is not null) throw new ModelException("Anthropic sent more than one stop reason.");
                        if (_blocks.Values.Any(block => !block.Stopped)) throw new ModelException("Anthropic stopped a message with unfinished content blocks.");
                        _stopReason = reason;
                    }
                    if (root.TryGetProperty("usage", out var usage)) UpdateUsage(usage);
                    break;
                case "message_stop":
                    if (_stopReason is null || _blocks.Values.Any(block => !block.Stopped))
                        throw new ModelException("Anthropic message_stop arrived before the response completed.");
                    long total;
                    try { total = checked(_input + _output + _cacheRead + _cacheWrite); }
                    catch (OverflowException error) { throw new ModelException("Anthropic usage exceeds the supported token-count range.", error); }
                    Usage = new(_input, _output, _cacheRead, _cacheWrite, total);
                    Completed = true;
                    break;
                default: throw new ModelException($"Anthropic event type '{type}' is not supported.");
            }
            return events;
        }

        private void StartBlock(JsonElement root, List<ModelStreamEvent> events)
        {
            if (_stopReason is not null) throw new ModelException("Anthropic sent content after its stop reason.");
            var index = ProviderHttp.RequiredIndex(root);
            if (_blocks.ContainsKey(index)) throw new ModelException("Anthropic reused a content block index.");
            var content = ProviderHttp.RequiredObject(root, "content_block");
            var type = ProviderHttp.RequiredString(content, "type");
            var block = new BlockState(type, content);
            ToolUseBlock? toolUse = null;
            if (type == "tool_use")
            {
                var id = ProviderHttp.RequiredString(content, "id");
                if (!_toolIds.Add(id)) throw new ModelException("Anthropic returned duplicate tool call identifiers.");
                var name = ProviderHttp.RequiredString(content, "name");
                var input = ProviderHttp.RequiredObject(content, "input");
                toolUse = new(id, name, ProviderHttp.EmptyObject);
                if (input.EnumerateObject().Any()) block.Input.Append(input.GetRawText());
            }
            else if (block.Server)
            {
                if (type is "server_tool_use" or "mcp_tool_use")
                {
                    var id = ProviderHttp.RequiredString(content, "id");
                    if (!_toolIds.Add(id)) throw new ModelException("Anthropic returned duplicate tool call identifiers.");
                    _ = ProviderHttp.RequiredString(content, "name");
                    if (content.TryGetProperty("input", out var input))
                    {
                        if (input.ValueKind != JsonValueKind.Object) throw new ModelException("Anthropic server-tool input must be an object.");
                        if (input.EnumerateObject().Any()) block.Input.Append(input.GetRawText());
                    }
                }
            }
            else if (type is not ("text" or "thinking" or "redacted_thinking")) throw new ModelException($"Anthropic content type '{type}' is not supported.");
            _blocks.Add(index, block);
            if (block.Server) return;
            var emittedIndex = checked(offset + index);
            events.Add(type == "redacted_thinking"
                ? new ModelContentBlockStartEvent(emittedIndex) { ContentBlock = new RedactedReasoningBlock(ProviderHttp.RequiredString(content, "data")) }
                : new ModelContentBlockStartEvent(emittedIndex, toolUse));
            if (type == "text" && ProviderHttp.OptionalString(content, "text") is { Length: > 0 } text)
            {
                block.Text.Append(text);
                events.Add(new ModelContentBlockDeltaEvent(emittedIndex, Text: text));
            }
            if (type == "text")
            {
                var citations = block.NormalizedCitations();
                if (citations.Count > 0) events.Add(new ModelContentBlockDeltaEvent(emittedIndex) { Citations = citations });
            }
            if (type == "thinking")
            {
                var thinking = ProviderHttp.OptionalString(content, "thinking");
                var signature = ProviderHttp.OptionalString(content, "signature");
                if (!string.IsNullOrEmpty(signature)) block.HasSignature = true;
                block.Text.Append(thinking); block.Signature.Append(signature);
                if (!string.IsNullOrEmpty(thinking) || !string.IsNullOrEmpty(signature))
                    events.Add(new ModelContentBlockDeltaEvent(emittedIndex, Reasoning: thinking, Signature: signature));
            }
            if (block.Input.Length > 0) events.Add(new ModelContentBlockDeltaEvent(emittedIndex, ToolInput: block.Input.ToString()));
        }

        private void AddDelta(JsonElement root, List<ModelStreamEvent> events)
        {
            var index = ProviderHttp.RequiredIndex(root);
            var block = ActiveBlock(index);
            var delta = ProviderHttp.RequiredObject(root, "delta");
            var type = ProviderHttp.RequiredString(delta, "type");
            var emittedIndex = checked(offset + index);
            switch (type, block.Type)
            {
                case ("text_delta", "text"):
                    var text = ProviderHttp.OptionalString(delta, "text") ?? throw new ModelException("Anthropic text delta is missing text.");
                    block.Text.Append(text);
                    events.Add(new ModelContentBlockDeltaEvent(emittedIndex, Text: text));
                    break;
                case ("citations_delta", "text"):
                    var rawCitation = ProviderHttp.RequiredObject(delta, "citation");
                    block.Citations.Add(rawCitation.Clone());
                    if (AnthropicCitations.Format(rawCitation) is { } citation)
                        events.Add(new ModelContentBlockDeltaEvent(emittedIndex) { Citations = [citation] });
                    break;
                case ("thinking_delta", "thinking"):
                    var thinking = ProviderHttp.OptionalString(delta, "thinking") ?? throw new ModelException("Anthropic thinking delta is missing thinking.");
                    block.Text.Append(thinking);
                    events.Add(new ModelContentBlockDeltaEvent(emittedIndex, Reasoning: thinking));
                    break;
                case ("signature_delta", "thinking"):
                    var signature = ProviderHttp.RequiredString(delta, "signature");
                    block.HasSignature = true;
                    block.Signature.Append(signature);
                    events.Add(new ModelContentBlockDeltaEvent(emittedIndex, Signature: signature));
                    break;
                case ("input_json_delta", "tool_use"):
                case ("input_json_delta", "server_tool_use"):
                case ("input_json_delta", "mcp_tool_use"):
                    var json = ProviderHttp.OptionalString(delta, "partial_json") ?? throw new ModelException("Anthropic tool delta is missing partial_json.");
                    block.Input.Append(json);
                    if (!block.Server) events.Add(new ModelContentBlockDeltaEvent(emittedIndex, ToolInput: json));
                    break;
                default: throw new ModelException($"Anthropic delta '{type}' cannot be represented in a '{block.Type}' block.");
            }
        }

        private void StopBlock(JsonElement root, List<ModelStreamEvent> events)
        {
            var index = ProviderHttp.RequiredIndex(root);
            var block = ActiveBlock(index);
            if (block.Type is "tool_use" or "server_tool_use" or "mcp_tool_use") ProviderHttp.ValidateToolInput(block.Input.Length == 0 ? "{}" : block.Input.ToString(), "Anthropic");
            if (block.Type == "thinking" && !block.HasSignature) throw new ModelException("Anthropic thinking block ended without a replay signature.");
            block.Stopped = true;
            var emittedIndex = checked(offset + index);
            if (block.Server)
            {
                events.Add(new ModelContentBlockStartEvent(emittedIndex) { ContentBlock = new AnthropicContentBlock(block.NativeContent()) });
                events.Add(new ModelContentBlockStopEvent(emittedIndex));
            }
            else
            {
                ContentBlock? completed = null;
                if (block.Type == "text" && block.Citations.Count > 0)
                {
                    var normalized = block.NormalizedCitations();
                    completed = normalized.Count > 0
                        ? new CitationsBlock(normalized, block.Text.Length == 0 ? [] : [new(block.Text.ToString())], JsonSerializer.SerializeToElement(new { anthropic = block.NativeContent() }))
                        : new AnthropicContentBlock(block.NativeContent());
                }
                events.Add(new ModelContentBlockStopEvent(emittedIndex) { ContentBlock = completed });
            }
        }

        private BlockState ActiveBlock(int index)
        {
            if (!_blocks.TryGetValue(index, out var block) || block.Stopped || _stopReason is not null)
                throw new ModelException("Anthropic referenced an inactive content block.");
            return block;
        }

        private void UpdateUsage(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new ModelException("Anthropic usage must be an object.");
            _input = ProviderHttp.TokenCount(usage, "input_tokens", _input);
            _output = ProviderHttp.TokenCount(usage, "output_tokens", _output);
            _cacheRead = ProviderHttp.TokenCount(usage, "cache_read_input_tokens", _cacheRead);
            _cacheWrite = ProviderHttp.TokenCount(usage, "cache_creation_input_tokens", _cacheWrite);
        }
    }

    private sealed class BlockState
    {
        private readonly Dictionary<string, object?> _native;
        internal BlockState(string type, JsonElement content)
        {
            Type = type;
            _native = content.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
            if (type == "text" && content.TryGetProperty("citations", out var citations))
            {
                if (citations.ValueKind != JsonValueKind.Array || citations.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object))
                    throw new ModelException("Anthropic citations must be an array of objects.");
                Citations.AddRange(citations.EnumerateArray().Select(value => value.Clone()));
            }
        }
        internal string Type { get; }
        internal bool Server => AnthropicContentBlock.IsServerContent(Type);
        internal StringBuilder Input { get; } = new();
        internal StringBuilder Text { get; } = new();
        internal StringBuilder Signature { get; } = new();
        internal List<JsonElement> Citations { get; } = [];
        internal bool HasSignature { get; set; }
        internal bool Stopped { get; set; }
        internal IReadOnlyList<Citation> NormalizedCitations() => Citations.Select(AnthropicCitations.Format).OfType<Citation>().ToArray();
        internal JsonElement NativeContent()
        {
            var content = new Dictionary<string, object?>(_native, StringComparer.Ordinal);
            if (Type == "text")
            {
                content["text"] = Text.ToString();
                if (Citations.Count > 0) content["citations"] = Citations;
            }
            if (Type == "thinking") { content["thinking"] = Text.ToString(); content["signature"] = Signature.ToString(); }
            if (Type is "tool_use" or "server_tool_use" or "mcp_tool_use")
                content["input"] = Input.Length == 0 ? ProviderHttp.EmptyObject : JsonSerializer.Deserialize<JsonElement>(Input.ToString());
            return JsonSerializer.SerializeToElement(content);
        }
    }
}
