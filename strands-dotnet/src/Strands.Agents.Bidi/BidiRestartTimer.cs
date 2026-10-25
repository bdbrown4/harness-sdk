using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

internal static class BidiRestartTimer
{
    internal static async Task RunAsync(TimeSpan deadline, TimeSpan warningLead, Func<TimeSpan, CancellationToken, Task> warning,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warningAt = deadline > warningLead ? deadline - warningLead : TimeSpan.Zero;
        await delay(warningAt, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var countdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadlineTask = delay(deadline - warningAt, countdown.Token);
        Exception? primary = null;
        try
        {
            await warning(deadline - warningAt, cancellationToken).ConfigureAwait(false);
            await deadlineTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) { primary = error; }
        finally
        {
            if (!deadlineTask.IsCompleted)
            {
                try { countdown.Cancel(); } catch (Exception error) { Add(ref primary, error); }
            }
            try { await deadlineTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (countdown.IsCancellationRequested && primary is not null) { }
            catch (Exception error) { Add(ref primary, error); }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static void Add(ref Exception? primary, Exception error)
    {
        if (primary is null) primary = error;
        else if (!ReferenceEquals(primary, error))
        {
            const string key = "Strands.CleanupErrors";
            if (primary.Data[key] is not List<Exception> failures) primary.Data[key] = failures = [];
            failures.Add(error);
        }
    }
}
