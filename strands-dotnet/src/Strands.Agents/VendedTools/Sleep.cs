using System.Globalization;
using System.Text.Json;

namespace Strands.Agents.VendedTools;

/// <summary>Creates an optional cooperative managed sleep tool.</summary>
public static class Sleep
{
    // Framework Task.Delay accepts at most Int32.MaxValue milliseconds; leave one millisecond below that limit.
    private const double MaximumChunkSeconds = (int.MaxValue - 1) / 1000d;

    /// <summary>Creates an inert tool with an application-selected maximum duration.</summary>
    public static ITool Create(SleepOptions? options = null)
    {
        options ??= new();
        if (options.MaximumDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumDuration must be positive.");
        var maximumSeconds = options.MaximumDuration.TotalSeconds;
        var maximumText = maximumSeconds.ToString("R", CultureInfo.InvariantCulture);
        var spec = new ToolSpec(options.Name, options.Description ??
            $"Pauses execution for a specified number of seconds (max {maximumText}). Cooperative and cancellable: the sleep aborts when its execution is cancelled. Rejects negative, nonfinite, nonnumeric, and excessive durations.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    duration = new
                    {
                        type = "number",
                        minimum = 0,
                        maximum = maximumSeconds,
                        description = $"Seconds to pause. Must be finite, non-negative, and no larger than {maximumText}.",
                    },
                },
                required = new[] { "duration" },
            }));
        return new FunctionTool(spec, async (input, context, cancellationToken) =>
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            var signal = context.CancelSignal;
            signal.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("duration", out var duration) ||
                duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var seconds) ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > maximumSeconds)
                throw new ArgumentOutOfRangeException(nameof(input), "duration must be a finite non-negative number of seconds within MaximumDuration.");

            using var linked = cancellationToken.CanBeCanceled && signal.CanBeCanceled && cancellationToken != signal
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, signal)
                : null;
            var effectiveToken = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : signal);
            try
            {
                var remaining = seconds;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, MaximumChunkSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(chunk), effectiveToken).ConfigureAwait(false);
                    remaining -= chunk;
                }
                effectiveToken.ThrowIfCancellationRequested();
                return $"Slept for {seconds.ToString("R", CultureInfo.InvariantCulture)} seconds";
            }
            catch (OperationCanceledException error) when (linked is not null && error.CancellationToken == effectiveToken && effectiveToken.IsCancellationRequested)
            {
                // A linked timer token is internal. Keep cancellation attributable to an original execution token.
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
                if (signal.IsCancellationRequested)
                    throw new OperationCanceledException(error.Message, error, signal);
                throw;
            }
        });
    }
}
