using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

public sealed partial class GoogleGeminiLiveModel
{
    private static GoogleGeminiLiveModelOptions Snapshot(GoogleGeminiLiveModelOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ModelId)) throw new ArgumentException("ModelId is required.", nameof(options));
        string? key = null;
        if (options.UseVertexAi)
        {
            if (options.Endpoint is null || options.AccessTokenProvider is null) throw new ArgumentException("Vertex Live requires Endpoint and AccessTokenProvider.", nameof(options));
            var resource = options.ModelId.Split('/');
            if (resource.Length != 8 || resource[0] != "projects" || resource[2] != "locations" || resource[4] != "publishers" || resource[5] != "google" || resource[6] != "models" || resource.Any(part => part.Length == 0 || part.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')))) throw new ArgumentException("Vertex ModelId requires projects/{project}/locations/{location}/publishers/google/models/{model}.", nameof(options));
        }
        else
        {
            key = options.ApiKey ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A Google API key is required.", nameof(options));
            if (options.AccessTokenProvider is not null) throw new ArgumentException("AccessTokenProvider requires UseVertexAi.", nameof(options));
        }
        if (options.InputSampleRate <= 0 || options.MaximumEventBytes <= 0 || options.SetupTimeout <= TimeSpan.Zero || options.SetupTimeout.TotalMilliseconds > int.MaxValue || options.CloseTimeout <= TimeSpan.Zero || options.CloseTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Endpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("ws" or "wss"))) throw new ArgumentException("Endpoint must be an absolute ws/wss URI.", nameof(options));
        if (options.Connection is null || options.Connection.RestartAfter is { } interval && interval <= TimeSpan.Zero) throw new ArgumentException("Connection requires a positive restart interval.", nameof(options));
        var copy = options with { ApiKey = key, Params = options.Params?.Clone(), Connection = options.Connection with { RestartAfter = options.Connection.RestartAfter ?? TimeSpan.FromSeconds(540) } };
        _ = Configuration(copy, new(), null, false); return copy;
    }
    private PreparedSession Prepare(BidiSessionOptions? session, string? handle)
    {
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(GoogleGeminiLiveModel));
        session ??= new();
        if (session.Tools is null || session.Messages is null) throw new ArgumentException("Session collections cannot be null.", nameof(session));
        var detached = session with
        {
            Tools = Array.AsReadOnly(session.Tools.Select(tool => tool is null ? throw new ArgumentException("Null tool specification.", nameof(session)) : new ToolSpec(tool.Name, tool.Description, tool.InputSchema.Clone())).ToArray()),
            Messages = Array.AsReadOnly(session.Messages.Select(message => message is null ? throw new ArgumentException("Null history message.", nameof(session)) : new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray())
        };
        var turns = detached.Messages.Select(message => new { role = message.Role == Role.Assistant ? "model" : "user", parts = message.Content.OfType<TextBlock>().Select(text => new { text = text.Text }).ToArray() }).Where(turn => turn.parts.Length > 0).ToArray();
        var options = Volatile.Read(ref _options);
        var configuration = Configuration(options, detached, handle, handle is null && turns.Length > 0);
        var history = handle is null && turns.Length > 0 ? (object)new { clientContent = new { turns, turnComplete = true } } : null;
        _ = EventBytes(new { setup = configuration }, options.MaximumEventBytes);
        if (history is not null) _ = EventBytes(history, options.MaximumEventBytes);
        return new(options, configuration, history);
    }
    private static JsonElement Configuration(GoogleGeminiLiveModelOptions options, BidiSessionOptions session, string? handle, bool history)
    {
        var defaults = JsonSerializer.SerializeToElement(new
        {
            generationConfig = new { responseModalities = new[] { "AUDIO" } },
            outputAudioTranscription = new { },
            inputAudioTranscription = new { },
            contextWindowCompression = new { slidingWindow = new { } },
            sessionResumption = new { handle }
        });
        var fields = RealtimeJson.Copy(defaults);
        if (history && !options.UseVertexAi) fields["historyConfig"] = RealtimeJson.Copy(JsonSerializer.SerializeToElement(new { initialHistoryInClientContent = true }));
        if (!string.IsNullOrEmpty(session.SystemPrompt)) fields["systemInstruction"] = RealtimeJson.Copy(JsonSerializer.SerializeToElement(new { parts = new[] { new { text = session.SystemPrompt } } }));
        if (session.Tools.Count > 0) fields["tools"] = new[] { new { functionDeclarations = session.Tools.Select(tool => new { name = tool.Name, description = tool.Description, parameters = LiveSchema(tool.InputSchema) }).ToArray() } };
        if (options.Voice is not null) ((Dictionary<string, object?>)fields["generationConfig"]!)["speechConfig"] = RealtimeJson.Copy(JsonSerializer.SerializeToElement(new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = options.Voice } } }));
        if (options.Params is { } parameters)
        {
            var extra = RealtimeJson.Copy(parameters);
            if (options.UseVertexAi && extra.ContainsKey("historyConfig")) throw new ArgumentException("Vertex Live does not support Params.historyConfig.", nameof(options));
            if (extra.ContainsKey("model")) throw new ArgumentException("Use ModelId to configure the Live model.", nameof(options));
            RealtimeJson.Merge(fields, extra);
        }
        fields["model"] = options.UseVertexAi || options.ModelId.StartsWith("models/", StringComparison.Ordinal) ? options.ModelId : "models/" + options.ModelId;
        return JsonSerializer.SerializeToElement(fields);
    }
    private static Dictionary<string, object?> LiveSchema(JsonElement schema)
    {
        var fields = RealtimeJson.Copy(schema);
        if (schema.TryGetProperty("type", out var type) && type.ValueKind != JsonValueKind.Null)
        {
            if (type.ValueKind != JsonValueKind.String) throw new ArgumentException("Gemini Live structured Schema requires a single type enum.", nameof(schema));
            var nativeType = type.GetString()!.ToUpperInvariant();
            if (nativeType is not ("TYPE_UNSPECIFIED" or "STRING" or "NUMBER" or "INTEGER" or "BOOLEAN" or "ARRAY" or "OBJECT" or "NULL")) throw new ArgumentException("Unsupported Gemini Live schema type.", nameof(schema));
            fields["type"] = nativeType;
        }
        foreach (var name in new[] { "properties", "defs", "$defs" })
        {
            if (!schema.TryGetProperty(name, out var values) || values.ValueKind == JsonValueKind.Null) continue;
            if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Gemini Live schema properties require an object.", nameof(schema));
            fields[name] = values.EnumerateObject().ToDictionary(property => property.Name, property => (object?)LiveSchema(property.Value), StringComparer.Ordinal);
        }
        foreach (var name in new[] { "items", "additionalProperties" })
            if (schema.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object) fields[name] = LiveSchema(item);
        foreach (var name in new[] { "anyOf", "allOf", "oneOf" })
        {
            if (!schema.TryGetProperty(name, out var values) || values.ValueKind == JsonValueKind.Null) continue;
            if (values.ValueKind != JsonValueKind.Array) throw new ArgumentException("Gemini Live schema alternatives require an array.", nameof(schema));
            fields[name] = values.EnumerateArray().Select(LiveSchema).ToArray();
        }
        return fields;
    }
    private static (object Value, string[] Completed) Input(LiveConnection connection, IBidiInput input)
    {
        if (input is BidiAudioDelta audio) return (new { realtimeInput = new { audio = new { mimeType = "audio/pcm;rate=" + connection.Options.InputSampleRate, data = Convert.ToBase64String(audio.GetBytes()) } } }, []);
        if (input is not BidiMessage message) throw new ArgumentException("Unsupported Gemini Live input.", nameof(input));
        if (message.Content[0] is ToolResultBlock)
        {
            var results = message.Content.Cast<ToolResultBlock>().ToArray();
            if (results.Select(result => result.ToolUseId).Distinct(StringComparer.Ordinal).Count() != results.Length) throw new ArgumentException("Duplicate tool result identifiers.", nameof(input));
            var responses = results.Select(result =>
            {
                if (!connection.State.ToolNames.TryGetValue(result.ToolUseId, out var name)) throw new ArgumentException("No pending Gemini Live call matches the tool result.", nameof(input));
                var blocks = result.ContentBlocks.Count == 0 ? new ContentBlock[] { new TextBlock(result.Content) } : result.ContentBlocks;
                var content = blocks.Select(block => block switch { TextBlock text => (object)new { text = text.Text }, JsonBlock json => new { json = json.Json }, _ => throw new ArgumentException("Gemini Live tool results support only text and JSON.", nameof(input)) }).ToList();
                if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(block => JsonElement.DeepEquals(block.Json, structured))) content.Add(new { json = structured });
                var response = content.Count == 1 ? content[0] : new { result = content };
                return new { id = result.ToolUseId, name, response };
            }).ToArray();
            return (new { toolResponse = new { functionResponses = responses } }, results.Select(result => result.ToolUseId).ToArray());
        }
        var parts = message.Content.Select(block => block switch
        {
            TextBlock text => (object)new { text = text.Text },
            ImageBlock image when image.Location is null && image.Url is null => new { inlineData = new { mimeType = image.MediaType, data = image.Data } },
            _ => throw new ArgumentException("Gemini Live user messages require text or inline image bytes.", nameof(input))
        }).ToArray();
        return (new { clientContent = new { turns = new[] { new { role = "user", parts } }, turnComplete = true } }, []);
    }
}
