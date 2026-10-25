using Strands.Agents.Bidi;

namespace Strands.Agents.Bedrock;

/// <summary>Settings captured by the next Nova Sonic connection.</summary>
public sealed record BedrockNovaSonicModelOptions
{
    /// <summary>The required Nova Sonic model identifier. The AWS client supplies region and credentials.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>The output voice; defaults to matthew.</summary>
    public string VoiceId { get; init; } = "matthew";

    /// <summary>The mono PCM16 input rate: 8000, 16000 or 24000 Hz; defaults to 16000.</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>The mono PCM16 output rate: 8000, 16000 or 24000 Hz; defaults to 24000.</summary>
    public int OutputSampleRate { get; init; } = 24000;

    /// <summary>Maximum queued input events before sending waits for the AWS publisher; defaults to 32.</summary>
    public int MaximumBufferedInputEvents { get; init; } = 32;

    /// <summary>Maximum queued output events before transport reads wait for the receiver; defaults to 64.</summary>
    public int MaximumBufferedOutputEvents { get; init; } = 64;

    /// <summary>Maximum UTF8 bytes in one input batch or initial session, including its envelopes; defaults to 1 MiB.</summary>
    public int MaximumInputBytes { get; init; } = 1048576;

    /// <summary>Maximum generated tokens per response; defaults to 1024.</summary>
    public int MaxTokens { get; init; } = 1024;
    /// <summary>Sampling temperature from zero through one; defaults to 0.7.</summary>
    public double Temperature { get; init; } = 0.7;
    /// <summary>Nucleus probability from zero through one; defaults to 0.9.</summary>
    public double TopP { get; init; } = 0.9;
    /// <summary>Automatic reconnect policy. The default seven-minute interval precedes Nova Sonic's eight-minute connection limit.</summary>
    public BidiConnectionOptions Connection { get; init; } = new() { RestartAfter = TimeSpan.FromMinutes(7) };
}
