using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Bidi.Models;

/// <summary>Native Gemini Developer API or Vertex AI Live WebSocket configuration.</summary>
public sealed record GoogleGeminiLiveModelOptions
{
    /// <summary>The Developer model identifier or fully qualified Vertex model resource.</summary>
    public required string ModelId { get; init; }
    /// <summary>Uses the Vertex AI Live protocol with an explicit endpoint and model resource.</summary>
    public bool UseVertexAi { get; init; }
    /// <summary>Borrowed callback resolving a fresh OAuth access token for each Vertex connection attempt.</summary>
    [JsonIgnore]
    public Func<CancellationToken, Task<string>>? AccessTokenProvider { get; init; }
    /// <summary>The Developer API key; null resolves GOOGLE_API_KEY, then GEMINI_API_KEY. Ignored for Vertex.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Optional prebuilt output voice; null uses the service default.</summary>
    public string? Voice { get; init; }
    /// <summary>The mono PCM16 input rate; defaults to 16000Hz. Output is 24000Hz.</summary>
    public int InputSampleRate { get; init; } = 16000;
    /// <summary>Additional native camelCase setup fields, recursively overriding session defaults.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>An absolute ws/wss endpoint, required for Vertex; otherwise defaults to the Developer v1beta endpoint.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>Declared automatic restart settings; the default interval is 540 seconds.</summary>
    public BidiConnectionOptions Connection { get; init; } = new();
    /// <summary>Cooperative deadline for credentials and setupComplete; defaults to thirty seconds. Credential callbacks are physically awaited.</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Bounded socket shutdown time; defaults to five seconds.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Maximum UTF-8 bytes per native JSON message; defaults to 16MiB.</summary>
    public int MaximumEventBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Returns a secret-safe configuration summary.</summary>
    public override string ToString() => $"GoogleGeminiLiveModelOptions {{ ModelId = {ModelId}, ApiKey = [redacted] }}";
}
