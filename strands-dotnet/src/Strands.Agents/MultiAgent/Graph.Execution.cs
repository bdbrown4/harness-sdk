using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.MultiAgent;

public sealed partial class Graph
{
    private sealed class Execution : IDisposable
    {
        private readonly Graph _graph;
        private readonly MultiAgentInput _input;
        private readonly MultiAgentState _state;
        private readonly IReadOnlyDictionary<string, object?> _caller;
        private readonly CancellationTokenSource _operation;
        private readonly CancellationTokenSource? _deadline;
        private readonly CancellationToken _externalCancellation;
        private readonly EventQueue _queue = new();
        private readonly Queue<Node> _ready;
        private readonly Dictionary<string, Task<Visit>> _active = new(StringComparer.Ordinal);
        private readonly List<Task<Visit>> _all = [];
        private readonly List<Node> _transitions;
        private readonly Queue<IMultiAgentStreamEvent> _notifications = new();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<InterruptResponse>> _responses;
        private readonly MultiAgentTelemetry.Invocation _trace;
        private bool _paused;
        private readonly CheckpointJournal? _checkpoints;
        private string? _committingNode;
        internal IReadOnlyList<string> PendingTargets => Array.AsReadOnly(_ready.Select(node => node.Id).Concat(_active.Keys).Concat(_committingNode is null ? [] : new[] { _committingNode }).Distinct(StringComparer.Ordinal).ToArray());
        internal IReadOnlyList<string> PendingTransitions => Array.AsReadOnly(_transitions.Select(node => node.Id).ToArray());
        internal MultiAgentSnapshot? RecoveryCheckpoint => _checkpoints is { } journal && (PendingTargets.Count > 0 || _transitions.Count > 0) ? journal.Capture(_graph, _state, _input, PendingTargets, PendingTransitions) : null;

        internal Execution(Graph graph, MultiAgentInput input, MultiAgentState state, IReadOnlyDictionary<string, object?> caller, CancellationToken cancellationToken, IReadOnlyList<Node> ready, IReadOnlyDictionary<string, IReadOnlyList<InterruptResponse>> responses, IReadOnlyList<string> transitions, MultiAgentTelemetry.Invocation trace)
        {
            _graph = graph; _input = input; _state = state; _caller = caller; _externalCancellation = cancellationToken; _ready = new(ready); _responses = responses; _trace = trace;
            _transitions = transitions.Select(id => graph.Nodes[id]).ToList();
            if (graph._options.SessionStore is not null) _checkpoints = new(state);
            if (graph._options.Timeout is { } timeout) { _deadline = new(); _deadline.CancelAfter(timeout); }
            _operation = _deadline is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        }

        internal async ValueTask<EventQueue.Pending?> NextAsync()
        {
            try { return await NextCoreAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { CheckCancellation(); throw; }
        }

        private void CheckCancellation()
        {
            _externalCancellation.ThrowIfCancellationRequested();
            if (_deadline?.IsCancellationRequested == true) throw new GraphLimitException("timeout", "The graph exceeded its wall-clock budget.");
            _operation.Token.ThrowIfCancellationRequested();
        }

        private async ValueTask<EventQueue.Pending?> NextCoreAsync()
        {
            while (true)
            {
                CheckCancellation();
                await ResolveTransitionsAsync().ConfigureAwait(false);
                if (_notifications.Count > 0) return new(_notifications.Dequeue());
                while (!_paused && _ready.Count > 0 && _active.Count < _graph._options.MaxConcurrency)
                {
                    if (_graph._options.MaxSteps is { } limit && _state.Steps >= limit)
                    {
                        if (_active.Count == 0) throw new GraphLimitException("steps", $"The graph exhausted its {limit} node-visit budget.");
                        break;
                    }
                    var node = _ready.Dequeue();
                    _checkpoints?.Admit(node.Id, _state.Nodes[node.Id]);
                    _state.Steps = checked(_state.Steps + 1);
                    _state.Nodes[node.Id].Status = Status.Executing;
                    var task = RunAndSignalAsync(node);
                    _active.Add(node.Id, task);
                    _all.Add(task);
                }
                if (_active.Count == 0) return null;
                var update = await _queue.ReadAsync(_operation.Token).ConfigureAwait(false);
                CheckCancellation();
                if (update.Event is not null) return update;
                var completed = update.Node!;
                _committingNode = completed.Id;
                _active.Remove(completed.Id);
                _checkpoints?.Commit(completed.Id, _state.Nodes[completed.Id], update.Result!);
                if (update.Result!.Status == Status.Completed) { _transitions.Add(completed); _committingNode = null; }
                if (_state.Nodes[completed.Id].HasPendingExecution) _paused = true;
                if (update.Visit?.FatalError is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();
                if (update.Result!.Status == Status.Failed && _graph._options.NodeFailure == NodeFailurePolicy.FailFast)
                    ExceptionDispatchInfo.Capture(update.Result.Error ?? new InvalidOperationException($"Node {completed.Id} returned a failed result.")).Throw();
                await ResolveTransitionsAsync().ConfigureAwait(false);
                if (_checkpoints is { } journal)
                {
                    _committingNode = null;
                    if (_graph._options.SaveLatestOn == MultiAgentSaveLatestStrategy.Node)
                        await _graph._options.SessionStore!.SaveAsync(_graph._options.SessionId!, journal.Capture(_graph, _state, _input, PendingTargets, PendingTransitions), _operation.Token).ConfigureAwait(false);
                }
                _committingNode = null;
            }
        }

        private async ValueTask ResolveTransitionsAsync()
        {
            if (_transitions.Count == 0 || _graph._options.Scheduling == GraphScheduling.CompletedBatches && (_active.Count > 0 || _ready.Count > 0)) return;
            await EnqueueReadyAsync(_transitions).ConfigureAwait(false);
            _transitions.Clear();
        }

        private async ValueTask EnqueueReadyAsync(IReadOnlyList<Node> completed)
        {
            var completedIds = new HashSet<string>(completed.Select(node => node.Id), StringComparer.Ordinal);
            var targets = new List<string>();
            foreach (var target in _graph._options.Edges.Where(edge => completedIds.Contains(edge.Source)).Select(edge => edge.Target).Distinct(StringComparer.Ordinal).Select(id => _graph.Nodes[id]))
            {
                if (_active.ContainsKey(target.Id) || _ready.Any(node => node.Id == target.Id)) continue;
                var satisfied = _graph._options.Dependencies == DependencyPolicy.AllSatisfied;
                foreach (var edge in _graph._options.Edges.Where(edge => edge.Target == target.Id))
                {
                    if (_graph._options.Dependencies == DependencyPolicy.AnySatisfied && !completedIds.Contains(edge.Source)) continue;
                    var accepted = !_active.ContainsKey(edge.Source) && _state.Nodes[edge.Source].Status == Status.Completed && (edge.Condition is null || await edge.Condition(_state, _caller, _operation.Token).ConfigureAwait(false));
                    if (_graph._options.Dependencies == DependencyPolicy.AllSatisfied && !accepted) { satisfied = false; break; }
                    if (_graph._options.Dependencies == DependencyPolicy.AnySatisfied && accepted) { satisfied = true; break; }
                }
                if (satisfied) { _ready.Enqueue(target); targets.Add(target.Id); }
            }
            if (targets.Count > 0) _notifications.Enqueue(new MultiAgentHandoffEvent(Array.AsReadOnly(completed.Select(node => node.Id).ToArray()), Array.AsReadOnly(targets.ToArray()), _state, _caller));
        }

        private async Task<Visit> RunAndSignalAsync(Node node)
        {
            Exception? preparationFailure = null;
            try
            {
                if (node is AgentNode agentNode)
                    await agentNode.PrepareConstructionRevisitAsync(_state.Nodes[node.Id], _operation.Token).ConfigureAwait(false);
            }
            catch (Exception error) { preparationFailure = error; }
            using var trace = _trace.BeginVisit(node);
            var timer = Stopwatch.StartNew();
            NodeResultUpdateEvent? output = null;
            Exception? failure = null;
            Exception? fatal = null;
            var cleanupErrors = new List<Exception>();
            var hookCancelled = false;
            var timeout = node is AgentNode agent ? agent.Options.Timeout ?? _graph._options.NodeTimeout : _graph._options.NodeTimeout;
            using var deadline = timeout is null ? null : new CancellationTokenSource();
            using var nodeOperation = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, deadline.Token);
            var nodeToken = nodeOperation?.Token ?? _operation.Token;
            try
            {
                if (preparationFailure is not null) ExceptionDispatchInfo.Capture(preparationFailure).Throw();
                var before = new BeforeNodeCallEvent(_graph, node.Id, _state, _caller);
                var nodeState = _state.Nodes[node.Id];
                using var hookScope = new NodeHookScope(node.Id, nodeState, _operation.Token);
                before.Attach(hookScope);
                try { await _graph._hooks.InvokeAsync(before, _operation.Token).ConfigureAwait(false); }
                catch (InterruptException interrupted)
                {
                    if (interrupted.Interrupts.Any(question => question.Source != InterruptSource.OrchestratorHook || question.ToolUseId != node.Id || nodeState.HookPause?.Interrupts.Any(known => known.Id == question.Id) != true))
                        throw new InvalidOperationException("Node callback interruptions must originate from their active event helper.", interrupted);
                    hookCancelled = true;
                    output = new([], status: Status.Interrupted, interrupts: nodeState.PendingInterrupts);
                }
                catch (Exception error) { if (error is not OperationCanceledException || !_operation.IsCancellationRequested) fatal = error; throw; }
                finally { hookScope.Dispose(); }
                await _queue.SendAsync(before, _operation.Token, true).ConfigureAwait(false);
                if (output is null)
                {
                    if (before.CancelReason is { } reason)
                    {
                        hookCancelled = true;
                        output = new([new TextBlock(reason)], status: Status.Cancelled);
                        await _queue.SendAsync(new NodeCancelEvent(node.Id, reason, _state, _caller), _operation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        if (timeout is { } limit) deadline!.CancelAfter(limit);
                        var body = await ReadBodyAsync(node, nodeToken, trace).ConfigureAwait(false);
                        output = body.Output; failure = body.Error; cleanupErrors.AddRange(body.Cleanup);
                    }
                }
                if (failure is null) nodeToken.ThrowIfCancellationRequested();
                if (failure is null && output?.Status is Status.Completed or Status.Cancelled) nodeState.HookPause = null;
            }
            catch (Exception error) { failure = error; }
            if (deadline?.IsCancellationRequested == true && !_operation.IsCancellationRequested)
            {
                var exceeded = new GraphLimitException("nodeTimeout", $"Node {node.Id} exceeded its wall-clock budget.");
                if (failure is not null && failure is not OperationCanceledException) AttachCleanup(exceeded, failure);
                foreach (var cleanup in cleanupErrors) AttachCleanup(exceeded, cleanup);
                fatal = failure = exceeded;
            }
            var priorUsage = _state.Nodes[node.Id].IsResuming ? _state.Nodes[node.Id].Results.LastOrDefault()?.Usage ?? new() : new Usage();
            var result = failure is null
                ? new NodeResult(node.Id, output!.Status, output.Content, output.Usage + (node is MultiAgentNode ? new Usage() : priorUsage), timer.Elapsed, output.Error, output.StructuredOutput, output.Interrupts, output.AgentResult, output.MultiAgentResult)
                : new NodeResult(node.Id, _operation.IsCancellationRequested ? Status.Cancelled : Status.Failed, usage: priorUsage, duration: timer.Elapsed, error: failure);
            _state.Add(result);
            try { await _queue.SendAsync(new NodeResultEvent(result, _state, _caller), _operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_operation.IsCancellationRequested) { }
            if (hookCancelled) trace.DeferEnd(result, failure, true);
            else trace.End(result, failure);
            var after = new AfterNodeCallEvent(_graph, result, _state, _caller);
            try { await _graph._hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { cleanupErrors.Add(cleanup); if (fatal is null) fatal = cleanup; else AttachCleanup(fatal, cleanup); }
            try { await _queue.SendAsync(after, _operation.Token, true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_operation.IsCancellationRequested) { }
            if (hookCancelled) trace.Complete(fatal);
            var visit = new Visit(result, fatal, cleanupErrors);
            _queue.CompleteNode(node, visit);
            return visit;
        }

        private async Task<(NodeResultUpdateEvent? Output, Exception? Error, IReadOnlyList<Exception> Cleanup)> ReadBodyAsync(Node node, CancellationToken cancellationToken, MultiAgentTelemetry.Visit trace)
        {
            IAsyncEnumerator<INodeStreamEvent>? iterator = null;
            NodeResultUpdateEvent? output = null;
            Exception? failure = null;
            var cleanupErrors = new List<Exception>();
            try
            {
                var input = _responses.TryGetValue(node.Id, out var responses) && _state.Nodes[node.Id].HasPendingBody ? MultiAgentInput.Resume(responses) : await _graph.ResolveInputAsync(node, _input, _state, _caller, cancellationToken).ConfigureAwait(false);
                var stream = trace.Run(() => node is AgentNode agent
                    ? agent.HandleGraphAsync(input, new(_state, _caller, _graph._options.AgentContext), cancellationToken)
                    : node.HandleAsync(input, new(_state, _caller, _graph._options.AgentContext), cancellationToken)) ?? throw new InvalidOperationException("A node returned a null stream.");
                iterator = trace.Run(() => stream.GetAsyncEnumerator(cancellationToken)) ?? throw new InvalidOperationException("A node returned a null iterator.");
                while (await trace.RunAsync(iterator.MoveNextAsync).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output is not null) throw new InvalidOperationException("A node emitted content after its terminal result.");
                    switch (iterator.Current)
                    {
                        case NodeStreamEvent progress:
                            var forwarded = node is MultiAgentNode && progress is { Source: NodeStreamSource.MultiAgent, Value: NodeStreamUpdateEvent nested }
                                ? nested : new NodeStreamUpdateEvent(node.Id, node.Type, _state, progress.Value, progress.Source, _caller);
                            await _queue.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
                            break;
                        case NodeResultUpdateEvent terminal: output = terminal; break;
                        default: throw new InvalidOperationException("A node emitted a null or unsupported event.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (output is null) throw new InvalidOperationException("A node completed without a terminal result.");
                if (node is not (AgentNode or MultiAgentNode))
                    _state.Nodes[node.Id].DeferredResponses = Array.AsReadOnly(_state.Nodes[node.Id].DeferredResponses.Where(response => output.Interrupts.Any(question => question.Id == response.InterruptId)).ToArray());
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (iterator is not null)
                    try { await trace.RunAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
                    catch (Exception cleanup) { cleanupErrors.Add(cleanup); if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            }
            return (output, failure, cleanupErrors);
        }

        internal async ValueTask CloseAsync(Exception? primary)
        {
            var failure = primary;
            var aborted = primary is not null || _active.Count > 0 || _ready.Count > 0;
            try { if (_all.Any(task => !task.IsCompleted)) _operation.Cancel(); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            foreach (var task in _all)
                try
                {
                    var visit = await task.ConfigureAwait(false);
                    if (aborted)
                        foreach (var cleanup in visit.CleanupErrors) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
                    if (visit.FatalError is { } fatal) { if (failure is null) failure = fatal; else AttachCleanup(failure, fatal); }
                }
                catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose() { _queue.Dispose(); _operation.Dispose(); _deadline?.Dispose(); }
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }

    private sealed record Visit(NodeResult Result, Exception? FatalError, IReadOnlyList<Exception> CleanupErrors);

    private sealed class EventQueue : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Pending> _items = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly SemaphoreSlim _capacity = new(1, 1);
        internal async ValueTask SendAsync(IMultiAgentStreamEvent item, CancellationToken cancellationToken, bool hooksInvoked = false)
        {
            await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            var pending = new Pending(item, hooksInvoked);
            Enqueue(pending);
            using (cancellationToken.Register(() => pending.Cancel(cancellationToken))) await pending.Completion.ConfigureAwait(false);
        }
        internal void CompleteNode(Node node, Visit visit) => Enqueue(new(node, visit));
        private void Enqueue(Pending pending) { lock (_gate) _items.Enqueue(pending); _available.Release(); }
        internal async ValueTask<Pending> ReadAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                var pending = _items.Dequeue();
                if (pending.Event is not null) _capacity.Release();
                return pending;
            }
        }
        public void Dispose() { _available.Dispose(); _capacity.Dispose(); }
        internal sealed class Pending
        {
            private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Pending(IMultiAgentStreamEvent item, bool hooksInvoked = false) { Event = item; HooksInvoked = hooksInvoked; }
            internal Pending(Node node, Visit visit) { Node = node; Visit = visit; }
            internal IMultiAgentStreamEvent? Event { get; }
            internal bool HooksInvoked { get; }
            internal Node? Node { get; }
            internal Visit? Visit { get; }
            internal NodeResult? Result => Visit?.Result;
            internal Task Completion => _completion.Task;
            internal void Acknowledge() => _completion.TrySetResult(true);
            internal void Cancel(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);
        }
    }
}
