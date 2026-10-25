using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

internal sealed class BidiOutputQueue
{
    private readonly object _gate = new();
    private readonly Queue<IAgentStreamEvent> _events = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _capacity = new(1, 1);
    private bool _complete;
    private Exception? _error;
    internal async ValueTask WriteAsync(IAgentStreamEvent value, CancellationToken cancellationToken)
    {
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_complete) { _capacity.Release(); throw new InvalidOperationException("The bidirectional output queue has completed."); }
            _events.Enqueue(value);
        }
        _available.Release();
    }
    internal async ValueTask<IAgentStreamEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_complete && _events.Count == 0) { if (_error is { } error) ExceptionDispatchInfo.Capture(error).Throw(); return null; }
            }
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_events.Count > 0) { var value = _events.Dequeue(); _capacity.Release(); if (_complete) _available.Release(); return value; }
                if (_complete) { if (_error is { } error) ExceptionDispatchInfo.Capture(error).Throw(); return null; }
            }
        }
    }
    internal void Complete(Exception? error = null)
    {
        lock (_gate) { if (_complete) return; _error = error; _complete = true; }
        _available.Release();
    }
}
