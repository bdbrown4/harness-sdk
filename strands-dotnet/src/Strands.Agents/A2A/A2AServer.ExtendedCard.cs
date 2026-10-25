using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private JsonElement? _authenticatedCard;
    private readonly HashSet<Task> _cardReads = [];
    private async Task<JsonElement> ReadAuthenticatedCardAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var scope = Scope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Server, this)) throw new InvalidOperationException("An extended-card callback cannot recursively read its own server.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shutdown;
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            if (_authenticatedCard is null) throw new A2ARpcException(-32007, "An authenticated extended card is not configured.");
            shutdown = _shutdown.Token; _cardReads.Add(completion.Task);
        }
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
            operation.Token.ThrowIfCancellationRequested();
            var allowed = await _options.AuthorizeExtendedCard!(request, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (!allowed) throw new ExtendedCardDeniedException();
            lock (_gate) return _authenticatedCard!.Value;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
        {
            var canceled = new OperationCanceledException("The extended-card request was canceled.", error, cancellationToken);
            if (error.Data["Strands.CleanupErrors"] is List<Exception> cleanup) canceled.Data["Strands.CleanupErrors"] = new List<Exception>(cleanup);
            throw canceled;
        }
        finally
        {
            Scope.Value = previous;
            completion.TrySetResult(true); lock (_gate) _cardReads.Remove(completion.Task);
        }
    }
    private sealed class ExtendedCardDeniedException : Exception;
}
