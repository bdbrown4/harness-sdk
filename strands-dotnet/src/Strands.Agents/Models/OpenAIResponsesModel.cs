using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A native streaming adapter for OpenAI's Responses API using a caller-owned HttpClient.</summary>
public sealed partial class OpenAIResponsesModel : IStatefulModel, IModelMetadataProvider, IConfigurableModel<OpenAIResponsesModelOptions>
{
    /// <inheritdoc/>
    public bool Stateful => _options.Stateful;
    private readonly HttpClient _client;
    private readonly OpenAIResponsesModelOptions _options;
    private readonly Uri _endpoint;
    private readonly JsonElement? _parameters;
    private readonly JsonElement? _format;
    private readonly JsonElement? _reasoning;
    private OpenAIResponsesModel? _configured;
    /// <inheritdoc/>
    public OpenAIResponsesModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with { Params = current._parameters, TextFormat = current._format, Reasoning = current._reasoning };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<OpenAIResponsesModelOptions, OpenAIResponsesModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new OpenAIResponsesModel(_client, update(GetConfig()));
        if (replacement.Stateful != Stateful) throw new ArgumentException("Changing server-history ownership requires a new Responses adapter and agent.", nameof(update));
        Interlocked.Exchange(ref _configured, replacement);
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata { get { var current = Volatile.Read(ref _configured) ?? this; return new(current._options.ModelId, MaxOutputTokens: current._options.MaxTokens ?? current.NativeMaxTokens()); } }
    /// <summary>Creates a Responses adapter without taking transport ownership.</summary>
    public OpenAIResponsesModel(HttpClient client, OpenAIResponsesModelOptions options)
    {
        Guard.NotNull(client, nameof(client)); Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.ApiKeyProvider is null) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        if (options.MaxTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxResponseBytes));
        ProviderConfiguration.Range(options.Temperature, 0, 2, nameof(options.Temperature));
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        _parameters = ProviderConfiguration.Snapshot(options.Params);
        if (options.Stateful && _parameters is { } parameters && parameters.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("Stateful Responses uses response identifiers and cannot also select a conversation.", nameof(options));
        _format = ProviderConfiguration.Snapshot(options.TextFormat);
        _reasoning = ProviderConfiguration.Snapshot(options.Reasoning);
        if (_format is { } format)
        {
            var kind = ProviderHttp.RequiredString(format, "type");
            if (kind is not ("text" or "json_object" or "json_schema")) throw new ArgumentException("Unsupported Responses text format.", nameof(options));
            if (kind == "json_schema") { _ = ProviderHttp.RequiredString(format, "name"); _ = ProviderHttp.RequiredObject(format, "schema"); }
        }
        if (options.CacheConfig?.Ttl is { } ttl && ttl is not ("in_memory" or "24h")) throw new ArgumentException("Responses cache TTL must be in_memory or 24h.", nameof(options));
        _client = client; _options = options;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "responses", "https://api.openai.com/v1/responses");
    }
    private long? NativeMaxTokens() => _parameters is { } parameters && parameters.TryGetProperty("max_output_tokens", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens > 0 ? tokens : null;
    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        if (Stateful) request = request with { ModelState = request.ModelState is { } initial ? ModelStateValues.Clone(initial) : new StateStore().GetAll() };
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(FormatRequest(request)) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ProviderHttp.ResolveApiKeyAsync(_options.ApiKey, _options.ApiKeyProvider, cancellationToken).ConfigureAwait(false));
        message.Headers.Accept.Add(new("text/event-stream"));
        var response = await ProviderHttp.SendAsync(_client, message, "OpenAI Responses", cancellationToken).ConfigureAwait(false);
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
            var state = new ResponseState();
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new ModelException("Responses stream ended without a terminal response event.");
                    var frame = frames.Current;
                    using var document = ProviderHttp.Parse(frame.Data, "OpenAI Responses");
                    var root = document.RootElement;
                    var type = ProviderHttp.RequiredString(root, "type");
                    if (frame.Event is { Length: > 0 } && frame.Event != type) throw new ModelException("Responses event name does not match its data type.");
                    events = state.Process(type, root);
                    if (Stateful && state.Completed)
                        events = events.Select(value => value is ModelMessageStopEvent stop ? stop with { ModelState = NextState(request, stop) } : value).ToArray();
                }
                catch (Exception error) { primary = error; throw; }
                foreach (var item in events) yield return item;
                if (state.Completed) yield break;
            }
        }
        finally
        {
            Exception? cleanup = null;
            try { if (frames is not null) await frames.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup = error; }
            try { response.Dispose(); }
            catch (Exception error)
            {
                if (cleanup is null) cleanup = error;
                else AddCleanup(cleanup, error);
            }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else AddCleanup(primary, cleanup);
            }
        }
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var body = ProviderConfiguration.Parameters(_parameters);
        body["model"] = _options.ModelId; body["stream"] = true; body["store"] = _options.Store || Stateful;
        body["input"] = FormatMessages(request.Messages);
        if (Stateful)
        {
            // Managed conversation state wins over raw parameters, including the first request of a new conversation.
            body.Remove("previous_response_id");
            if (request.ModelState is { } modelState && modelState.TryGetValue("openaiResponses", out var state))
            {
                if (ProviderHttp.OptionalString(state, "modelId") != _options.ModelId)
                    throw new ModelException("Stored Responses conversation state belongs to a different model.");
                var responseId = ProviderHttp.RequiredString(state, "responseId");
                if (string.IsNullOrWhiteSpace(responseId)) throw new ModelException("Stored Responses state requires a nonempty response identifier.");
                body["previous_response_id"] = responseId;
            }
            else if (request.Messages.FirstOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true)
                throw new ModelException("Unacknowledged Responses tool outputs require the stored server conversation identifier.");
        }
        if (request.SystemPrompt is not null) body["instructions"] = request.SystemPrompt;
        if (_options.MaxTokens is { } maximum) body["max_output_tokens"] = maximum;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.TopP is { } topP) body["top_p"] = topP;
        if (_format is { } format)
        {
            var text = body.TryGetValue("text", out var configured) && configured is JsonElement element ? ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(element)) : new Dictionary<string, object?>();
            text["format"] = format; body["text"] = text;
        }
        if (_reasoning is { } reasoning) body["reasoning"] = reasoning;
        var tools = new List<object>();
        if (body.TryGetValue("tools", out var native))
        {
            if (native is not JsonElement { ValueKind: JsonValueKind.Array } array) throw new ArgumentException("Native Responses tools must be an array.", nameof(request));
            tools.AddRange(array.EnumerateArray().Select(value => (object)value.Clone()));
        }
        if (request.Tools is { } specifications) tools.AddRange(specifications.Select(tool => (object)new { type = "function", name = tool.Name, description = tool.Description, parameters = tool.InputSchema, strict = (bool?)null }));
        ValidateChoice(request.ToolChoice, tools);
        if (tools.Count > 0) body["tools"] = tools;
        if (request.ToolChoice is { } choice) body["tool_choice"] = choice.Mode switch
        {
            ToolChoiceMode.Auto => "auto",
            ToolChoiceMode.Any => "required",
            ToolChoiceMode.None => "none",
            ToolChoiceMode.Tool => new { type = "function", name = choice.Name },
            _ => throw new ArgumentException("Unknown tool selection.", nameof(request))
        };
        if (_options.CacheConfig is { } cache)
        {
            var key = cache.CacheKey ?? (cache.UseSessionKey && request.SessionId is { } session ? "strands-" + session : null);
            if (!body.ContainsKey("prompt_cache_key") && key is not null) body["prompt_cache_key"] = key;
            if (!body.ContainsKey("prompt_cache_retention") && cache.Ttl is { } ttl) body["prompt_cache_retention"] = ttl;
        }
        return body;
    }

    private IReadOnlyDictionary<string, JsonElement> NextState(ModelRequest request, ModelMessageStopEvent stop)
    {
        var response = stop.Metadata!.Value.GetProperty("openaiResponses");
        var state = request.ModelState?.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        state["openaiResponses"] = JsonSerializer.SerializeToElement(new { modelId = _options.ModelId, responseId = ProviderHttp.RequiredString(response, "id") });
        return ModelStateValues.Clone(state);
    }

    private static void ValidateChoice(ToolChoice? choice, IReadOnlyList<object> tools)
    {
        if (choice is null) return;
        if (!Enum.IsDefined(typeof(ToolChoiceMode), choice.Mode) || choice.Mode != ToolChoiceMode.Tool && choice.Name is not null) throw new ArgumentException("Invalid Responses tool selection.", nameof(choice));
        if (choice.Mode == ToolChoiceMode.Any && tools.Count == 0) throw new ArgumentException("Required selection needs a supplied tool.", nameof(choice));
        if (choice.Mode != ToolChoiceMode.Tool) return;
        if (string.IsNullOrWhiteSpace(choice.Name) || !tools.Select(value => JsonSerializer.SerializeToElement(value)).Any(value => ProviderHttp.OptionalString(value, "type") == "function" && ProviderHttp.OptionalString(value, "name") == choice.Name)) throw new ArgumentException("Named selection needs a supplied function.", nameof(choice));
    }

    private static List<object> FormatMessages(IReadOnlyList<Message> messages)
    {
        var input = new List<object>();
        foreach (var message in messages)
        {
            var ordinary = new List<object>();
            var responseParts = new List<ResponsesContentBlock>();
            foreach (var block in message.Role == Role.User ? MediaContent.ExpandDocuments(message.Content, plainText: true) : message.Content)
            {
                switch (block)
                {
                    case TextBlock text: FlushParts(); ordinary.Add(new { type = "input_text", text = text.Text }); break;
                    case CitationsBlock citations: FlushParts(); ordinary.Add(new { type = "input_text", text = citations.Text }); break;
                    case ImageBlock or DocumentBlock or JsonBlock or ResourceLinkBlock when message.Role == Role.User: FlushParts(); ordinary.Add(FormatInput(block)); break;
                    case ResponsesContentBlock part when message.Role == Role.Assistant:
                        FlushOrdinary();
                        if (responseParts.Count > 0 && responseParts[0].ItemId != part.ItemId) FlushParts();
                        responseParts.Add(part); break;
                    case ToolUseBlock call when message.Role == Role.Assistant && call.Input.ValueKind == JsonValueKind.Object:
                        Flush(); input.Add(new { type = "function_call", call_id = call.ToolUseId, name = call.Name, arguments = call.Input.GetRawText() }); break;
                    case ToolResultBlock result when message.Role == Role.User:
                        Flush(); input.Add(new { type = "function_call_output", call_id = result.ToolUseId, output = FormatResult(result) }); break;
                    case ResponsesReasoningBlock reasoning when message.Role == Role.Assistant: Flush(); input.Add(reasoning.Item); break;
                    case ResponsesOutputItemBlock output when message.Role == Role.Assistant: Flush(); input.Add(output.Item); break;
                    default: throw new NotSupportedException($"Responses cannot represent {block.GetType().Name} in a {message.Role} message.");
                }
            }
            Flush();
            void Flush()
            {
                FlushOrdinary(); FlushParts();
            }
            void FlushOrdinary()
            {
                if (ordinary.Count > 0)
                {
                    input.Add(message.Role == Role.User ? new { role = "user", content = (object)ordinary.ToArray() } : new { role = "assistant", content = (object)string.Join("\n", ordinary.Select(value => JsonSerializer.SerializeToElement(value).GetProperty("text").GetString())) });
                    ordinary.Clear();
                }
            }
            void FlushParts()
            {
                if (responseParts.Count == 0) return;
                var ordered = responseParts.OrderBy(value => value.ContentIndex).ToArray();
                if (ordered.Where((value, index) => value.ContentIndex != index || value.Status != ordered[0].Status).Any()) throw new ArgumentException("Responses message parts are inconsistent.", nameof(messages));
                var complete = ordered.Where(value => value.Item is not null).Select(value => value.Item!.Value).ToArray();
                if (complete.Length > 1 && complete.Skip(1).Any(value => !JsonElement.DeepEquals(value, complete[0]))) throw new ArgumentException("Responses message metadata is inconsistent.", nameof(messages));
                if (complete.Length > 0)
                {
                    var parts = complete[0].GetProperty("content");
                    if (parts.GetArrayLength() != ordered.Length || ordered.Where((value, index) => !JsonElement.DeepEquals(value.Part, parts[index])).Any()) throw new ArgumentException("Responses message metadata disagrees with its parts.", nameof(messages));
                    input.Add(complete[0]);
                }
                else input.Add(new { type = "message", id = ordered[0].ItemId, role = "assistant", status = ordered[0].Status, content = ordered.Select(value => value.Part).ToArray() });
                responseParts.Clear();
            }
        }
        return input;
    }

    private static object FormatInput(ContentBlock block) => block switch
    {
        TextBlock text => new { type = "input_text", text = text.Text },
        JsonBlock json => new { type = "input_text", text = json.Json.GetRawText() },
        ImageBlock image => new { type = "input_image", image_url = ProviderConfiguration.DataUrl(image) },
        DocumentBlock document => new { type = "input_file", file_data = $"data:{document.MediaType};base64,{MediaContent.InlineData(document, "OpenAI Responses")}", filename = document.Name ?? "document" },
        ResourceLinkBlock link => new { type = "input_text", text = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, mimeType = link.MediaType, description = link.Description }) },
        _ => throw new NotSupportedException($"Responses cannot represent {block.GetType().Name} as input content.")
    };

    private static object FormatResult(ToolResultBlock result)
    {
        var blocks = MediaContent.ExpandDocuments(result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)], plainText: true).ToList();
        if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(value => value.Json.GetRawText() == structured.GetRawText())) blocks.Add(new JsonBlock(structured));
        var media = blocks.Any(value => value is ImageBlock or DocumentBlock);
        if (media)
        {
            var parts = blocks.Select(FormatInput).ToList();
            if (result.Status == ToolResultStatus.Error) parts.Insert(0, new { type = "input_text", text = "[ERROR]" });
            return parts;
        }
        return (result.Status == ToolResultStatus.Error ? "[ERROR] " : "") + string.Join("\n", blocks.Select(value => value switch
        {
            TextBlock text => text.Text,
            JsonBlock json => json.Json.GetRawText(),
            ResourceLinkBlock link => JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, mimeType = link.MediaType, description = link.Description }),
            _ => throw new NotSupportedException($"Responses cannot represent {value.GetType().Name} in a tool result.")
        }));
    }

    private sealed class ResponseState
    {
        private readonly Dictionary<int, OutputItem> _items = [];
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private string? _responseId;
        private long _sequence = -1;
        internal bool Completed { get; private set; }
        internal IReadOnlyList<ModelStreamEvent> Process(string type, JsonElement root)
        {
            if (root.TryGetProperty("sequence_number", out var sequence))
            {
                if (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var next) || next < 0 || next <= _sequence) throw new ModelException("Responses sequence numbers must increase.");
                _sequence = next;
            }
            if (type == "error") throw ProviderHttp.CreateError("OpenAI Responses", root);
            if (type == "response.failed") throw ProviderHttp.CreateError("OpenAI Responses", ProviderHttp.RequiredObject(root, "response"));
            if (type == "response.created")
            {
                if (_responseId is not null) throw new ModelException("Responses message started twice.");
                _responseId = ProviderHttp.RequiredString(ProviderHttp.RequiredObject(root, "response"), "id");
                return [new ModelMessageStartEvent()];
            }
            if (_responseId is null) throw new ModelException("Responses content arrived before response.created.");
            if (type is "response.completed" or "response.incomplete") return Complete(type, root);
            if (type is "response.in_progress" or "response.queued") return [];
            if (type == "response.output_item.added") return Add(root);
            var outputIndex = Index(root, "output_index");
            if (!_items.TryGetValue(outputIndex, out var item) || item.Done) throw new ModelException("Responses event refers to a missing or completed item.");
            if (type == "response.output_item.done") return FinishItem(outputIndex, item, ProviderHttp.RequiredObject(root, "item"));
            if (ProviderHttp.RequiredString(root, "item_id") != item.Id) throw new ModelException("Responses item identity changed.");
            if (item.Kind == "function_call") return Function(type, root, outputIndex, item);
            if (item.Kind == "reasoning")
            {
                if (type is "response.reasoning_text.delta" or "response.reasoning_summary_text.delta")
                {
                    var text = String(root, "delta"); item.Text.Append(text);
                    return [new ModelContentBlockDeltaEvent(BlockIndex(outputIndex, 0), Reasoning: text)];
                }
                if (type is "response.reasoning_text.done" or "response.reasoning_summary_text.done" or "response.reasoning_summary_part.added" or "response.reasoning_summary_part.done") return [];
            }
            if (item.Kind == "message") return Content(type, root, outputIndex, item);
            if (type.StartsWith("response.", StringComparison.Ordinal)) return [];
            throw new ModelException("Unknown Responses event: " + type);
        }

        private IReadOnlyList<ModelStreamEvent> Add(JsonElement root)
        {
            var index = Index(root, "output_index");
            var value = ProviderHttp.RequiredObject(root, "item");
            var id = ProviderHttp.RequiredString(value, "id");
            var kind = ProviderHttp.RequiredString(value, "type");
            if (index != _items.Count || !_ids.Add(id)) throw new ModelException("Responses output items must be unique and ordered.");
            var item = new OutputItem(id, kind); _items.Add(index, item);
            if (kind == "function_call")
            {
                item.CallId = ProviderHttp.RequiredString(value, "call_id"); item.Name = ProviderHttp.RequiredString(value, "name");
                var initial = ProviderHttp.OptionalString(value, "arguments") ?? ""; item.Text.Append(initial);
                var events = new List<ModelStreamEvent> { new ModelContentBlockStartEvent(BlockIndex(index, 0), new(item.CallId, item.Name, ProviderHttp.EmptyObject)) };
                if (initial.Length > 0) events.Add(new ModelContentBlockDeltaEvent(BlockIndex(index, 0), ToolInput: initial));
                return events;
            }
            if (kind == "reasoning") return [new ModelContentBlockStartEvent(BlockIndex(index, 0)) { ContentBlock = new ReasoningBlock("") }];
            if (kind == "message" && ProviderHttp.RequiredString(value, "role") != "assistant") throw new ModelException("Responses output messages must be assistant messages.");
            return [];
        }

        private static IReadOnlyList<ModelStreamEvent> Function(string type, JsonElement root, int index, OutputItem item)
        {
            if (item.ArgumentsDone) throw new ModelException("Responses function arguments changed after completion.");
            if (type == "response.function_call_arguments.delta")
            {
                var delta = String(root, "delta"); item.Text.Append(delta);
                return [new ModelContentBlockDeltaEvent(BlockIndex(index, 0), ToolInput: delta)];
            }
            if (type == "response.function_call_arguments.done")
            {
                var final = ProviderHttp.RequiredString(root, "arguments");
                if (final != item.Text.ToString()) throw new ModelException("Responses final arguments disagree with streamed arguments.");
                item.ArgumentsDone = true; return [];
            }
            throw new ModelException("Unexpected Responses function event: " + type);
        }

        private static IReadOnlyList<ModelStreamEvent> Content(string type, JsonElement root, int outputIndex, OutputItem item)
        {
            var index = Index(root, "content_index");
            if (type == "response.content_part.added")
            {
                var part = ProviderHttp.RequiredObject(root, "part"); var kind = ProviderHttp.RequiredString(part, "type");
                if (kind is not ("output_text" or "refusal")) throw new NotSupportedException("Unsupported Responses content part: " + kind);
                if (index != item.Parts.Count) throw new ModelException("Responses content parts must be unique and ordered.");
                var text = ProviderHttp.OptionalString(part, kind == "refusal" ? "refusal" : "text") ?? "";
                item.Parts.Add(index, new(kind, text));
                return [new ModelContentBlockStartEvent(BlockIndex(outputIndex, index)) { ContentBlock = new TextBlock(text) }];
            }
            if (!item.Parts.TryGetValue(index, out var current) || current.Final is not null) throw new ModelException("Responses content event refers to a missing or completed part.");
            if (type is "response.output_text.delta" or "response.refusal.delta")
            {
                if ((type == "response.refusal.delta") != (current.Kind == "refusal")) throw new ModelException("Responses content kind changed.");
                var delta = String(root, "delta"); current.Text.Append(delta);
                return [new ModelContentBlockDeltaEvent(BlockIndex(outputIndex, index), Text: delta)];
            }
            if (type == "response.content_part.done")
            {
                var part = ProviderHttp.RequiredObject(root, "part");
                if (ProviderHttp.RequiredString(part, "type") != current.Kind || ProviderHttp.OptionalString(part, current.Kind == "refusal" ? "refusal" : "text") != current.Text.ToString()) throw new ModelException("Responses completed content rewrote streamed text.");
                current.Final = part.Clone(); return [];
            }
            if (type is "response.output_text.done" or "response.refusal.done")
            {
                if (ProviderHttp.OptionalString(root, current.Kind == "refusal" ? "refusal" : "text") != current.Text.ToString()) throw new ModelException("Responses final text disagrees with its deltas.");
                return [];
            }
            if (type == "response.output_text.annotation.added") { _ = ProviderHttp.RequiredObject(root, "annotation"); return []; }
            throw new ModelException("Unexpected Responses content event: " + type);
        }

        private static IReadOnlyList<ModelStreamEvent> FinishItem(int index, OutputItem item, JsonElement value)
        {
            if (ProviderHttp.RequiredString(value, "id") != item.Id || ProviderHttp.RequiredString(value, "type") != item.Kind) throw new ModelException("Responses completed item changed identity.");
            var events = new List<ModelStreamEvent>();
            if (item.Kind == "function_call")
            {
                if (ProviderHttp.RequiredString(value, "call_id") != item.CallId || ProviderHttp.RequiredString(value, "name") != item.Name || ProviderHttp.RequiredString(value, "arguments") != item.Text.ToString()) throw new ModelException("Responses completed function changed identity or arguments.");
                events.Add(new ModelContentBlockStopEvent(BlockIndex(index, 0)));
            }
            else if (item.Kind == "reasoning")
            {
                var reasoning = new ResponsesReasoningBlock(value);
                if (reasoning.Text != item.Text.ToString()) throw new ModelException("Responses completed reasoning disagrees with its deltas.");
                events.Add(new ModelContentBlockStopEvent(BlockIndex(index, 0)) { ContentBlock = reasoning });
            }
            else if (item.Kind == "message")
            {
                if (ProviderHttp.RequiredString(value, "role") != "assistant" || !value.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != item.Parts.Count) throw new ModelException("Responses completed message parts are inconsistent.");
                var status = ProviderHttp.RequiredString(value, "status");
                if (item.Parts.Count == 0)
                {
                    events.Add(new ModelContentBlockStartEvent(BlockIndex(index, 0)) { ContentBlock = new ResponsesOutputItemBlock(value) });
                    events.Add(new ModelContentBlockStopEvent(BlockIndex(index, 0)));
                }
                foreach (var part in item.Parts)
                {
                    if (part.Value.Final is not { } final || !JsonElement.DeepEquals(final, parts[part.Key])) throw new ModelException("Responses final item disagrees with its completed parts.");
                    events.Add(new ModelContentBlockStopEvent(BlockIndex(index, part.Key)) { ContentBlock = new ResponsesContentBlock(item.Id, part.Key, final, status, part.Key == 0 ? value : null) });
                }
            }
            else
            {
                events.Add(new ModelContentBlockStartEvent(BlockIndex(index, 0)) { ContentBlock = new ResponsesOutputItemBlock(value) });
                events.Add(new ModelContentBlockStopEvent(BlockIndex(index, 0)));
            }
            item.Final = value.Clone(); item.Done = true; return events;
        }

        private IReadOnlyList<ModelStreamEvent> Complete(string type, JsonElement root)
        {
            var response = ProviderHttp.RequiredObject(root, "response");
            if (ProviderHttp.RequiredString(response, "id") != _responseId || _items.Values.Any(value => !value.Done)) throw new ModelException("Responses response terminated with incomplete items or changed identity.");
            var status = ProviderHttp.OptionalString(response, "status");
            if (status is not null && status != (type == "response.completed" ? "completed" : "incomplete")) throw new ModelException("Responses terminal status disagrees with its event.");
            if (response.TryGetProperty("output", out var outputItems))
            {
                if (outputItems.ValueKind != JsonValueKind.Array || outputItems.GetArrayLength() != _items.Count || outputItems.EnumerateArray().Where((value, index) => !JsonElement.DeepEquals(value, _items[index].Final!.Value)).Any()) throw new ModelException("Responses final output disagrees with its streamed items.");
            }
            var stop = _items.Values.Any(value => value.Kind == "function_call") ? StopReasons.ToolUse : StopReasons.EndTurn;
            if (type == "response.incomplete")
            {
                stop = ProviderHttp.RequiredString(ProviderHttp.RequiredObject(response, "incomplete_details"), "reason") switch
                {
                    "max_output_tokens" => StopReasons.MaxTokens,
                    "content_filter" => StopReasons.ContentFiltered,
                    _ => throw new ModelException("Unsupported Responses incomplete reason.")
                };
            }
            var events = new List<ModelStreamEvent>();
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                if (usage.ValueKind != JsonValueKind.Object) throw new ModelException("Responses usage must be an object.");
                var input = ProviderHttp.TokenCount(usage, "input_tokens"); var output = ProviderHttp.TokenCount(usage, "output_tokens");
                var details = usage.TryGetProperty("input_tokens_details", out var value) ? value : default;
                if (details.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new ModelException("Responses token details must be an object.");
                long total;
                try { total = ProviderHttp.TokenCount(usage, "total_tokens", checked(input + output)); }
                catch (OverflowException error) { throw new ModelException("Responses token totals overflowed.", error); }
                events.Add(new ModelMetadataEvent(new(input, output, ProviderHttp.TokenCount(details, "cached_tokens"), ProviderHttp.TokenCount(details, "cache_write_tokens"), total)));
            }
            var responseMetadata = response.EnumerateObject().Where(value => value.Name != "output").ToDictionary(value => value.Name, value => value.Value.Clone(), StringComparer.Ordinal);
            events.Add(new ModelMessageStopEvent(stop) { Metadata = JsonSerializer.SerializeToElement(new { openaiResponses = responseMetadata }) }); Completed = true; return events;
        }

        private static string String(JsonElement value, string name) => ProviderHttp.OptionalString(value, name) ?? throw new ModelException("Responses requires a string " + name + ".");
        private static int Index(JsonElement value, string name) => value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var index) && index >= 0 ? index : throw new ModelException("Responses requires a nonnegative " + name + ".");
        private static int BlockIndex(int output, int part)
        {
            if (output > 32767 || part > 65535) throw new ModelException("Responses indices exceed the native block-index range.");
            return (output << 16) + part;
        }
        private sealed class OutputItem(string id, string kind)
        {
            internal string Id { get; } = id;
            internal string Kind { get; } = kind;
            internal string? CallId { get; set; }
            internal string? Name { get; set; }
            internal StringBuilder Text { get; } = new();
            internal Dictionary<int, Part> Parts { get; } = [];
            internal bool ArgumentsDone { get; set; }
            internal bool Done { get; set; }
            internal JsonElement? Final { get; set; }
        }
        private sealed class Part(string kind, string text)
        {
            internal string Kind { get; } = kind;
            internal StringBuilder Text { get; } = new(text);
            internal JsonElement? Final { get; set; }
        }
    }
}
