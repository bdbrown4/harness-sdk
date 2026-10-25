namespace Strands.Agents.Bidi;

/// <summary>Runs a bidirectional conversation with a native asynchronous input sequence.</summary>
public static class BidiAgentRunExtensions
{
    /// <summary>Runs until the input sequence completes, output ends, or the conversation is stopped.</summary>
    /// <param name="agent">The borrowed agent.</param>
    /// <param name="input">The input sequence; normal completion ends the run after iterator cleanup.</param>
    /// <param name="outputs">Borrowed output streams, started and stopped by the run.</param>
    /// <param name="invocationState">Caller-owned invocation state.</param>
    /// <param name="cancellationToken">Cancels the run while preserving the caller token.</param>
    public static Task RunAsync(this BidiAgent agent, IAsyncEnumerable<IBidiInput> input,
        IReadOnlyList<IBidiOutputStream> outputs, IReadOnlyDictionary<string, object?>? invocationState = null,
        CancellationToken cancellationToken = default)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        return agent.RunInputAsync(input, outputs, invocationState, cancellationToken);
    }
}
