using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

public sealed partial class OpenAIRealtimeModel
{
    private static OpenAIRealtimeModelOptions Snapshot(OpenAIRealtimeModelOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ModelId)) throw new ArgumentException("ModelId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice)) throw new ArgumentException("Voice is required.", nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(3000) || options.CloseTimeout <= TimeSpan.Zero || options.CloseTimeout.TotalMilliseconds > int.MaxValue || options.MaximumEventBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Endpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("ws" or "wss"))) throw new ArgumentException("Endpoint requires an absolute ws/wss URI.", nameof(options));
        if (options.Connection is null) throw new ArgumentException("Connection settings cannot be null.", nameof(options));
        var key = options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An OpenAI API key is required.", nameof(options));
        var snapshot = options with
        {
            ApiKey = key,
            Organization = options.Organization ?? Environment.GetEnvironmentVariable("OPENAI_ORGANIZATION"),
            Project = options.Project ?? Environment.GetEnvironmentVariable("OPENAI_PROJECT"),
            Params = options.Params?.Clone(),
            Connection = options.Connection with { RestartAfter = options.Connection.RestartAfter ?? options.Timeout - TimeSpan.FromSeconds(300) }
        };
        _ = SessionConfiguration(snapshot, new());
        return snapshot;
    }
    private static BidiSessionOptions SessionSnapshot(BidiSessionOptions? session)
    {
        session ??= new();
        if (session.Tools is null || session.Messages is null) throw new ArgumentException("Session tools and messages cannot be null.", nameof(session));
        return session with
        {
            Tools = Array.AsReadOnly(session.Tools.Select(tool => tool is null ? throw new ArgumentException("Null tool specification.", nameof(session)) : new ToolSpec(tool.Name, tool.Description, tool.InputSchema.Clone())).ToArray()),
            Messages = Array.AsReadOnly(session.Messages.Select(message => message is null ? throw new ArgumentException("Null history message.", nameof(session)) : new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray())
        };
    }
    private static JsonElement SessionConfiguration(OpenAIRealtimeModelOptions options, BidiSessionOptions session)
    {
        var defaults = JsonSerializer.SerializeToElement(new
        {
            type = "realtime",
            instructions = "You are a helpful assistant. Please speak in English and keep your responses clear and concise.",
            output_modalities = new[] { "audio" },
            audio = new
            {
                input = new { format = new { type = "audio/pcm", rate = 24000 }, turn_detection = new { type = "server_vad", threshold = 0.5, prefix_padding_ms = 300, silence_duration_ms = 500 }, transcription = options.TranscriptionModelId is null ? null : (object)new { model = options.TranscriptionModelId } },
                output = new { format = new { type = "audio/pcm", rate = 24000 }, voice = options.Voice }
            }
        });
        var fields = RealtimeJson.Copy(defaults);
        if (!string.IsNullOrEmpty(session.SystemPrompt)) fields["instructions"] = session.SystemPrompt;
        if (session.Tools.Count > 0) fields["tools"] = session.Tools.Select(tool => new { type = "function", name = tool.Name, description = tool.Description, parameters = tool.InputSchema }).ToArray();
        if (options.Params is { } parameters) RealtimeJson.Merge(fields, RealtimeJson.Copy(parameters));
        var result = JsonSerializer.SerializeToElement(fields);
        var audio = RealtimeJson.Object(result, "audio");
        foreach (var direction in new[] { "input", "output" })
        {
            var format = RealtimeJson.Object(RealtimeJson.Object(audio, direction), "format");
            if (RealtimeJson.Text(format, "type") != "audio/pcm" || RealtimeJson.Count(format, "rate") != 24000) throw new ArgumentException("OpenAI Realtime requires mono PCM at 24000Hz.", nameof(options));
        }
        var input = RealtimeJson.Object(audio, "input");
        if (!input.TryGetProperty("turn_detection", out var detection) || detection.ValueKind != JsonValueKind.Object ||
            detection.TryGetProperty("create_response", out var create) && create.ValueKind != JsonValueKind.True ||
            detection.TryGetProperty("interrupt_response", out var interrupt) && interrupt.ValueKind != JsonValueKind.True)
            throw new ArgumentException("OpenAI Realtime requires turn detection with automatic responses and interruption.", nameof(options));
        return result;
    }
}
