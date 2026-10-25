namespace Strands.Agents;

internal static class TaskCancellation
{
    // Cancel only the caller's wait, leaving accepted background work running on legacy and modern targets alike.
    internal static async Task WaitAsync(Task operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.CanBeCanceled && !operation.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => canceled.TrySetResult(true)))
                if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) != operation) cancellationToken.ThrowIfCancellationRequested();
        }
        await operation.ConfigureAwait(false);
    }
}
