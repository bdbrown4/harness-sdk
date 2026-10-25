using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.MultiAgent;

/// <summary>How a borrowed agent's state behaves across node visits.</summary>
public enum AgentContextPolicy
{
    /// <summary>Snapshot before each execution and restore afterward, matching TypeScript's default.</summary>
    RestoreAfterExecution,
    /// <summary>Retain messages and state across visits, matching Python's graph default.</summary>
    Retain,
    /// <summary>Restore the orchestrator-supplied baseline before visits, capturing first-entry state when absent. Swarm supplies its initialization baseline; Graph captures per invocation.</summary>
    ResetOnRevisit,
    /// <summary>Capture node-construction messages, application state and model state for restoration before completed graph revisits.</summary>
    ResetToConstructionBaselineOnRevisit = 3
}
/// <summary>Per-agent-node invocation settings.</summary>
public sealed record AgentNodeOptions
{
    /// <summary>An optional state-isolation policy overriding the graph's default.</summary>
    public AgentContextPolicy? ContextPolicy { get; init; }
    /// <summary>A cooperative deadline overriding the graph's node deadline.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Optional single-agent invocation limits; graph caller state always takes precedence.</summary>
    public InvocationOptions InvocationOptions { get; init; } = new();
    /// <summary>An optional JSON structured response schema and validation configuration.</summary>
    public StructuredOutputOptions<JsonElement>? StructuredOutput { get; init; }
}
/// <summary>A node that borrows a native agent and streams its events with graph identity.</summary>
public sealed class AgentNode : Node
{
    private static readonly JsonSerializerOptions ConstructionSnapshotJson = new(JsonSerializerDefaults.Web);
    private AgentSnapshot? _constructionBaseline;

    internal bool HasConstructionBaseline => _constructionBaseline is not null;

    internal void CaptureConstructionBaseline()
    {
        if (Options.ContextPolicy != AgentContextPolicy.ResetToConstructionBaselineOnRevisit) return;
        _constructionBaseline = new(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["messages"] = JsonSerializer.SerializeToElement(Agent.Messages, ConstructionSnapshotJson),
            ["state"] = JsonSerializer.SerializeToElement(Agent.State.GetAll(), ConstructionSnapshotJson),
            ["modelState"] = JsonSerializer.SerializeToElement(Agent.ModelState.GetAll(), ConstructionSnapshotJson)
        });
    }

    internal ValueTask PrepareConstructionRevisitAsync(NodeState state, CancellationToken cancellationToken)
    {
        if (Options.ContextPolicy != AgentContextPolicy.ResetToConstructionBaselineOnRevisit ||
            state.Results.LastOrDefault()?.Status != Status.Completed) return default;
        return Agent.LoadSnapshotAsync(_constructionBaseline ?? throw new InvalidOperationException("The graph node has no construction baseline."), cancellationToken);
    }

    /// <summary>Wraps an inactive borrowed agent. The default identifier is its stable agent ID.</summary>
    public AgentNode(Agent agent, string? id = null, AgentNodeOptions? options = null) : base(id ?? agent?.Id ?? throw new ArgumentNullException(nameof(agent)), agent.Description)
    {
        Agent = agent; Options = options ?? new();
        if (Options.ContextPolicy is { } policy && !Enum.IsDefined(typeof(AgentContextPolicy), policy)) throw new ArgumentOutOfRangeException(nameof(options));
        if (Options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(options));
        Guard.NotNull(Options.InvocationOptions, nameof(options));
        if (Options.StructuredOutput is { } structured) Options = Options with { StructuredOutput = structured with { Schema = structured.Schema.Clone() } };
    }
    /// <summary>The borrowed agent.</summary>
    public Agent Agent { get; }
    /// <summary>The copied execution configuration.</summary>
    public AgentNodeOptions Options { get; }
    /// <inheritdoc />
    public override string Type => "agentNode";
    /// <inheritdoc />
    public override IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken = default) =>
        HandleCoreAsync(input, context, Options.InvocationOptions, Options.StructuredOutput, cancellationToken);

    internal IAsyncEnumerable<INodeStreamEvent> HandleGraphAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken) =>
        HandleCoreAsync(input, context, Options.InvocationOptions, Options.StructuredOutput, cancellationToken, graphDispatch: true);

    internal IAsyncEnumerable<INodeStreamEvent> HandleScopedAsync(MultiAgentInput input, NodeContext context, StructuredOutputOptions<JsonElement>? structuredOutput, IReadOnlyList<ITool> tools, CancellationToken cancellationToken) =>
        HandleCoreAsync(input, context, Options.InvocationOptions with { EphemeralTools = Options.InvocationOptions.EphemeralTools.Concat(tools).ToArray() }, structuredOutput, cancellationToken);

    private async IAsyncEnumerable<INodeStreamEvent> HandleCoreAsync(MultiAgentInput input, NodeContext context, InvocationOptions invocationOptions, StructuredOutputOptions<JsonElement>? structuredOutputOptions, [EnumeratorCancellation] CancellationToken cancellationToken, bool graphDispatch = false)
    {
        Guard.NotNull(input, nameof(input));
        Guard.NotNull(context, nameof(context));
        AgentSnapshot? restore = null;
        var snapshotOptions = new SnapshotOptions { Preset = SnapshotPreset.Session };
        var contextPolicy = Options.ContextPolicy ?? context.ContextPolicy;
        if (contextPolicy == AgentContextPolicy.ResetToConstructionBaselineOnRevisit && !graphDispatch)
            throw new InvalidOperationException("Construction-baseline reset requires graph execution; standalone agent-node execution is not supported for this policy.");
        if (contextPolicy == AgentContextPolicy.RestoreAfterExecution)
            restore = await Agent.TakeSnapshotAsync(snapshotOptions, cancellationToken).ConfigureAwait(false);
        else if (contextPolicy == AgentContextPolicy.ResetOnRevisit)
        {
            var state = context.State.Node(Id) ?? throw new ArgumentException("The node is not tracked by this invocation.", nameof(context));
            if (state.BaselineSnapshot is { } baseline) await Agent.LoadSnapshotAsync(baseline, cancellationToken).ConfigureAwait(false);
            else state.BaselineSnapshot = await Agent.TakeSnapshotAsync(snapshotOptions, cancellationToken).ConfigureAwait(false);
        }
        Exception? primary = null;
        IAsyncEnumerator<IAgentStreamEvent>? iterator = null;
        AgentResult? result = null;
        var ownsPendingExecution = false;
        var structured = structuredOutputOptions is { } schema ? new StructuredOutputTool<JsonElement>(schema) : null;
        try
        {
            try
            {
                var nodeState = context.State.Node(Id) ?? throw new ArgumentException("The node is not tracked by this invocation.", nameof(context));
                if (input.IsResume)
                {
                    var paused = nodeState.InterruptedAgentSnapshot ?? throw new InvalidOperationException("The agent node has no captured paused execution.");
                    await Agent.LoadSnapshotAsync(paused, cancellationToken).ConfigureAwait(false);
                }
                else if (Agent.InterruptState is not null) throw new InvalidOperationException("An agent already paused outside this graph cannot accept a new node task.");
                ownsPendingExecution = true;
                var options = invocationOptions with { InvocationState = context.InvocationState };
                if (structured is not null) options = options with
                {
                    StructuredOutputTool = structured,
                    StructuredOutputForcePrompt = structured.ForcePrompt,
                    StructuredOutputBehavior = structuredOutputOptions!.Behavior,
                    StructuredOutputValidationLimit = structuredOutputOptions.MaxValidationAttempts
                };
                iterator = input.IsResume
                    ? Agent.StreamAsync(null, options with { InterruptResponses = input.InterruptResponses }, cancellationToken).GetAsyncEnumerator(cancellationToken)
                    : Agent.StreamContentAsync(input.Content, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool next;
                IAgentStreamEvent? current = null;
                try
                {
                    next = await iterator.MoveNextAsync().ConfigureAwait(false);
                    if (next) current = iterator.Current;
                }
                catch (Exception error) { primary = error; throw; }
                if (!next) break;
                if (current is AgentResultEvent terminal) result = terminal.Result;
                yield return new NodeStreamEvent(current, NodeStreamSource.Agent);
            }
            NodeResultUpdateEvent completed;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result is null) throw new InvalidOperationException("The agent stream did not produce a terminal result.");
                var interrupted = result.StopReason == StopReasons.Interrupt;
                JsonElement? structuredOutput = null;
                if (structured is not null && !interrupted)
                {
                    if (result.StructuredOutputToolUseId is { } id && structured.TryGetResult(id, out var value))
                        structuredOutput = value.Clone();
                    else if (structuredOutputOptions!.Behavior != StructuredOutputBehavior.TypeScript ||
                        result.StopReason != StopReasons.Checkpoint || result.Checkpoint is null)
                        throw new StructuredOutputException("The agent node did not produce accepted structured output.");
                }
                completed = new(result.Message?.Content ?? [], result.Usage, interrupted ? Status.Interrupted : result.StopReason == StopReasons.Cancelled ? Status.Cancelled : Status.Completed,
                    null, structuredOutput, result.Interrupts, result, null);
            }
            catch (Exception error) { primary = error; throw; }
            yield return completed;
        }
        finally
        {
            var failure = primary;
            if (iterator is not null)
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (ownsPendingExecution)
                try
                {
                    var nodeState = context.State.Node(Id)!;
                    var pending = Agent.InterruptState;
                    nodeState.InterruptedAgentSnapshot = pending is null ? null : await Agent.TakeSnapshotAsync(snapshotOptions, CancellationToken.None).ConfigureAwait(false);
                    var answered = new HashSet<string>(pending?.Responses.Select(response => response.InterruptId) ?? [], StringComparer.Ordinal);
                    var completed = new HashSet<string>(pending?.CompletedToolResults.Select(value => value.ToolUseId) ?? [], StringComparer.Ordinal);
                    nodeState.PendingInterrupts = NodeResult.CopyInterrupts(pending?.Interrupts.Where(question => !answered.Contains(question.Id) && (question.IsBatch || !completed.Contains(question.ToolUseId))).ToArray() ?? []);
                    nodeState.DeferredResponses = pending is null ? [] : Array.AsReadOnly(nodeState.DeferredResponses.Where(response => !answered.Contains(response.InterruptId)).ToArray());
                }
                catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (restore is not null)
                try { await Agent.LoadSnapshotAsync(restore, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanup(failure, cleanup); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }
}
