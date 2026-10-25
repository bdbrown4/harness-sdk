using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    private async IAsyncEnumerable<IMultiAgentStreamEvent> StreamNodeAsync(AgentNode node, MultiAgentInput input, MultiAgentState state, IReadOnlyDictionary<string, object?> caller, MultiAgentTelemetry.Invocation trace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var visit = trace.BeginVisit(node);
        var timer = Stopwatch.StartNew();
        IAsyncEnumerator<INodeStreamEvent>? iterator = null;
        NodeResultUpdateEvent? output = null;
        NodeResult? result = null;
        Exception? primary = null;
        var hookCancelled = false;
        AfterNodeCallEvent? after = null;
        var nodeState = state.Nodes[node.Id];
        var priorUsage = nodeState.IsResuming ? nodeState.Results.LastOrDefault()?.Usage ?? new() : new Usage();
        ToolTurn? handoffs = null;
        if (_options.Routing == SwarmRoutingMode.Tool)
        {
            if (!nodeState.IsResuming || !_toolTurns.TryGetValue(node.Id, out handoffs)) _toolTurns[node.Id] = handoffs = new();
        }
        nodeState.Status = Status.Executing;
        try
        {
            var before = new BeforeNodeCallEvent(this, node.Id, state, caller);
            using var hookScope = new NodeHookScope(node.Id, nodeState, cancellationToken);
            before.Attach(hookScope);
            try { await _hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false); }
            catch (InterruptException interrupted)
            {
                if (interrupted.Interrupts.Any(question => question.Source != InterruptSource.OrchestratorHook || question.ToolUseId != node.Id || nodeState.HookPause?.Interrupts.Any(known => known.Id == question.Id) != true))
                    throw new InvalidOperationException("Node callback interruptions must originate from their active helper.", interrupted);
                hookCancelled = true;
                output = new(status: Status.Interrupted, interrupts: nodeState.PendingInterrupts);
            }
            catch (Exception error) { primary = error; throw; }
            finally { hookScope.Dispose(); }
            yield return before;
            if (output is null && before.CancelReason is { } reason)
            {
                hookCancelled = true;
                output = new([new TextBlock(reason)], status: Status.Cancelled);
                var denied = new NodeCancelEvent(node.Id, reason, state, caller);
                try { await _hooks.InvokeAsync(denied, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                yield return denied;
            }
            else if (output is null)
            {
                using var deadline = StartDeadline(node.Options.Timeout ?? _options.NodeTimeout);
                using var operation = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                var nodeToken = operation?.Token ?? cancellationToken;
                try { iterator = visit.Run(() => node.HandleScopedAsync(input, new(state, caller, _options.AgentContext), handoffs is null ? HandoffSchema(node.Id) : null, handoffs is null ? [] : [new HandoffTool(this, handoffs)], nodeToken).GetAsyncEnumerator(nodeToken)); }
                catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "nodeTimeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                while (true)
                {
                    bool moved;
                    INodeStreamEvent? current = null;
                    try
                    {
                        moved = await visit.RunAsync(iterator.MoveNextAsync).ConfigureAwait(false); nodeToken.ThrowIfCancellationRequested();
                        if (moved) { if (output is not null) throw new InvalidOperationException("A swarm node emitted content after its terminal result."); current = iterator.Current; }
                    }
                    catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "nodeTimeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                    if (!moved) break;
                    if (current is NodeResultUpdateEvent terminal) output = terminal;
                    else if (current is NodeStreamEvent progress)
                    {
                        handoffs?.Observe(progress.Value);
                        var forwarded = new NodeStreamUpdateEvent(node.Id, node.Type, state, progress.Value, progress.Source, caller);
                        try { await _hooks.InvokeAsync(forwarded, nodeToken).ConfigureAwait(false); }
                        catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "nodeTimeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                        yield return forwarded;
                    }
                    else { primary = new InvalidOperationException("A swarm node emitted an unsupported event."); throw primary; }
                }
                try { await visit.RunAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); nodeToken.ThrowIfCancellationRequested(); }
                catch (Exception error) { primary = ClassifyDeadline(error, cancellationToken, deadline, "nodeTimeout"); ExceptionDispatchInfo.Capture(primary).Throw(); throw; }
                finally { iterator = null; }
            }
            if (output is null) { primary = new InvalidOperationException("A swarm node completed without a terminal result."); throw primary; }
            try
            {
                if (output.Status == Status.Interrupted && nodeState.InterruptedAgentSnapshot is { } paused) handoffs?.Pause(paused);
                if (output.Status == Status.Interrupted && _options.ContextCommit == SwarmContextCommit.AcceptedResults) _ = handoffs?.Commit(SharedContext, node.Id);
                if (output.Status == Status.Completed && handoffs?.Commit(SharedContext, node.Id) is { } decision)
                    output = new(output.Content, output.Usage, output.Status, output.Error, decision, output.Interrupts);
                if (output.Status is Status.Completed or Status.Cancelled) nodeState.HookPause = null;
                result = new(node.Id, output.Status, output.Content, output.Usage + priorUsage, timer.Elapsed, output.Error, output.StructuredOutput, output.Interrupts);
                state.Add(result);
                await CommitVisitAsync(result, state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) { primary = error; throw; }
            var completed = new NodeResultEvent(result, state, caller);
            try { await _hooks.InvokeAsync(completed, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { primary = error; throw; }
            yield return completed;
        }
        finally
        {
            var failure = primary;
            if (iterator is not null)
                try { await visit.RunAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
                catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (result is null)
            {
                result = new(node.Id, failure is null || cancellationToken.IsCancellationRequested ? Status.Cancelled : Status.Failed, usage: priorUsage, duration: timer.Elapsed, error: failure);
                state.Add(result);
            }
            after = new(this, result, state, caller);
            if (hookCancelled) visit.DeferEnd(result, failure, true);
            else visit.End(result, failure);
            try { await _hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (hookCancelled && failure is not null) visit.Complete(failure);
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        yield return after;
        if (hookCancelled) visit.Complete();
    }
}
