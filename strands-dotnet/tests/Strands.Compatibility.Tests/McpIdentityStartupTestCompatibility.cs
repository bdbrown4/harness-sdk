#if NETFRAMEWORK
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Harness.Tests;

internal static class McpIdentityStartupTestCompatibility
{
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
        await ((Task)operation).WaitAsync(timeout).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    internal static async Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This fixture reads the official transport's already-buffered request content only.
        var result = await content.ReadAsStringAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
#endif
