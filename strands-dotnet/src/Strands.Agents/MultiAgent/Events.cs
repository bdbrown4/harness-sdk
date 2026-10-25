using System.Text.Json;

namespace Strands.Agents.MultiAgent;

/// <summary>A graph lifecycle, progress, or terminal result event.</summary>
public interface IMultiAgentStreamEvent;
/// <summary>Runs once when a graph is initialized.</summary>
/// <param name="Orchestrator">The initialized graph or other orchestrator.</param>
public sealed record MultiAgentInitializedEvent(IMultiAgent Orchestrator) : IMultiAgentStreamEvent;
/// <summary>Runs before the graph starts node execution.</summary>
/// <param name="Orchestrator">The invoking orchestrator.</param>
/// <param name="State">The fresh invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record BeforeMultiAgentInvocationEvent(IMultiAgent Orchestrator, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Runs as a graph exits, after all active nodes have been drained.</summary>
/// <param name="Orchestrator">The invoking orchestrator.</param>
/// <param name="State">The final invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
/// <param name="Error">The original graph failure or null.</param>
public sealed record AfterMultiAgentInvocationEvent(IMultiAgent Orchestrator, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState, Exception? Error = null) : IMultiAgentStreamEvent;
/// <summary>Runs before a node body. A callback may deny the visit using CancelReason.</summary>
public sealed class BeforeNodeCallEvent : IMultiAgentStreamEvent
{
    /// <summary>Creates a mutable node admission boundary.</summary>
    public BeforeNodeCallEvent(IMultiAgent orchestrator, string nodeId, MultiAgentState state, IReadOnlyDictionary<string, object?> invocationState)
    { Orchestrator = orchestrator; NodeId = nodeId; State = state; InvocationState = invocationState; }
    /// <summary>The invoking orchestrator.</summary>
    public IMultiAgent Orchestrator { get; }
    /// <summary>The node identifier.</summary>
    public string NodeId { get; }
    /// <summary>The live invocation state.</summary>
    public MultiAgentState State { get; }
    /// <summary>The unchanged caller-owned context.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>A denial explanation, or null to execute.</summary>
    public string? CancelReason { get; set; }
    /// <summary>Requests scoped human input before this node executes, or returns its previously accepted answer.</summary>
    public JsonElement Interrupt(string name, JsonElement? reason = null, JsonElement? response = null) =>
        _interruptScope?.Resolve(name, reason, response) ?? throw new InvalidOperationException("Human input is available only during the originating node callback dispatch.");
    private NodeHookScope? _interruptScope;
    internal void Attach(NodeHookScope scope) => _interruptScope = scope;
}
/// <summary>Runs after a node body's iterator has been disposed.</summary>
/// <param name="Orchestrator">The invoking orchestrator.</param>
/// <param name="Result">The detached node result, including local execution errors.</param>
/// <param name="State">The live invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record AfterNodeCallEvent(IMultiAgent Orchestrator, NodeResult Result, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Reports a node visit denied before its body executes.</summary>
/// <param name="NodeId">The denied node.</param>
/// <param name="Reason">The explanation supplied by a callback.</param>
/// <param name="State">The live invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record NodeCancelEvent(string NodeId, string Reason, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Reports ready successors from one completed node or batch.</summary>
/// <param name="Sources">The completed sources in readiness order.</param>
/// <param name="Targets">The newly queued successors in edge declaration order.</param>
/// <param name="State">The live invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record MultiAgentHandoffEvent(IReadOnlyList<string> Sources, IReadOnlyList<string> Targets, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Wraps progress from one active node.</summary>
/// <param name="NodeId">The originating node.</param>
/// <param name="NodeType">The extensible node kind.</param>
/// <param name="State">The live graph invocation state.</param>
/// <param name="Inner">The underlying event or caller-owned ephemeral payload.</param>
/// <param name="Source">The event origin.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record NodeStreamUpdateEvent(string NodeId, string NodeType, MultiAgentState State, object? Inner, NodeStreamSource Source, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Reports a completed node visit.</summary>
/// <param name="Result">The detached terminal node result.</param>
/// <param name="State">The live invocation state.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record NodeResultEvent(NodeResult Result, MultiAgentState State, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
/// <summary>Reports the terminal aggregate of an invocation.</summary>
/// <param name="Result">The aggregate result.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
public sealed record MultiAgentResultEvent(MultiAgentResult Result, IReadOnlyDictionary<string, object?> InvocationState) : IMultiAgentStreamEvent;
