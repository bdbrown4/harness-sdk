namespace Strands.Agents.Bidi;

/// <summary>A borrowed input or output stream with an explicit run-scoped lifecycle.</summary>
public interface IBidiStream
{
    /// <summary>Starts this stream after the conversation has connected.</summary>
    ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default);
    /// <summary>Releases run-scoped I/O and unblocks pending callbacks; object ownership remains with the caller.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>A source read repeatedly while a bidirectional run remains active.</summary>
public interface IBidiInputStream : IBidiStream
{
    /// <summary>Reads the next complete user input or audio delta.</summary>
    ValueTask<IBidiInput> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>An awaited consumer of ordered bidirectional output.</summary>
public interface IBidiOutputStream : IBidiStream
{
    /// <summary>Consumes one output before the run advances to its next output.</summary>
    ValueTask WriteAsync(IAgentStreamEvent output, CancellationToken cancellationToken = default);
}

/// <summary>Adapts an input callable and optional lifecycle callables without owning their resources.</summary>
public sealed class BidiInputStream : IBidiInputStream
{
    private readonly Func<CancellationToken, ValueTask<IBidiInput>> _read;
    private readonly Func<BidiAgent, CancellationToken, ValueTask>? _start;
    private readonly Func<CancellationToken, ValueTask>? _stop;
    /// <summary>Creates a callable input stream.</summary>
    /// <param name="read">Reads one input per invocation.</param>
    /// <param name="start">Optional run initialization.</param>
    /// <param name="stop">Optional run cleanup.</param>
    public BidiInputStream(Func<CancellationToken, ValueTask<IBidiInput>> read,
        Func<BidiAgent, CancellationToken, ValueTask>? start = null, Func<CancellationToken, ValueTask>? stop = null)
    { _read = read ?? throw new ArgumentNullException(nameof(read)); _start = start; _stop = stop; }
    /// <inheritdoc />
    public ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default) => _start?.Invoke(agent, cancellationToken) ?? default;
    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => _stop?.Invoke(cancellationToken) ?? default;
    /// <inheritdoc />
    public ValueTask<IBidiInput> ReadAsync(CancellationToken cancellationToken = default) => _read(cancellationToken);
}

/// <summary>Adapts an output callable and optional lifecycle callables without owning their resources.</summary>
public sealed class BidiOutputStream : IBidiOutputStream
{
    private readonly Func<IAgentStreamEvent, CancellationToken, ValueTask> _write;
    private readonly Func<BidiAgent, CancellationToken, ValueTask>? _start;
    private readonly Func<CancellationToken, ValueTask>? _stop;
    /// <summary>Creates a callable output stream.</summary>
    /// <param name="write">Consumes one output per invocation.</param>
    /// <param name="start">Optional run initialization.</param>
    /// <param name="stop">Optional run cleanup.</param>
    public BidiOutputStream(Func<IAgentStreamEvent, CancellationToken, ValueTask> write,
        Func<BidiAgent, CancellationToken, ValueTask>? start = null, Func<CancellationToken, ValueTask>? stop = null)
    { _write = write ?? throw new ArgumentNullException(nameof(write)); _start = start; _stop = stop; }
    /// <inheritdoc />
    public ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default) => _start?.Invoke(agent, cancellationToken) ?? default;
    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => _stop?.Invoke(cancellationToken) ?? default;
    /// <inheritdoc />
    public ValueTask WriteAsync(IAgentStreamEvent output, CancellationToken cancellationToken = default) => _write(output, cancellationToken);
}
