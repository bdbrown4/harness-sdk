using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents;

internal static class ToolStreamRunner
{
    internal static async ValueTask<ToolResultBlock> InvokeAsync(IStreamingTool tool, JsonElement input, ToolContext context,
        Func<ToolStreamEvent, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken, Action? onCleanupFailure = null)
    {
        Guard.NotNull(tool, nameof(tool));
        Guard.NotNull(context, nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncEnumerator<IToolStreamEvent>? iterator = null;
        ToolResultBlock? result = null;
        Exception? failure = null;
        try
        {
            iterator = (tool.StreamAsync(input, context, cancellationToken) ?? throw new InvalidOperationException("A streaming tool returned a null stream."))
                .GetAsyncEnumerator(cancellationToken) ?? throw new InvalidOperationException("A streaming tool returned a null iterator.");
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result is not null) throw new InvalidOperationException("A streaming tool emitted content after its terminal result.");
                switch (iterator.Current)
                {
                    case ToolStreamEvent update:
                        if (progress is not null) await progress(new(update.Data), cancellationToken).ConfigureAwait(false);
                        break;
                    case ToolStreamResultEvent terminal:
                        if (terminal.Result.ToolUseId != context.ToolUse.ToolUseId) throw new InvalidOperationException("A streaming tool returned a different tool-use identifier.");
                        if (!Enum.IsDefined(typeof(ToolResultStatus), terminal.Result.Status)) throw new InvalidOperationException("A streaming tool returned an invalid result status.");
                        result = (ToolResultBlock)Message.CloneBlock(terminal.Result);
                        break;
                    default: throw new InvalidOperationException("A streaming tool emitted a null or unsupported event.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) throw new InvalidOperationException("A streaming tool completed without a terminal result.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (iterator is not null)
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    if (failure is null) failure = cleanup;
                    else AttachCleanupError(failure, cleanup);
                    onCleanupFailure?.Invoke();
                    if (!failure.Data.IsReadOnly) failure.Data["Strands.ToolCleanupFailed"] = true;
                }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private static void AttachCleanupError(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }
}
