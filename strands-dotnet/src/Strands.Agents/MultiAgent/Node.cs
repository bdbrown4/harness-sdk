using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.MultiAgent;

/// <summary>A progress or terminal item from a custom node body.</summary>
public interface INodeStreamEvent;
/// <summary>The source of a node's wrapped progress.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NodeStreamSource>))]
public enum NodeStreamSource
{
    /// <summary>Native single-agent progress.</summary>
    [JsonStringEnumMemberName("agent")] Agent,
    /// <summary>Nested orchestration progress.</summary>
    [JsonStringEnumMemberName("multiAgent")] MultiAgent,
    /// <summary>Application-defined progress.</summary>
    [JsonStringEnumMemberName("custom")] Custom
}
/// <summary>An ephemeral node progress update. JSON payloads are detached; other CLR objects remain caller-owned.</summary>
public sealed record NodeStreamEvent : INodeStreamEvent
{
    /// <summary>Creates a progress update and identifies its origin.</summary>
    public NodeStreamEvent(object? value, NodeStreamSource source = NodeStreamSource.Custom)
    {
        if (!Enum.IsDefined(typeof(NodeStreamSource), source)) throw new ArgumentOutOfRangeException(nameof(source));
        Value = value switch { JsonElement json => json.Clone(), JsonDocument document => document.RootElement.Clone(), _ => value }; Source = source;
    }
    /// <summary>The detached JSON or caller-owned progress payload.</summary>
    public object? Value { get; }
    /// <summary>The progress origin.</summary>
    public NodeStreamSource Source { get; }
}
/// <summary>One terminal custom-node output. No events may follow it.</summary>
public sealed class NodeResultUpdateEvent : INodeStreamEvent
{
    /// <summary>Copies terminal content and optional structured output.</summary>
    [JsonConstructor]
    public NodeResultUpdateEvent(IReadOnlyList<ContentBlock>? content = null, Usage? usage = null, Status status = Status.Completed, Exception? error = null, JsonElement? structuredOutput = null, IReadOnlyList<Interrupt>? interrupts = null)
        : this(content, usage, status, error, structuredOutput, interrupts, null, null) { }
    /// <summary>Creates a terminal update with one optional live typed result. Typed result references remain locally owned.</summary>
    public NodeResultUpdateEvent(IReadOnlyList<ContentBlock>? content, Usage? usage, Status status, Exception? error,
        JsonElement? structuredOutput, IReadOnlyList<Interrupt>? interrupts, AgentResult? agentResult, MultiAgentResult? multiAgentResult)
    {
        if (agentResult is not null && multiAgentResult is not null) throw new ArgumentException("A terminal update cannot contain both typed result arms.", nameof(multiAgentResult));
        AgentResult = agentResult; MultiAgentResult = multiAgentResult;
        Content = new Message(Role.Assistant, content ?? []).Content; Usage = usage ?? new(); Status = status; Error = error; StructuredOutput = structuredOutput?.Clone();
        NodeResult.ValidateUsage(Usage);
        if (status is not (Status.Completed or Status.Failed or Status.Cancelled or Status.Interrupted)) throw new ArgumentOutOfRangeException(nameof(status));
        Interrupts = NodeResult.CopyInterrupts(interrupts ?? []);
        if ((status == Status.Interrupted) != (Interrupts.Count > 0)) throw new ArgumentException("Only interrupted terminal updates carry nonempty human requests.", nameof(interrupts));
    }
    /// <summary>Detached ordered output.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
    /// <summary>Token usage for this visit.</summary>
    public Usage Usage { get; }
    /// <summary>The terminal status.</summary>
    public Status Status { get; }
    /// <summary>The original local failure.</summary>
    public Exception? Error { get; }
    /// <summary>Detached structured output.</summary>
    public JsonElement? StructuredOutput { get; }
    /// <summary>Detached unanswered human requests when this visit interrupted.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; }
    /// <summary>The original live single-agent result, excluded from ordinary JSON.</summary>
    [JsonIgnore] public AgentResult? AgentResult { get; }
    /// <summary>The original live nested orchestration result, excluded from ordinary JSON.</summary>
    [JsonIgnore] public MultiAgentResult? MultiAgentResult { get; }
}
/// <summary>Context supplied to a node body.</summary>
/// <param name="State">The current graph invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
/// <param name="ContextPolicy">The graph's default borrowed-agent state policy.</param>
public sealed record NodeContext(MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState, AgentContextPolicy ContextPolicy = AgentContextPolicy.RestoreAfterExecution);
/// <summary>An extensible graph node. Implementations yield progress and exactly one terminal update.</summary>
public abstract class Node
{
    /// <summary>Creates a node with a nonblank identifier.</summary>
    protected Node(string id, string? description = null) { Guard.NotNullOrWhiteSpace(id, nameof(id)); Id = id; Description = description; }
    /// <summary>The node's unique graph identifier.</summary>
    public string Id { get; }
    /// <summary>An optional description.</summary>
    public string? Description { get; }
    /// <summary>The extensible node kind.</summary>
    public virtual string Type => "node";
    /// <summary>Executes one visit. The graph owns and disposes the returned iterator; dependencies remain caller-owned.</summary>
    public abstract IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken = default);
}
/// <summary>A custom node implemented by an asynchronous sequence callback.</summary>
public sealed class FunctionNode(string id, Func<MultiAgentInput, NodeContext, CancellationToken, IAsyncEnumerable<INodeStreamEvent>> callback, string? description = null) : Node(id, description)
{
    private readonly Func<MultiAgentInput, NodeContext, CancellationToken, IAsyncEnumerable<INodeStreamEvent>> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    /// <inheritdoc />
    public override IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken = default) => _callback(input, context, cancellationToken);
}
