using Strands.Agents.Files;

namespace Strands.Agents.Sandbox;

/// <summary>An explicit command and filesystem environment. Implementations define its authority; the name does not imply OS isolation.</summary>
public abstract class Sandbox : IFileSystem
{
    /// <summary>Returns borrowed tools bound to this environment. The default exposes no tools.</summary>
    public virtual IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();

    /// <summary>Executes a command, emitting incremental output followed by exactly one terminal result.</summary>
    public abstract IAsyncEnumerable<SandboxStreamEvent> ExecuteStreamingAsync(string command, ExecuteOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Executes source through the selected interpreter and environment.</summary>
    public virtual IAsyncEnumerable<SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = SandboxPolicy.CodeOptions(code, language, options, cancellationToken);
        return ExecuteStreamingAsync(language, snapshot, cancellationToken);
    }
    /// <summary>Consumes source execution output and awaits its terminal result and iterator cleanup.</summary>
    public async ValueTask<ExecutionResult> ExecuteCodeAsync(string code, string language, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        var iterator = ExecuteCodeStreamingAsync(code, language, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        ExecutionResult? result = null;
        Exception? failure = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failureDispatch = null;
        try
        {
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
                if (iterator.Current is ExecutionResult terminal) { result = terminal; break; }
            if (result is null) throw new InvalidOperationException("Sandbox source execution ended without a terminal result.");
        }
        catch (Exception error) { failure = error; failureDispatch = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup))
                {
                    const string key = "Strands.CleanupErrors";
                    var errors = failure.Data[key] is List<Exception> list ? list
                        : failure.Data[key] is IEnumerable<Exception> previous ? previous.ToList() : new List<Exception>();
                    if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                    failure.Data[key] = errors;
                }
            }
        }
        if (failure is not null) (failureDispatch ?? System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure)).Throw();
        return result!;
    }
    /// <summary>Consumes command output and returns its terminal result.</summary>
    public ValueTask<ExecutionResult> ExecuteAsync(string command, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ConsumeAsync();
        return new(completed.Task);
        async Task ConsumeAsync()
        {
            try
            {
                var iterator = ExecuteStreamingAsync(command, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                ExecutionResult? terminal = null;
                Exception? failure = null;
                try
                {
                    while (await iterator.MoveNextAsync().ConfigureAwait(false))
                        if (iterator.Current is ExecutionResult result) { terminal = result; break; }
                    if (terminal is null) throw new InvalidOperationException("Sandbox execution ended without a terminal result.");
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { await iterator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup)
                    {
                        if (failure is null) failure = cleanup;
                        else if (!ReferenceEquals(failure, cleanup))
                        {
                            const string key = "Strands.CleanupErrors";
                            var errors = failure.Data[key] is List<Exception> list ? list
                                : failure.Data[key] is IEnumerable<Exception> previous ? previous.ToList() : new List<Exception>();
                            if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                            failure.Data[key] = errors;
                        }
                    }
                }
                if (failure is null) completed.TrySetResult(terminal!);
                else completed.TrySetException(failure);
            }
            catch (Exception error) { completed.TrySetException(error); }
        }
    }
    /// <inheritdoc/>
    public abstract ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default);
    /// <inheritdoc/>
    public abstract ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default);
    /// <inheritdoc/>
    public abstract ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default);
    /// <inheritdoc/>
    public abstract ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    /// <inheritdoc/>
    public abstract ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default);
}
