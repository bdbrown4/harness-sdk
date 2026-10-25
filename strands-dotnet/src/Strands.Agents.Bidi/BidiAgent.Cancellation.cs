namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private CancellationRequest? _cancelRequest = new();

    /// <summary>The cooperative stop-request signal, independent of active operation cancellation tokens.</summary>
    public CancellationToken CancelSignal
    {
        get { lock (_gate) return _cancelRequest?.Source.Token ?? CancellationToken.None; }
    }

    /// <summary>Requests conversation termination after a complete tool group has settled.</summary>
    /// <param name="message">An optional retained reason. The last nonnull value wins; it is not appended to history.</param>
    /// <param name="afterCurrentTools">Accepted for core-agent signature consistency. Bidirectional stop requests always wait for the tool group.</param>
    public void Cancel(string? message = null, bool afterCurrentTools = false)
    {
        CancellationRequest request;
        lock (_gate)
        {
            if (_disposed || _cancelRequest is not { } current) return;
            request = current;
            if (message is not null) request.Message = message;
            request.Signallers++;
        }
        try { request.Source.Cancel(); }
        finally
        {
            bool release;
            lock (_gate) { request.Signallers--; release = TryReleaseCancellationRequest(request); }
            if (release) request.Source.Dispose();
        }
    }

    private void ResetCancellationRequest()
    {
        CancellationRequest? release = null;
        lock (_gate)
        {
            var previous = _cancelRequest;
            _cancelRequest = _disposed ? null : new();
            if (previous is not null)
            {
                previous.Message = null;
                previous.Retired = true;
                if (TryReleaseCancellationRequest(previous)) release = previous;
            }
        }
        release?.Source.Dispose();
    }

    private static bool TryReleaseCancellationRequest(CancellationRequest request)
    {
        if (!request.Retired || request.Signallers != 0 || request.Disposed) return false;
        request.Disposed = true;
        return true;
    }

    private bool ShouldStopAfterTools(Conversation conversation, out BidiConnectionStopEvent? terminal)
    {
        lock (_gate)
        {
            terminal = null;
            if (_cancelRequest?.Source.IsCancellationRequested != true) return false;
            if (!conversation.StopPublished)
            {
                conversation.StopPublished = true;
                var connectionId = conversation.ConnectionIdGeneration == conversation.Generation ? conversation.ConnectionId : "unknown";
                terminal = new(connectionId, "user_request");
            }
            return true;
        }
    }

    private sealed class CancellationRequest
    {
        internal CancellationTokenSource Source { get; } = new();
        internal string? Message;
        internal int Signallers;
        internal bool Retired;
        internal bool Disposed;
    }
}
