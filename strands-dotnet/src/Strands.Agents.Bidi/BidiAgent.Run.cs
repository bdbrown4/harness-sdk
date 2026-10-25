using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    /// <summary>Connects, starts borrowed streams, pumps input and awaited output, then joins physical I/O and connection cleanup.</summary>
    /// <param name="inputs">Input streams, started in list order.</param>
    /// <param name="outputs">Output streams, started after inputs and invoked concurrently for each event.</param>
    /// <param name="invocationState">The caller-owned context retained by reference.</param>
    /// <param name="cancellationToken">Cancels this run while preserving the caller token.</param>
    public async Task RunAsync(IReadOnlyList<IBidiInputStream> inputs, IReadOnlyList<IBidiOutputStream> outputs,
        IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));
        if (outputs is null) throw new ArgumentNullException(nameof(outputs));
        var sources = inputs.ToArray(); var sinks = outputs.ToArray();
        if (sources.Any(stream => stream is null) || sinks.Any(stream => stream is null)) throw new ArgumentException("Bidirectional streams cannot contain null entries.");
        await RunCoreAsync(sources, sinks, null, invocationState, cancellationToken).ConfigureAwait(false);
    }

    internal Task RunInputAsync(IAsyncEnumerable<IBidiInput> input, IReadOnlyList<IBidiOutputStream> outputs,
        IReadOnlyDictionary<string, object?>? invocationState, CancellationToken cancellationToken)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (outputs is null) throw new ArgumentNullException(nameof(outputs));
        var sinks = outputs.ToArray();
        if (sinks.Any(stream => stream is null)) throw new ArgumentException("Bidirectional streams cannot contain null entries.", nameof(outputs));
        return RunCoreAsync([], sinks, input, invocationState, cancellationToken);
    }

    private async Task RunCoreAsync(IBidiInputStream[] sources, IBidiOutputStream[] sinks,
        IAsyncEnumerable<IBidiInput>? input, IReadOnlyDictionary<string, object?>? invocationState,
        CancellationToken cancellationToken)
    {
        var conversation = await StartConversationAsync(invocationState, cancellationToken, run: true).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conversation.Token);
        var token = operation.Token;
        var entered = new List<IBidiStream>();
        var pumps = new List<Task>();
        var errors = new ConcurrentQueue<Exception>();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primary = null;
        try
        {
            foreach (var stream in sources.Cast<IBidiStream>().Concat(sinks))
            {
                token.ThrowIfCancellationRequested(); entered.Add(stream);
                await InvokeStreamCallbackAsync(() => stream.StartAsync(this, token)).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            foreach (var source in sources) pumps.Add(PumpInputAsync(source));
            if (input is not null) pumps.Add(PumpSequenceAsync(input));
            var output = PumpOutputAsync(); pumps.Add(output);
            await Task.WhenAny(output, failed.Task, inputEnded.Task).ConfigureAwait(false);
            if (failed.Task.IsCompleted) primary = await failed.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (conversation.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { primary = error; }
        finally
        {
            if (primary is null && cancellationToken.IsCancellationRequested) primary = new OperationCanceledException(cancellationToken);
            try { operation.Cancel(); } catch (Exception error) { errors.Enqueue(error); }
            for (var index = entered.Count - 1; index >= 0; index--)
                try { await InvokeStreamCallbackAsync(() => entered[index].StopAsync(CancellationToken.None)).ConfigureAwait(false); }
                catch (Exception error) { errors.Enqueue(error); }
            var stopping = StopRunConversationAsync(conversation);
            foreach (var pump in pumps)
                try { await pump.ConfigureAwait(false); }
                catch (Exception error) { errors.Enqueue(error); }
            conversation.RunDrain!.TrySetResult(true);
            try { await stopping.ConfigureAwait(false); }
            catch (Exception error) { errors.Enqueue(error); }
            Interlocked.Exchange(ref conversation.Receiver, 0);
        }
        if (primary is null && failed.Task.IsCompleted) primary = await failed.Task.ConfigureAwait(false);
        foreach (var error in errors) Add(ref primary, error);
        if (primary is OperationCanceledException canceled && cancellationToken.IsCancellationRequested && canceled.CancellationToken != cancellationToken)
        {
            var replacement = new OperationCanceledException("The bidirectional run was canceled.", canceled, cancellationToken);
            if (canceled.Data["Strands.CleanupErrors"] is List<Exception> cleanup) replacement.Data["Strands.CleanupErrors"] = new List<Exception>(cleanup);
            primary = replacement;
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();

        void RecordFailure(Exception error) { errors.Enqueue(error); failed.TrySetResult(error); }
        async Task PumpSequenceAsync(IAsyncEnumerable<IBidiInput> sequence)
        {
            IAsyncEnumerator<IBidiInput>? iterator = null;
            try
            {
                var previous = Scope.Value; Scope.Value = new(this, previous);
                try { iterator = sequence.GetAsyncEnumerator(token); }
                finally { Scope.Value = previous; }
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool available;
                    IBidiInput? item = null;
                    previous = Scope.Value; Scope.Value = new(this, previous);
                    try
                    {
                        available = await iterator.MoveNextAsync().ConfigureAwait(false);
                        if (available) item = iterator.Current;
                    }
                    finally { Scope.Value = previous; }
                    if (!available) break;
                    token.ThrowIfCancellationRequested();
                    ValidateInput(item!);
                    await SendModelAsync(conversation, item!, true, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { RecordFailure(error); }
            finally
            {
                if (iterator is not null)
                    try { await InvokeStreamCallbackAsync(iterator.DisposeAsync).ConfigureAwait(false); }
                    catch (Exception error) { RecordFailure(error); }
                inputEnded.TrySetResult(true);
            }
        }
        async Task PumpInputAsync(IBidiInputStream source)
        {
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    IBidiInput input;
                    var previous = Scope.Value; Scope.Value = new(this, previous);
                    try { input = await source.ReadAsync(token).ConfigureAwait(false); }
                    finally { Scope.Value = previous; }
                    ValidateInput(input);
                    await SendModelAsync(conversation, input, true, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { RecordFailure(error); }
        }
        async Task PumpOutputAsync()
        {
            try
            {
                while (await conversation.Output.ReadAsync(token).ConfigureAwait(false) is { } value)
                {
                    await Task.WhenAll(sinks.Select(sink => WriteOutputAsync(sink, value))).ConfigureAwait(false);
                    if (failed.Task.IsCompleted || value is BidiConnectionStopEvent { Reason: "user_request" }) return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { RecordFailure(error); }
        }
        async Task WriteOutputAsync(IBidiOutputStream sink, IAgentStreamEvent output)
        {
            try { await InvokeStreamCallbackAsync(() => sink.WriteAsync(output, token)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { RecordFailure(error); }
        }
    }

    private static void ValidateInput(IBidiInput input)
    {
        if (input is not (BidiMessage or BidiAudioDelta) || input is BidiMessage message && message.Content.Any(block => block is ToolResultBlock))
            throw new ArgumentException("Input streams must supply user messages or audio deltas.");
    }

    private async ValueTask InvokeStreamCallbackAsync(Func<ValueTask> callback)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try { await callback().ConfigureAwait(false); }
        finally { Scope.Value = previous; }
    }

    private async Task StopRunConversationAsync(Conversation conversation)
    {
        bool current; lock (_gate) current = ReferenceEquals(_conversation, conversation);
        if (!current) { await conversation.Stopped.Task.ConfigureAwait(false); return; }
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            lock (_gate) current = ReferenceEquals(_conversation, conversation);
            if (current) await StopCoreAsync(default).ConfigureAwait(false);
            else await conversation.Stopped.Task.ConfigureAwait(false);
        }
        finally { Scope.Value = previous; _lifecycle.Release(); }
    }
}
