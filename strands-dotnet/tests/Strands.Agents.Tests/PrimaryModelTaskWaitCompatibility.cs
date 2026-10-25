#if NETFRAMEWORK
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Agents.Tests.PrimaryModelPortability;

internal static class PrimaryModelTaskWaitCompatibility
{
    internal static async Task WaitAsync(this Task operation, CancellationToken cancellationToken)
    {
        if (operation.IsCompleted)
        {
            await operation.ConfigureAwait(false);
            return;
        }
        await Strands.Agents.TaskCancellation.WaitAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitAsync(this Task operation, TimeSpan timeout)
    {
        if (operation.IsCompleted)
        {
            await operation.ConfigureAwait(false);
            return;
        }
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await Strands.Agents.TaskCancellation.WaitAsync(operation, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && error.CancellationToken == deadline.Token)
        {
            throw new TimeoutException("The operation has timed out.", error);
        }
    }

    internal static async Task<T> WaitAsync<T>(this Task<T> operation, TimeSpan timeout)
    {
        await WaitAsync((Task)operation, timeout).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }
}
#endif
