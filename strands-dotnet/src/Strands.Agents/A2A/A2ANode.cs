using System.Runtime.CompilerServices;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.A2A;

/// <summary>A graph node borrowing a real A2A client. Stable remote pauses are scoped to the graph invocation rather than this shared declaration.</summary>
public sealed partial class A2ANode : Node
{
    private readonly A2AClient _client;
    /// <summary>Creates a remote node without network I/O. Disposing its graph does not cancel or dispose the remote service or client.</summary>
    public A2ANode(A2AClient client, string? id = null, A2ANodeOptions? options = null) : base(id ?? client?.DiscoveryUrl.AbsoluteUri ?? throw new ArgumentNullException(nameof(client)))
    {
        Guard.NotNull(client, nameof(client)); _client = client; Options = options ?? new();
        Guard.NotNull(Options.AgentOptions, nameof(options));
        if (Options.MaxCheckpointBytes <= 0 || Options.MaxQuestions <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _ = new A2AAgent(client, Options.AgentOptions);
    }
    /// <summary>The immutable node limits and projection settings.</summary>
    public A2ANodeOptions Options { get; }
    /// <inheritdoc />
    public override string Type => "a2aNode";
    /// <inheritdoc />
    public override IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken = default)
        => HandleCoreAsync(input, context, cancellationToken);

    private async IAsyncEnumerable<INodeStreamEvent> HandleCoreAsync(MultiAgentInput input, NodeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guard.NotNull(input, nameof(input)); Guard.NotNull(context, nameof(context));
        var nodeState = context.State.Node(Id) ?? throw new ArgumentException("This node does not belong to the active graph invocation.", nameof(context));
        var pause = nodeState.InterruptedNodeSnapshot is { } snapshot ? ReadPause(snapshot) : null;
        if (input.IsResume != (pause is not null)) throw new InvalidOperationException("Only the captured paused remote task can accept response-only input.");
        var bound = await _client.BindEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (pause is not null && pause.Binding != bound.Binding) throw new InvalidDataException("The discovered endpoint no longer matches the paused remote node.");
        var responses = input.InterruptResponses;
        if (pause is not null)
        {
            var task = await bound.Client.GetTaskAsync(pause.TaskId, historyLength: 0, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (A2AJson.RequiredString(task, "contextId") != pause.ContextId) throw new A2AProtocolException("The remote task context no longer matches its captured pause.");
            var remoteState = A2AJson.RequiredString(A2AJson.RequiredObject(task, "status"), "state");
            if (remoteState is not ("completed" or "failed" or "rejected" or "canceled" or "input-required" or "auth-required"))
                throw new A2AProtocolException("The previously paused remote task is active; its answers must not be replayed while their acceptance is uncertain.");
            if (IsPause(task))
            {
                if (pause.UncertainGenericSend) throw new A2AProtocolException("An unstructured remote answer has uncertain acceptance. The host must resolve that task explicitly; the graph will not resend its text automatically.");
                pause = CapturePause(task, bound.Binding, pause);
                RetainPause(nodeState, pause);
                responses = responses.Where(response => pause.Questions.Any(question => question.Id == response.InterruptId)).ToArray();
            }
            if (!IsPause(task) || responses.Count == 0)
            {
                var accumulator = new A2AResultAccumulator(Options.AgentOptions); accumulator.Observe(task);
                var observed = accumulator.Build(context.InvocationState);
                yield return new NodeStreamEvent(new A2AStreamUpdateEvent(task), NodeStreamSource.Agent);
                cancellationToken.ThrowIfCancellationRequested();
                yield return new NodeStreamEvent(observed, NodeStreamSource.Agent);
                cancellationToken.ThrowIfCancellationRequested();
                var status = RemoteStatus(remoteState);
                if (status != Status.Interrupted) ClearPending(nodeState);
                yield return new NodeResultUpdateEvent(observed.Result.Message?.Content ?? [], status: status, interrupts: status == Status.Interrupted ? Questions(pause) : []);
                yield break;
            }
        }
        var content = pause is null ? input.Content : ResponseContent(pause, responses);
        var answeredGeneric = pause is not null && pause.Questions.Any(question => question.RemoteId is null && responses.Any(response => response.InterruptId == question.Id));
        var agent = new A2AAgent(bound.Client, Options.AgentOptions);
        var invocation = new A2AInvocationOptions { InvocationState = context.InvocationState, TaskId = pause?.TaskId, ContextId = pause?.ContextId };
        var iterator = (pause is null ? agent.StreamContentAsync(content, invocation, cancellationToken) : agent.StreamProtocolContentAsync(content, invocation, cancellationToken)).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null; A2AResultEvent? result = null; var receivedPause = false;
        try
        {
            while (true)
            {
                bool next; IAgentStreamEvent? current = null;
                try { cancellationToken.ThrowIfCancellationRequested(); next = await iterator.MoveNextAsync().ConfigureAwait(false); if (next) current = iterator.Current; }
                catch (Exception error) { primary = error; throw; }
                if (!next) break;
                try
                {
                    if (current is A2AResultEvent terminal) result = terminal;
                    if (current is A2AStreamUpdateEvent update && IsPause(update.Event))
                    {
                        pause = CapturePause(update.Event, bound.Binding, pause, preserveGeneric: !answeredGeneric);
                        RetainPause(nodeState, pause);
                        receivedPause = true;
                    }
                }
                catch (Exception error) { primary = error; throw; }
                yield return new NodeStreamEvent(current, NodeStreamSource.Agent);
            }
            NodeResultUpdateEvent output;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result is null) throw new A2AProtocolException("The remote graph node did not receive a terminal projection.");
                var status = RemoteStatus(result.TaskState);
                if (status == Status.Interrupted && pause is null) throw new A2AProtocolException("A remote pause requires its captured task identifiers.");
                if (status != Status.Interrupted) ClearPending(nodeState);
                output = new(result.Result.Message?.Content ?? [], result.Result.Usage, status, interrupts: status == Status.Interrupted ? Questions(pause!) : []);
            }
            catch (Exception error) { primary = error; throw; }
            yield return output;
        }
        finally
        {
            var throwAfterCleanup = false;
            if (answeredGeneric && !receivedPause && result is null && pause is not null)
                try { RetainPause(nodeState, pause with { UncertainGenericSend = true }); }
                catch (Exception cleanup) { if (primary is null) { primary = cleanup; throwAfterCleanup = true; } else A2AHttp.AddCleanup(primary, cleanup); }
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { if (primary is null) throw; A2AHttp.AddCleanup(primary, cleanup); }
            if (throwAfterCleanup) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary!).Throw();
        }
    }
    private static Status RemoteStatus(string? state) => state switch { "failed" or "rejected" => Status.Failed, "canceled" => Status.Cancelled, "input-required" or "auth-required" => Status.Interrupted, _ => Status.Completed };
    private static void ClearPending(NodeState state) { state.InterruptedNodeSnapshot = null; state.PendingInterrupts = []; state.DeferredResponses = []; }
    private static bool IsPause(System.Text.Json.JsonElement value) => value.TryGetProperty("status", out var status) && A2AJson.OptionalString(status, "state") is "input-required" or "auth-required";
}
