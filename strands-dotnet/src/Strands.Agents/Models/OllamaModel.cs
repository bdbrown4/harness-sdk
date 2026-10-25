using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A native newline-delimited JSON adapter for Ollama chat, borrowing the caller's HTTP client.</summary>
public sealed partial class OllamaModel : IModel, IModelMetadataProvider, IConfigurableModel<OllamaModelOptions>
{
    /// <summary>Constructs a provider with an explicit model and caller-owned HTTP client.</summary>
    public OllamaModel(HttpClient client, OllamaModelOptions options)
    {
        Guard.NotNull(client, nameof(client)); Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResponseBytes must be positive.");
        if (options.MaxTokens is < -2 or 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive or -1/-2.");
        if (options.Temperature is { } temperature && (!Guard.IsFinite(temperature) || temperature < 0)) throw new ArgumentOutOfRangeException(nameof(options), "Temperature must be nonnegative and finite.");
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        if (options.KeepAlive is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number) }) throw new ArgumentException("KeepAlive must be a duration string or number.", nameof(options));
        if (options.Format is { } format && format.ValueKind != JsonValueKind.Object && (format.ValueKind != JsonValueKind.String || format.GetString() != "json"))
            throw new ArgumentException("Format must be a JSON schema object or the string json.", nameof(options));
        _client = client;
        _options = options;
        _nativeOptions = ProviderConfiguration.Snapshot(options.Options);
        _additional = ProviderConfiguration.Snapshot(options.AdditionalArgs);
        _keepAlive = options.KeepAlive?.Clone();
        _format = options.Format?.Clone();
        _stops = options.StopSequences?.ToArray();
        if (_stops?.Any(string.IsNullOrEmpty) == true) throw new ArgumentException("Stop sequences cannot be empty.", nameof(options));
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "api/chat", "http://localhost:11434/api/chat");
        long? window = _nativeOptions is { } native && native.TryGetProperty("num_ctx", out var context) && context.TryGetInt64(out var limit) && limit > 0 ? limit : null;
        _metadata = new(options.ModelId, window, options.MaxTokens is > 0 ? options.MaxTokens : null);
    }

    private readonly HttpClient _client;
    private readonly OllamaModelOptions _options;
    private readonly JsonElement? _nativeOptions, _additional, _keepAlive, _format;
    private readonly string[]? _stops;
    private readonly Uri _endpoint;
    private readonly ModelMetadata _metadata;
    private OllamaModel? _configured;
    /// <inheritdoc/>
    public OllamaModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with { Options = current._nativeOptions, AdditionalArgs = current._additional, KeepAlive = current._keepAlive, Format = current._format, StopSequences = current._stops is null ? null : Array.AsReadOnly(current._stops) };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<OllamaModelOptions, OllamaModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new OllamaModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }
    /// <inheritdoc />
    public ModelMetadata Metadata => (Volatile.Read(ref _configured) ?? this)._metadata;
    /// <inheritdoc />
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(FormatRequest(request)) };
        message.Headers.Accept.Add(new("application/x-ndjson"));
        var response = await ProviderHttp.SendAsync(_client, message, "Ollama", cancellationToken, new[] { "application/x-ndjson", "application/ndjson", "application/json" }).ConfigureAwait(false);
        IAsyncEnumerator<string>? frames = null;
        Exception? primary = null;
        try
        {
            try
            {
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                frames = NewlineDelimitedJson.ReadAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            var state = new StreamState();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new ModelException("Ollama stream ended without done=true.");
                    using var document = ProviderHttp.Parse(frames.Current, "Ollama");
                    events = state.Process(document.RootElement);
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
            catch (Exception error) { if (cleanup is null) cleanup = error; else AddCleanup(cleanup, error); }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else AddCleanup(primary, cleanup);
            }
        }
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        ProviderConfiguration.ValidateToolChoice(request);
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages)
            foreach (var block in message.Content) messages.AddRange(FormatBlock(block, message.Role == Role.User ? "user" : "assistant"));
        var options = ProviderConfiguration.Parameters(_nativeOptions);
        if (_options.MaxTokens is { } maximum) options["num_predict"] = maximum;
        if (_options.Temperature is { } temperature) options["temperature"] = temperature;
        if (_options.TopP is { } topP) options["top_p"] = topP;
        if (_stops is not null) options["stop"] = _stops;
        var body = ProviderConfiguration.Parameters(_additional);
        body["model"] = _options.ModelId;
        body["messages"] = messages;
        body["options"] = options;
        body["stream"] = true;
        body["tools"] = request.Tools?.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray() ?? [];
        if (_keepAlive is { } keepAlive) body["keep_alive"] = keepAlive;
        if (_format is { } format) body["format"] = format;
        return body;
    }

    private static IEnumerable<object> FormatBlock(ContentBlock block, string role)
    {
        switch (block)
        {
            case TextBlock text: yield return new { role, content = text.Text }; break;
            case CitationsBlock citations: yield return new { role, content = citations.Text }; break;
            case ImageBlock image: yield return new { role, images = new[] { MediaContent.InlineData(image, "Ollama") } }; break;
            case ToolUseBlock call:
                yield return new { role, tool_calls = new[] { new { function = new { name = call.Name, arguments = call.Input } } } }; break;
            case ToolResultBlock result:
                var content = result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) };
                foreach (var item in content)
                {
                    if (item is JsonBlock json) yield return new { role = "tool", content = json.Json.GetRawText() };
                    else foreach (var converted in FormatBlock(item, "tool")) yield return converted;
                }
                break;
            default: throw new NotSupportedException($"Ollama cannot represent {block.GetType().Name} in its pinned flattened message format.");
        }
    }

    internal static bool IsContextOverflow(string message) => new[] { "the prompt is longer than the context length", "the input length exceeds the context length", "exceeds the available context", "exceeded max context length" }
        .Any(fragment => message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }

    private sealed class StreamState
    {
        private int _index = 1;
        private bool _tools;
        internal bool Completed { get; private set; }
        internal IReadOnlyList<ModelStreamEvent> Process(JsonElement root)
        {
            if (root.TryGetProperty("error", out _)) throw ProviderHttp.CreateError("Ollama", root);
            if (!root.TryGetProperty("done", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ModelException("Ollama events require a boolean done field.");
            var events = new List<ModelStreamEvent>();
            if (root.TryGetProperty("message", out var message))
            {
                if (message.ValueKind != JsonValueKind.Object) throw new ModelException("Ollama message must be an object.");
                if (message.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")) throw new ModelException("Ollama response role must be assistant.");
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
                {
                    if (calls.ValueKind != JsonValueKind.Array) throw new ModelException("Ollama tool_calls must be an array.");
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) throw new ModelException("Ollama tool calls require a function.");
                        var name = ProviderHttp.RequiredString(function, "name");
                        if (string.IsNullOrWhiteSpace(name) || !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object) throw new ModelException("Ollama tool arguments must be a JSON object.");
                        events.Add(new ModelContentBlockStartEvent(_index) { ContentBlock = new ToolUseBlock("tooluse_" + Guid.NewGuid().ToString("N").Substring(0, 24), name, arguments.Clone()) });
                        events.Add(new ModelContentBlockStopEvent(_index++));
                        _tools = true;
                    }
                }
                if (message.TryGetProperty("content", out var text))
                {
                    if (text.ValueKind != JsonValueKind.String) throw new ModelException("Ollama message content must be a string.");
                    if (text.GetString() is { Length: > 0 } value) events.Add(new ModelContentBlockDeltaEvent(0, Text: value));
                }
            }
            if (!done.GetBoolean()) return events;
            var input = Count(root, "prompt_eval_count");
            var output = Count(root, "eval_count");
            try { _ = checked(input + output); }
            catch (OverflowException error) { throw new ModelException("Ollama total usage exceeds the supported token-count range.", error); }
            events.Add(new ModelContentBlockStopEvent(0));
            events.Add(new ModelMetadataEvent(new(input, output)));
            var reason = root.TryGetProperty("done_reason", out var terminal) && terminal.ValueKind == JsonValueKind.String ? terminal.GetString() : null;
            events.Add(new ModelMessageStopEvent(_tools ? StopReasons.ToolUse : reason == "length" ? StopReasons.MaxTokens : StopReasons.EndTurn) { Metadata = root.Clone() });
            Completed = true;
            return events;
        }

        private static long Count(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw new ModelException($"Ollama {name} must be a nonnegative integer.");
            return count;
        }
    }
}
