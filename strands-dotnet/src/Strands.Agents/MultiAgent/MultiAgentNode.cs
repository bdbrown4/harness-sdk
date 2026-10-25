using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.MultiAgent;

/// <summary>A borrowed nested orchestrator. Inner node-progress identities are preserved.</summary>
public sealed class MultiAgentNode : Node
{
    /// <summary>Wraps a graph or other typed orchestrator without transferring ownership.</summary>
    public MultiAgentNode(IMultiAgent orchestrator, string? id = null, string? description = null) : base(id ?? orchestrator?.Id ?? throw new ArgumentNullException(nameof(orchestrator)), description) => Orchestrator = orchestrator;
    /// <summary>The borrowed nested orchestrator.</summary>
    public IMultiAgent Orchestrator { get; }
    /// <inheritdoc />
    public override string Type => "multiAgentNode";
    /// <inheritdoc />
    public override async IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<IMultiAgentStreamEvent>? iterator = null;
        Exception? primary = null;
        MultiAgentResult? result = null;
        var ownsPendingExecution = false;
        try
        {
            try
            {
                if (input.IsResume && context.State.Node(Id)?.InterruptedMultiAgentSnapshot is { } paused)
                {
                    if (Orchestrator is not ICheckpointableMultiAgent checkpointable) throw new NotSupportedException("The nested orchestrator cannot restore its captured paused execution.");
                    await checkpointable.LoadSnapshotAsync(paused, cancellationToken).ConfigureAwait(false);
                }
                else if (!input.IsResume && Orchestrator is IInterruptibleMultiAgent { CanResume: true })
                    throw new InvalidOperationException("A nested orchestrator already paused outside this graph cannot accept a new node task.");
                ownsPendingExecution = true;
                iterator = Orchestrator.StreamAsync(input, new() { InvocationState = context.InvocationState }, cancellationToken)?.GetAsyncEnumerator(cancellationToken)
                    ?? throw new InvalidOperationException("The nested orchestrator returned a null stream or iterator.");
            }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool next;
                IMultiAgentStreamEvent? current = null;
                try
                {
                    next = await iterator.MoveNextAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (next)
                    {
                        if (result is not null) throw new InvalidOperationException("The nested orchestrator emitted content after its terminal result.");
                        current = iterator.Current ?? throw new InvalidOperationException("The nested orchestrator emitted a null event.");
                        if (current is MultiAgentResultEvent terminal) result = terminal.Result ?? throw new InvalidOperationException("The nested orchestrator emitted a null result.");
                    }
                }
                catch (Exception error) { primary = error; throw; }
                if (!next) break;
                yield return new NodeStreamEvent(current, NodeStreamSource.MultiAgent);
            }
            if (result is null) { primary = new InvalidOperationException("The nested orchestrator completed without a terminal result."); throw primary; }
            yield return new NodeResultUpdateEvent(result.Content, result.Usage, result.Status, result.Error ?? result.Results.FirstOrDefault(value => value.Status == Status.Failed)?.Error,
                null, result.Status == Status.Interrupted ? result.Interrupts : [], null, result);
        }
        finally
        {
            var failure = primary;
            if (iterator is not null)
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    if (failure is null) failure = cleanup;
                    else if (failure is { } existing && !ReferenceEquals(existing, cleanup))
                    {
                        if (existing.Data["Strands.CleanupErrors"] is not List<Exception> errors) existing.Data["Strands.CleanupErrors"] = errors = [];
                        errors.Add(cleanup);
                    }
                }
            try
            {
                if (ownsPendingExecution && context.State.Node(Id) is { } nodeState)
                {
                    var pending = Orchestrator is IInterruptibleMultiAgent resumable ? resumable.CanResume : result?.Status == Status.Interrupted;
                    nodeState.InterruptedMultiAgentSnapshot = pending && Orchestrator is ICheckpointableMultiAgent checkpointable
                        ? await checkpointable.TakeSnapshotAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false) : null;
                    nodeState.PendingInterrupts = NodeResult.CopyInterrupts(Orchestrator is IInterruptibleMultiAgent interrupted ? interrupted.PendingInterrupts : result?.Interrupts ?? []);
                    var unanswered = new HashSet<string>(nodeState.PendingBodyInterrupts.Select(question => question.Id), StringComparer.Ordinal);
                    nodeState.DeferredResponses = pending ? Array.AsReadOnly(nodeState.DeferredResponses.Where(response => unanswered.Contains(response.InterruptId)).ToArray()) : [];
                }
            }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup))
                {
                    if (failure.Data["Strands.CleanupErrors"] is not List<Exception> errors) failure.Data["Strands.CleanupErrors"] = errors = [];
                    errors.Add(cleanup);
                }
            }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
