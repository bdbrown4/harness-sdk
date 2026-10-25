namespace Strands.Agents.Bedrock;

// One connection's wire ownership. The model retains Nova JSON, history and queue policy.
internal interface INovaSonicDuplex
{
    // Input must be consumed before waiting for response headers. Null input ends the stream.
    Task OpenAsync(Func<CancellationToken, Task<byte[]?>> readInput, CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken cancellationToken);
    // Stop joins owned transport work; the model separately joins its output-enumeration task.
    Task StopAsync();
}
