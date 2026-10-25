using System.Runtime.ExceptionServices;
using Amazon.Runtime.EventStreams;
using Amazon.SageMakerRuntime.Model;

namespace Strands.Agents.SageMaker;

internal sealed class SageMakerResponseReader : IAsyncDisposable
{
    private readonly ResponseStream _output;
    private readonly IAsyncEnumerator<IEventStreamEvent> _events;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _registration;
    private readonly Func<Exception, Exception> _translate;
    private readonly List<Exception> _cleanup = [];
    private int _disposed;
    internal Exception? PrimaryError { get; set; }
    internal IEventStreamEvent Current => _events.Current;

    internal SageMakerResponseReader(ResponseStream output, CancellationToken token, Func<Exception, Exception> translate)
    {
        _output = output; _token = token; _translate = translate;
        try { _events = output.GetAsyncEnumerator(token); }
        catch (Exception primary)
        {
            try { output.Dispose(); } catch (Exception cleanup) { SageMakerAIModel.AttachCleanup(primary, cleanup); }
            throw;
        }
        _registration = token.Register(DisposeOutput);
    }
    internal async ValueTask<bool> MoveNextAsync()
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            var next = await _events.MoveNextAsync().ConfigureAwait(false);
            _token.ThrowIfCancellationRequested();
            return next;
        }
        catch (Exception error)
        {
            PrimaryError = _translate(error);
            ExceptionDispatchInfo.Capture(PrimaryError).Throw(); throw;
        }
    }
    private void DisposeOutput()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _output.Dispose(); } catch (Exception error) { lock (_cleanup) _cleanup.Add(error); }
    }
    public async ValueTask DisposeAsync()
    {
        _registration.Dispose();
        try { await _events.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { lock (_cleanup) _cleanup.Add(error); }
        DisposeOutput();
        var primary = PrimaryError;
        foreach (var cleanup in _cleanup) { if (primary is null) primary = cleanup; else SageMakerAIModel.AttachCleanup(primary, cleanup); }
        if (PrimaryError is null && primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
