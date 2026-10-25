using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>Inline approval prompt callbacks for command-line applications.</summary>
public static class HumanApprovalPrompts
{
    /// <summary>Creates a serialized standard-input prompt using borrowed readers and writers. Cancellation stops waiting for input; a pending noncancelable read retains the input slot until it completes.</summary>
    public static Func<string, CancellationToken, ValueTask<JsonElement?>> Stdio(bool includeTrust = false, TextReader? reader = null, TextWriter? writer = null)
    {
        var input = reader ?? Console.In;
        var output = writer ?? Console.Out;
        var gate = new SemaphoreSlim(1, 1);
        var suffix = includeTrust ? "(y/n/t)" : "(y/n)";
        return async (prompt, cancellationToken) =>
        {
            Guard.NotNull(prompt, nameof(prompt));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<string?>? reading = null;
            var drainOwnsGate = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await output.WriteAsync($"{prompt} {suffix}: ").ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Framework Console.In may implement ReadLineAsync synchronously. Keep that read off the invoking thread.
                reading = Task.Run(() => input.ReadLineAsync());
                var response = await ReadLineAsync(reading, cancellationToken).ConfigureAwait(false);
                return response is null ? null : JsonSerializer.SerializeToElement(response.Trim());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (reading is not null)
                {
                    drainOwnsGate = true;
                    _ = DrainAsync(reading, gate);
                }
                throw;
            }
            finally { if (!drainOwnsGate) gate.Release(); }
        };
    }

    private static async Task<string?> ReadLineAsync(Task<string?> reading, CancellationToken cancellationToken)
    {
        if (!reading.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => canceled.TrySetResult(true));
            await Task.WhenAny(reading, canceled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var result = await reading.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task DrainAsync(Task<string?> reading, SemaphoreSlim gate)
    {
        try { await reading.ConfigureAwait(false); }
        catch (Exception) { /* The canceled prompt no longer accepts a result; still observe its pending read. */ }
        finally { gate.Release(); }
    }
}
