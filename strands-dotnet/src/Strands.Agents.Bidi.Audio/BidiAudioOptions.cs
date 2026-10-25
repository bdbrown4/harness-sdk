namespace Strands.Agents.Bidi;

/// <summary>Selects devices, bounded buffering and optional microphone processing.</summary>
public sealed record BidiAudioOptions
{
    /// <summary>The input device index; null selects the backend default.</summary>
    public int? InputDeviceIndex { get; init; }
    /// <summary>The output device index; null selects the backend default.</summary>
    public int? OutputDeviceIndex { get; init; }
    /// <summary>The number of PCM frames requested per device callback.</summary>
    public int FramesPerBuffer { get; init; } = 512;
    /// <summary>The maximum buffered capture chunks; overflow drops the oldest chunk.</summary>
    public int InputBufferCapacity { get; init; } = 100;
    /// <summary>The maximum queued playback bytes. A write exceeding capacity fails explicitly without discarding queued speech.</summary>
    public int OutputBufferBytes { get; init; } = 1024 * 1024;
    /// <summary>Optional mono PCM microphone echo cancellation, noise suppression and gain control.</summary>
    public BidiAudioProcessingOptions? Processing { get; init; }
    /// <summary>An optional borrowed console sink. Null creates ordinary transcript and tool output.</summary>
    public IBidiOutputStream? ConsoleOutput { get; init; }
}

/// <summary>Selects independent managed voice-processing operations.</summary>
public sealed record BidiAudioProcessingOptions
{
    /// <summary>Whether to remove correlated speaker playback from capture.</summary>
    public bool EchoCancellation { get; init; } = true;
    /// <summary>Whether to reduce stationary background noise in captured speech.</summary>
    public bool NoiseSuppression { get; init; } = true;
    /// <summary>Whether to normalize microphone levels without amplifying digital silence.</summary>
    public bool AutomaticGainControl { get; init; } = true;
    /// <summary>The expected speaker-to-microphone delay, from 0 to 1000 milliseconds.</summary>
    public int StreamDelayMilliseconds { get; init; }
}
