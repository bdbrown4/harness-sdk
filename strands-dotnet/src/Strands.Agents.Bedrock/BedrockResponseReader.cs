using System.Runtime.ExceptionServices;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;

namespace Strands.Agents.Bedrock;

internal sealed class BedrockResponseReader : IAsyncDisposable
{
    private readonly ConverseStreamOutput _output;
    private readonly IAsyncEnumerator<IEventStreamEvent> _events;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<Exception, Exception> _translate;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly List<Exception> _cleanupErrors = [];
    private int _outputDisposed;
    internal Exception? PrimaryError { get; set; }
    internal IEventStreamEvent Current => _events.Current;

    internal BedrockResponseReader(ConverseStreamOutput output, CancellationToken cancellationToken, Func<Exception, Exception> translate)
    {
        _output = output;
        _cancellationToken = cancellationToken;
        _translate = translate;
        _events = output.GetAsyncEnumerator(cancellationToken);
        _cancellation = cancellationToken.Register(DisposeOutput);
    }

    internal async ValueTask<bool> MoveNextAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var next = await _events.MoveNextAsync().ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
            return next;
        }
        catch (Exception error)
        {
            PrimaryError = _translate(error);
            ExceptionDispatchInfo.Capture(PrimaryError).Throw();
            throw;
        }
    }

    private void DisposeOutput()
    {
        if (Interlocked.Exchange(ref _outputDisposed, 1) != 0) return;
        try { _output.Dispose(); }
        catch (Exception error) { lock (_cleanupErrors) _cleanupErrors.Add(error); }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        try { await _events.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { lock (_cleanupErrors) _cleanupErrors.Add(error); }
        DisposeOutput();
        Exception? failure = PrimaryError;
        foreach (var cleanup in _cleanupErrors)
        {
            if (failure is null) failure = cleanup;
            else
            {
                const string key = "Strands.CleanupErrors";
                if (failure.Data[key] is not List<Exception> errors) failure.Data[key] = errors = [];
                errors.Add(cleanup);
            }
        }
        if (PrimaryError is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
