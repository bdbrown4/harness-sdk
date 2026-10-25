using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

/// <summary>Immutable OpenAI Realtime session configuration. Params override session defaults recursively.</summary>
public sealed record OpenAIRealtimeModelOptions
{
    /// <summary>The explicit Realtime model identifier.</summary>
    public required string ModelId { get; init; }
    /// <summary>The explicit input transcription model, or null to disable input transcription.</summary>
    public required string? TranscriptionModelId { get; init; }
    /// <summary>The API key, or null to resolve OPENAI_API_KEY at construction.</summary>
    public string? ApiKey { get; init; }
    /// <summary>The organization, or null to resolve OPENAI_ORGANIZATION.</summary>
    public string? Organization { get; init; }
    /// <summary>The project, or null to resolve OPENAI_PROJECT.</summary>
    public string? Project { get; init; }
    /// <summary>Output voice; defaults to alloy.</summary>
    public string Voice { get; init; } = "alloy";
    /// <summary>Additional native session fields, recursively overriding defaults and direct session settings.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>Optional explicit ws/wss endpoint; defaults to the OpenAI Realtime WebSocket API.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>Maximum connection duration; defaults to and cannot exceed 3000 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3000);
    /// <summary>Declared restart policy. The default interval is Timeout minus 300 seconds.</summary>
    public BidiConnectionOptions Connection { get; init; } = new();
    /// <summary>Maximum UTF-8 bytes in one incoming or outgoing JSON event; defaults to 16 MiB.</summary>
    public int MaximumEventBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Bounded transport shutdown time; defaults to five seconds. Expiration throws TimeoutException; caller cancellation retains its caller token.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Returns a secret-safe configuration summary.</summary>
    public override string ToString() => $"OpenAIRealtimeModelOptions {{ ModelId = {ModelId}, ApiKey = [redacted] }}";
}
