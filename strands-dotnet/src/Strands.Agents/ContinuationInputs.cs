using System.Runtime.CompilerServices;

namespace Strands.Agents;

// Internal hook-owned input transactions; application continuation remains AfterInvocationEvent.Resume.
internal sealed class ContinuationInputs(Agent owner)
{
    private readonly ConditionalWeakTable<HookEvent, State> _events = new();
    private readonly List<Input> _deferred = [];
    private readonly object _gate = new();

    internal void Add(HookEvent boundary, IReadOnlyList<Message> messages, Func<ValueTask>? onAppended = null, Func<Exception, ValueTask>? onAbandoned = null)
    {
        ValidateBoundary(boundary); Guard.NotNull(messages, nameof(messages));
        var input = new Input(messages.ToArray(), onAppended, onAbandoned);
        lock (_gate)
        {
            var state = _events.GetValue(boundary, _ => new());
            if (state.Preparing || state.Messages is not null) throw new InvalidOperationException("Cannot add inputs after continuation preparation has started.");
            state.Inputs.Add(input);
        }
    }

    internal async ValueTask<IReadOnlyList<Message>?> PrepareAsync(HookEvent boundary, string? stopReason = null)
    {
        ValidateBoundary(boundary);
        State state;
        Input[] inputs;
        lock (_gate)
        {
            state = _events.GetValue(boundary, _ => new());
            if (state.Preparing) throw new InvalidOperationException("Continuation input preparation is already active.");
            if (state.Messages is not null) return state.Messages;
            if (stopReason == StopReasons.Interrupt)
            {
                _deferred.AddRange(state.Inputs); _events.Remove(boundary); return null;
            }
            if (stopReason is not null && stopReason is not (StopReasons.EndTurn or StopReasons.StopSequence)) return null;
            if (boundary is AfterInvocationEvent && _deferred.Count > 0)
            { state.Inputs.InsertRange(0, _deferred); _deferred.Clear(); }
            if (state.Inputs.Count == 0) { _events.Remove(boundary); return null; }
            state.Preparing = true;
            inputs = state.Inputs.ToArray();
        }
        var accepted = new List<Input>(); var messages = new List<Message>();
        foreach (var input in inputs)
        {
            try
            {
                var detached = input.Messages.Select(message => message is null ? throw new InvalidDataException("Continuation messages cannot be null.") : new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray();
                ConversationHistory.Validate(detached);
                if (detached.Length == 0 || detached[detached.Length - 1].Role != Role.User)
                    throw new InvalidDataException("Continuation input must contain a complete message sequence ending with user input.");
                messages.AddRange(detached); accepted.Add(input);
            }
            catch (Exception error) { await SettleAsync(input, error).ConfigureAwait(false); }
        }
        lock (_gate)
        {
            state.Preparing = false;
            state.Inputs.Clear(); state.Inputs.AddRange(accepted);
            if (accepted.Count == 0) { _events.Remove(boundary); return null; }
            state.Messages = Array.AsReadOnly(messages.ToArray());
            return state.Messages;
        }
    }

    internal async ValueTask MarkAppendedAsync(HookEvent boundary)
    {
        ValidateBoundary(boundary);
        Input[] inputs;
        lock (_gate)
        {
            if (!_events.TryGetValue(boundary, out var state) || state.Messages is null) return;
            inputs = state.Inputs.ToArray(); _events.Remove(boundary);
        }
        foreach (var input in inputs) await SettleAsync(input, null).ConfigureAwait(false);
    }

    internal async ValueTask AbandonAsync(HookEvent boundary, Exception reason)
    {
        ValidateBoundary(boundary); Guard.NotNull(reason, nameof(reason));
        Input[] inputs;
        lock (_gate)
        {
            if (!_events.TryGetValue(boundary, out var state)) return;
            if (state.Preparing) throw new InvalidOperationException("Cannot abandon continuation inputs during preparation.");
            inputs = state.Inputs.ToArray(); _events.Remove(boundary);
        }
        foreach (var input in inputs) await SettleAsync(input, reason).ConfigureAwait(false);
    }

    internal async ValueTask AbandonDeferredAsync(Exception reason)
    {
        Guard.NotNull(reason, nameof(reason));
        Input[] inputs;
        lock (_gate) { inputs = _deferred.ToArray(); _deferred.Clear(); }
        foreach (var input in inputs) await SettleAsync(input, reason).ConfigureAwait(false);
    }

    private void ValidateBoundary(HookEvent boundary)
    {
        Guard.NotNull(boundary, nameof(boundary));
        if (!ReferenceEquals(boundary.Agent, owner) || boundary is not (BeforeModelCallEvent or AfterInvocationEvent))
            throw new ArgumentException("Continuation inputs require this agent's before-model or after-invocation event.", nameof(boundary));
    }

    private static async ValueTask SettleAsync(Input input, Exception? reason)
    {
        if (Interlocked.Exchange(ref input.Settled, 1) != 0) return;
        try
        {
            if (reason is null) { if (input.OnAppended is { } appended) await appended().ConfigureAwait(false); }
            else if (input.OnAbandoned is { } abandoned) await abandoned(reason).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { SdkLogging.Warning(error, "Continuation settlement callback failed: {Error}", error.Message); }
            catch (Exception) { /* Diagnostics cannot change input settlement or its invocation result. */ }
        }
    }

    private sealed class State
    {
        internal List<Input> Inputs { get; } = [];
        internal IReadOnlyList<Message>? Messages;
        internal bool Preparing;
    }
    private sealed record Input(IReadOnlyList<Message> Messages, Func<ValueTask>? OnAppended, Func<Exception, ValueTask>? OnAbandoned)
    {
        internal int Settled;
    }
}
