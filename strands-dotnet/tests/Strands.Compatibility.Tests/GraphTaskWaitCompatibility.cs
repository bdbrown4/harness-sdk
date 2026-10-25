#if NETFRAMEWORK
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Agents.Tests.MultiAgent;

internal static class GraphTaskWaitCompatibility
{
    internal static Task WaitAsync(this Task operation, CancellationToken cancellationToken) =>
        Strands.Agents.TaskCancellation.WaitAsync(operation, cancellationToken);

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
}
#endif
