namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly ContinuationInputs _continuationInputs;
    internal void AddContinuationInput(HookEvent boundary, IReadOnlyList<Message> messages, Func<ValueTask>? onAppended = null, Func<Exception, ValueTask>? onAbandoned = null)
        => _continuationInputs.Add(boundary, messages, onAppended, onAbandoned);

    private static IReadOnlyList<Message> CombineContinuationInput(IReadOnlyList<Message>? automatic, IReadOnlyList<Message> manual)
        => automatic is null ? manual : automatic.Concat(manual).ToArray();

    private async ValueTask<(ModelRequest Request, IReadOnlyList<Message> Appended)> ApplyBeforeModelContinuationAsync(BeforeModelCallEvent boundary, CancellationToken cancellationToken)
    {
        try
        {
            await Hooks.InvokeAsync(boundary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var request = boundary.Request ?? throw new InvalidOperationException("A before-model hook supplied a null request.");
            if (boundary.CancelReason is not null)
            {
                await _continuationInputs.AbandonAsync(boundary, new InvalidOperationException("The model call was denied.")).ConfigureAwait(false);
                return (request, Array.Empty<Message>());
            }
            var messages = await _continuationInputs.PrepareAsync(boundary).ConfigureAwait(false);
            if (messages is null) return (request, Array.Empty<Message>());
            await AddInputMessagesAsync(messages, boundary.InvocationState, cancellationToken, () => _continuationInputs.MarkAppendedAsync(boundary)).ConfigureAwait(false);
            return (request with { Messages = request.Messages.Concat(messages).ToArray() }, messages);
        }
        catch (BackgroundModelInterruptException)
        {
            _ = await _continuationInputs.PrepareAsync(boundary, StopReasons.Interrupt).ConfigureAwait(false);
            throw;
        }
        catch (Exception failure)
        {
            await _continuationInputs.AbandonAsync(boundary, failure).ConfigureAwait(false);
            throw;
        }
    }
}
