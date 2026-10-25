using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private sealed class EventBuffer(int maximumEvents, int maximumBytes)
    {
        private readonly object _gate = new();
        private readonly Queue<(JsonElement Event, int Bytes)> _events = new();
        private TaskCompletionSource<bool> _changed = NewSignal();
        private long _first, _next;
        private int _bytes, _subscribers;
        private bool _complete;
        private Exception? _error;
        internal bool HasFinal { get; private set; }
        internal long NextCursor { get { lock (_gate) return _next; } }
        internal void Publish(JsonElement value)
        {
            var bytes = Validate(value);
            lock (_gate)
            {
                if (_complete) throw new InvalidOperationException("The event buffer is complete.");
                _events.Enqueue((value.Clone(), (int)bytes)); _bytes += (int)bytes; _next++;
                while (_events.Count > maximumEvents || _bytes > maximumBytes) { _bytes -= _events.Dequeue().Bytes; _first++; }
                HasFinal = A2AJson.OptionalBoolean(value, "final");
                Pulse();
            }
        }
        internal int Validate(JsonElement value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value.GetRawText()) + 64L;
            if (bytes > maximumBytes) throw new A2AProtocolException("The event exceeds MaxReplayBytes.");
            return (int)bytes;
        }
        internal void Complete(Exception? error)
        { lock (_gate) { _error = error; _complete = true; Pulse(); } }
        internal async ValueTask<JsonElement?> ReadAsync(long cursor, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (cursor < _first) throw new A2ARpcException(-32000, "The requested event replay has expired; retrieve the current task instead.");
                    if (cursor < _next) return _events.ElementAt((int)(cursor - _first)).Event;
                    if (_complete)
                    {
                        if (_error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                        return null;
                    }
                    changed = _changed.Task;
                }
                await WaitAsync(changed, cancellationToken).ConfigureAwait(false);
            }
        }
        internal void Subscribe(int maximum)
        { lock (_gate) { if (_subscribers >= maximum) throw new A2ARpcException(-32000, "The task listener capacity has been reached."); _subscribers++; } }
        internal void Unsubscribe() { lock (_gate) _subscribers--; }
        private void Pulse() { var changed = _changed; _changed = NewSignal(); changed.TrySetResult(true); }
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
