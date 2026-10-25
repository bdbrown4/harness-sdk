namespace Strands.Agents.Bidi;

/// <summary>A persistent model connection supporting independent concurrent sending and receiving.</summary>
public interface IBidiModel : IAsyncDisposable
{
    /// <summary>Establishes a connection using a detached initial conversation and tool configuration.</summary>
    Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default);
    /// <summary>Sends one complete input or live audio delta after the connection has started.</summary>
    Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default);
    /// <summary>Receives ordered model events through a single active reader.</summary>
    IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync(CancellationToken cancellationToken = default);
    /// <summary>Closes the current connection. A stopped model can be started again.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
    /// <summary>Declared automatic-restart timing consumed by a bidirectional agent loop.</summary>
    BidiConnectionOptions Connection { get; }
}

/// <summary>A model supporting a fresh connection with provider-specific history replay or resumption.</summary>
public interface IRestartableBidiModel : IBidiModel
{
    /// <summary>Re-establishes the connection and restores the supplied conversation without replaying tool bodies.</summary>
    Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default);
}

/// <summary>A model exposing resolved input and output audio stream formats.</summary>
public interface IAudioBidiModel
{
    /// <summary>The resolved audio stream formats.</summary>
    BidiAudioConfig Audio { get; }
}

/// <summary>Initial configuration for a persistent model conversation.</summary>
public sealed record BidiSessionOptions
{
    /// <summary>Optional system instructions.</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>Tool specifications available to the remote model; tool execution remains local.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; init; } = [];
    /// <summary>Initial conversation history, copied before connection I/O.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];
}

/// <summary>Declared restart policy; model adapters do not run an agent's restart scheduler.</summary>
public sealed record BidiConnectionOptions
{
    /// <summary>A positive interval requests a proactive restart; null uses the provider default.</summary>
    public TimeSpan? RestartAfter { get; init; }
    /// <summary>Whether a bidirectional agent loop should restart automatically; defaults to true.</summary>
    public bool AutoRestart { get; init; } = true;
}

/// <summary>Resolved raw audio stream format.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">The channel count.</param>
/// <param name="Format">The raw encoding, such as pcm.</param>
public sealed record BidiAudioStreamConfig(int SampleRate, int Channels, string Format);

/// <summary>Resolved audio formats for both connection directions.</summary>
/// <param name="Input">Input stream format.</param>
/// <param name="Output">Output stream format.</param>
public sealed record BidiAudioConfig(BidiAudioStreamConfig Input, BidiAudioStreamConfig Output);

/// <summary>A provider connection deadline that can trigger a restart in an agent loop.</summary>
public sealed class BidiConnectionTimeoutException : Exception
{
    /// <summary>Creates a connection-deadline failure.</summary>
    public BidiConnectionTimeoutException(string message) : base(message) { }
}
