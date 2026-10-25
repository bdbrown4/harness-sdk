using System.Text.Json;

namespace Strands.Agents;

public sealed partial class Agent
{
    private InterruptState? _interruptState;
    private PendingToolBatch? _activeToolBatch;
    /// <summary>A detached snapshot of the pending interrupted tool batch, or null when no batch is paused.</summary>
    public InterruptState? InterruptState { get { lock (_historyGate) return _interruptState?.Copy(); } }

    internal string? ActiveInterruptBatchId { get { lock (_historyGate) return _activeToolBatch?.ContextId; } }

    internal JsonElement ResolveInterrupt(string toolUseId, string name, JsonElement? reason, InterruptSource source, JsonElement? response = null, bool isBatch = false, string? originBatchId = null)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        PendingToolBatch batch;
        lock (_historyGate) batch = _activeToolBatch ?? throw new InvalidOperationException("Human input is not available outside an active tool batch.");
        if (originBatchId != batch.ContextId) throw new InvalidOperationException("An interrupt context cannot outlive its originating tool batch.");
        return batch.Resolve(toolUseId, name, reason, source, response, isBatch);
    }

    internal JsonElement ResolveBatchInterrupt(string name, JsonElement? reason, JsonElement? response, string? originBatchId)
    {
        PendingToolBatch batch;
        lock (_historyGate) batch = _activeToolBatch ?? throw new InvalidOperationException("Human input is not available outside an active tool batch.");
        return ResolveInterrupt(batch.FirstCallId, name, reason, InterruptSource.Hook, response, isBatch: true, originBatchId);
    }

    private static InvocationOptions CopyInterruptResponses(InvocationOptions options)
    {
        Guard.NotNull(options.InterruptResponses, nameof(options.InterruptResponses));
        var responses = options.InterruptResponses.Select(value => value is null ? throw new ArgumentException("An interrupt response cannot be null.", nameof(options)) : new InterruptResponse(value.InterruptId, value.Response)).ToArray();
        if (responses.Select(value => value.InterruptId).Distinct(StringComparer.Ordinal).Count() != responses.Length)
            throw new ArgumentException("An invocation cannot provide duplicate interrupt responses.", nameof(options));
        return options with { InterruptResponses = Array.AsReadOnly(responses) };
    }

    private void ValidateInterruptResume(string? prompt, InvocationOptions options)
    {
        lock (_historyGate)
        {
            if (_interruptState is not { } pending)
            {
                if (options.InterruptResponses.Count > 0) throw new ArgumentException("This agent has no pending interrupts to answer.", nameof(options));
                return;
            }
            if (prompt is not null || options.InputMessage is not null || options.InputMessages.Count > 0) throw new ArgumentException("A paused tool batch must be resumed without a new user prompt.", nameof(prompt));
            var known = new HashSet<string>(pending.Interrupts.Select(value => value.Id), StringComparer.Ordinal);
            var answered = new HashSet<string>(pending.Responses.Select(value => value.InterruptId), StringComparer.Ordinal);
            if (options.InterruptResponses.Any(value => !known.Contains(value.InterruptId) || answered.Contains(value.InterruptId)))
                throw new ArgumentException("Responses must target outstanding interrupts and cannot replace an accepted response.", nameof(options));
        }
    }

    private async ValueTask AcceptInterruptResponsesAsync(InvocationOptions options)
    {
        var changed = false;
        lock (_historyGate)
            if (_interruptState is { } pending && options.InterruptResponses.Count > 0)
            {
                _interruptState = new(pending.AssistantMessage, pending.CompletedToolResults, pending.Interrupts, pending.Responses.Concat(options.InterruptResponses).ToArray(), pending.ToolExecutions) { AssistantMessageIsIndependent = pending.AssistantMessageIsIndependent, SourceHistoryOwned = pending.SourceHistoryOwned, Context = pending.Context };
                changed = true;
            }
        // Once accepted, a human answer survives callback failure, cancellation, and restart before tool execution.
        if (changed) await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private PendingToolBatch BeginToolBatch(ToolUseBlock[] calls)
    {
        lock (_historyGate)
        {
            var message = _interruptState?.AssistantMessage ?? _messages.Last();
            var pending = _interruptState ?? new(message, [], [], [])
            { SourceHistoryOwned = _options.ModelHistoryBehavior is ModelHistoryBehavior.TypeScript or ModelHistoryBehavior.Python };
            if (!calls.Select(call => call.ToolUseId).SequenceEqual(message.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId)))
                throw new InvalidOperationException("The executing tool batch must match its assistant message.");
            return _activeToolBatch = new(pending);
        }
    }

    private IReadOnlyList<Interrupt> UnansweredInterrupts()
    {
        var pending = InterruptState;
        if (pending is null) return [];
        var answered = new HashSet<string>(pending.Responses.Select(value => value.InterruptId), StringComparer.Ordinal);
        var completed = new HashSet<string>(pending.CompletedToolResults.Select(value => value.ToolUseId), StringComparer.Ordinal);
        return Array.AsReadOnly(pending.Interrupts.Where(value => !answered.Contains(value.Id) && (value.IsBatch || !completed.Contains(value.ToolUseId))).ToArray());
    }

    private sealed class PendingToolBatch
    {
        private readonly object _gate = new();
        private readonly Message _assistant;
        internal bool AssistantMessageIsIndependent { get; }
        private readonly bool _sourceHistoryOwned;
        internal Message AssistantMessage => _assistant;
        private readonly Dictionary<string, ToolUseBlock> _calls;
        private readonly Dictionary<string, ToolResultBlock> _completed;
        private readonly Dictionary<string, Interrupt> _questions;
        private readonly Dictionary<string, InterruptResponse> _responses;
        private readonly Dictionary<string, ToolExecutionCheckpoint> _executions;
        private readonly HashSet<string> _replayBodies;
        private readonly Dictionary<string, JsonElement> _context;
        private bool _closed;
        internal PendingToolBatch(InterruptState state)
        {
            _assistant = state.AssistantMessage;
            AssistantMessageIsIndependent = state.AssistantMessageIsIndependent;
            _sourceHistoryOwned = state.SourceHistoryOwned;
            _calls = _assistant.Content.OfType<ToolUseBlock>().ToDictionary(value => value.ToolUseId, StringComparer.Ordinal);
            _completed = state.CompletedToolResults.ToDictionary(value => value.ToolUseId, StringComparer.Ordinal);
            _questions = state.Interrupts.ToDictionary(value => value.Id, StringComparer.Ordinal);
            _responses = state.Responses.ToDictionary(value => value.InterruptId, StringComparer.Ordinal);
            _executions = state.ToolExecutions.ToDictionary(value => value.ToolUse.ToolUseId, StringComparer.Ordinal);
            _replayBodies = new(_executions.Keys, StringComparer.Ordinal);
            _context = state.Context.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);
        }
        internal bool IsPaused { get { lock (_gate) return _questions.Count > 0 && _completed.Count < _calls.Count; } }
        internal string FirstCallId => _assistant.Content.OfType<ToolUseBlock>().First().ToolUseId;
        internal string ContextId { get; } = Guid.NewGuid().ToString("N");
        internal void Close() { lock (_gate) _closed = true; }
        internal AgentToolCallContext AgentToolContext(string callId) => new(_gate, _context,
            () => { if (_closed || !_calls.ContainsKey(callId) || _completed.ContainsKey(callId)) throw new InvalidOperationException("An agent-tool interrupt context requires its incomplete originating call."); },
            (question, reopen) =>
            {
                var id = AgentToolInterruptJournal.Prefix(callId) + question.Id;
                if (reopen) _responses.Remove(id);
                if (_responses.TryGetValue(id, out var answer)) return answer.Response.Clone();
                var forwarded = new Interrupt(id, question.Name, callId, question.Reason, InterruptSource.Tool);
                _questions[id] = forwarded;
                throw new InterruptException(forwarded);
            }, () => _questions.Values.Any(question => question.ToolUseId == callId && AgentToolInterruptJournal.IsForwarded(question)),
            () =>
            {
                var questions = _questions.Values.Where(question => question.ToolUseId == callId && AgentToolInterruptJournal.IsForwarded(question)).ToArray();
                if (questions.Length > 0 && !questions.Any(question => _responses.ContainsKey(question.Id))) throw new InterruptException(questions);
            });
        internal bool TryGetResult(string id, out ToolResultBlock result) { lock (_gate) return _completed.TryGetValue(id, out result!); }
        internal IReadOnlyList<ContentBlock> ResultsForCommit()
        {
            lock (_gate) return _assistant.Content.OfType<ToolUseBlock>().Select(call => (ContentBlock)(_completed.TryGetValue(call.ToolUseId, out var result) ? result : CancelledTool(call))).ToArray();
        }
        internal void Complete(ToolResultBlock result) { lock (_gate) _completed[result.ToolUseId] = (ToolResultBlock)Message.CloneBlock(result); }
        internal void RecordBody(ToolUseBlock call, string toolName, ToolResultBlock result) { lock (_gate) _executions[call.ToolUseId] = new(call, toolName, result); }
        internal void ResetAttempt(string callId) { lock (_gate) { _executions.Remove(callId); _replayBodies.Remove(callId); } }
        internal bool IteratorCleanupFailed(string callId) { lock (_gate) return _executions.TryGetValue(callId, out var checkpoint) && checkpoint.IteratorCleanupFailed; }
        internal IReadOnlyList<string> SuccessfulBodyIds(string toolName)
        {
            lock (_gate) return _executions.Values.Where(value => value.ToolName == toolName && value.Result.Status == ToolResultStatus.Success).Select(value => value.ToolUse.ToolUseId).ToArray();
        }
        internal ToolResultBlock? ReuseBody(ToolUseBlock call, string toolName)
        {
            lock (_gate)
            {
                if (!_replayBodies.Contains(call.ToolUseId) || !_executions.TryGetValue(call.ToolUseId, out var prior)) return null;
                if (prior.ToolName != toolName || prior.ToolUse.Name != call.Name || !JsonElement.DeepEquals(prior.ToolUse.Input, call.Input))
                    throw new InvalidOperationException("A resumed tool body cannot change its completed input or selected implementation.");
                _replayBodies.Remove(call.ToolUseId);
                return (ToolResultBlock)Message.CloneBlock(prior.Result with { IteratorCleanupFailed = prior.IteratorCleanupFailed });
            }
        }
        internal JsonElement Resolve(string callId, string name, JsonElement? reason, InterruptSource source, JsonElement? preemptiveResponse, bool isBatch)
        {
            lock (_gate)
            {
                if (!_calls.ContainsKey(callId) || !isBatch && _completed.ContainsKey(callId)) throw new InvalidOperationException("Only an incomplete call in the active batch can request human input.");
                // Tracking identifiers are application-owned and may repeat. Persisted questions retain their own opaque identities.
                var prior = _questions.Values.FirstOrDefault(value => value.ToolUseId == callId && value.Name == name && value.Source == source && value.IsBatch == isBatch);
                var id = prior?.Id ?? Guid.NewGuid().ToString("N");
                if (_responses.TryGetValue(id, out var response)) return response.Response.Clone();
                if (preemptiveResponse.HasValue) return InterruptJson.Copy(preemptiveResponse, nameof(preemptiveResponse))!.Value;
                var question = new Interrupt(id, name, callId, reason, source, isBatch);
                throw new InterruptException(question);
            }
        }
        internal void Register(Interrupt question, string callId)
        {
            lock (_gate)
            {
                if (question.ToolUseId != callId || !_calls.ContainsKey(callId) || !question.IsBatch && _completed.ContainsKey(callId))
                    throw new InvalidOperationException("An interrupt must retain its original tool-call correlation.");
                if (_questions.TryGetValue(question.Id, out var prior) && (prior.ToolUseId != callId || prior.Name != question.Name || prior.Source != question.Source || prior.IsBatch != question.IsBatch))
                    throw new InvalidOperationException("An interrupt identifier cannot be reused for another request.");
                _questions[question.Id] = question;
            }
        }
        internal InterruptState Snapshot()
        {
            lock (_gate) return new(_assistant, _assistant.Content.OfType<ToolUseBlock>().Where(call => _completed.ContainsKey(call.ToolUseId)).Select(call => _completed[call.ToolUseId]).ToArray(), _questions.Values.ToArray(), _responses.Values.ToArray(), _executions.Values.ToArray()) { AssistantMessageIsIndependent = AssistantMessageIsIndependent, SourceHistoryOwned = _sourceHistoryOwned, Context = _context };
        }
    }
}
