# Agents as tools

`Agent.AsTool(AgentToolOptions)` exposes a borrowed agent through a streaming tool whose JSON input is `{ "input": "the task" }`.

```csharp
var specialistTool = specialist.AsTool(new AgentToolOptions
{
    Name = "specialist",
    PreserveContext = true,
    Delegate = true,
});
```

`PreserveContext = true` runs the wrapped conversation directly, including its selected session store. False captures messages, application/provider state, system prompt and interrupted state at tool creation, then restores that baseline before each new call. This mode rejects a session store because resetting conversation state conflicts with external persistence. Neither mode transfers ownership of the wrapped agent, its model, hooks, tools or transports. Dispose the agent through its application owner. Disposing a tool stream releases its active invocation without disposing that agent.

The adapter serializes its own calls and rejects a concurrent call with a correlated error. The wrapped agent's ordinary invocation guard remains active. Caller `InvocationState` is forwarded unchanged; the adapter adds no entries. Explicit tool names are validated normally. An omitted name derives the existing native ASCII-safe name from the agent display name.

The existing `AsTool(string? name = null, string? description = null)` overload remains available with its original `task` input schema and fresh-child behavior. It retains native compatibility for callers that expose session-configured agents through fresh children. The typed overload follows the upstream reset/preserve semantics instead; it does not silently change the older overload's contract.

## Delegation

`Delegate = true` adds the upstream model-facing instruction that this must be the only call in a turn. If a registered delegate appears in a mixed batch, every call receives one correlated error result and no body runs. A dynamically selected delegate is also prevented from executing in a mixed batch. Stateful parents reject delegate registration because early exit cannot acknowledge server-owned function calls. Delegate tools always execute in the foreground even under an Always background wildcard.

A successful, meaningful delegation result becomes the final assistant message without another parent model call. Ordered typed content is retained. Empty text or an error continues the parent model loop. A structured parent still needs its own newly validated structured-output call. Retries use the final effective selected tool, so replacing a delegate with an ordinary tool clears delegation behavior and the reverse enables it. Rejected attempts' already-streamed progress remains visible.

Child model events are exposed natively for delegation. Other child lifecycle events remain wrapped progress, and the child's final result is consumed by the adapter; only the parent's final result is emitted as `AgentResultEvent`.

`BeforeToolsEvent.CancelReason` can deny an entire batch. `AfterToolsEvent.EndTurn` can clear, replace or supply final assistant content. The complete content is detached and validated before history changes; tool protocol blocks are rejected. The batch's correlated tool results are committed first, preserving valid conversation history if validation or a later callback fails. Explicit EndTurn is also refused for a stateful parent.

## Source and checkpoint boundary

The reference is pinned commit `3bd98e618320f0f286acbfb2252f72388030421b`: [TypeScript adapter](../../strands-ts/src/agent/agent-as-tool.ts), [delegation plugin](../../strands-ts/src/agent/agent-delegation.ts), [Python adapter](../../strands-py/src/strands/agent/_agent_as_tool.py) and [delegation plugin](../../strands-py/src/strands/agent/_agent_delegation.py). The native typed adapter retains richer typed content than the TypeScript adapter's plain-text fallback.

## Nested human input

The typed adapter forwards child tool, hook and middleware input requests through the originating parent call. It preserves the question's display name and reason, and prefixes its opaque identifier with `agent_as_tool:<escaped parent call id>:`. Different child questions can share a display name. Responses map back to their original child identifiers; partial responses retain completed sibling work. An invocation with no answer repeats the pending questions without invoking the child again. Further nested agents apply another prefix at each level.

Resetting adapters store an interrupted child checkpoint in the parent's private interrupt context under `subAgentInterruptedTurns`. A parent `FileSessionStore` therefore supports recreating both the parent and its ephemeral child before answering. The native checkpoint representation remains independent of Python and TypeScript serialization. Restore validates child checkpoints and their correlation before changing parent history or state. If a runtime dependency prevents child restoration, the child checkpoint remains stored and its unapplied answers become unanswered again, allowing a repaired child to resume later.

Preserved adapters keep the interrupted conversation in the wrapped agent instead. They require the child's own session store for restart recovery; without one, a fresh child reports explicitly that it did not run and the answer was not applied. A live background task uses its own input context and the same forwarding behavior. Background tasks follow the existing restart rule: persisted nonterminal tasks become failed rather than resurrecting an executing task. A direct tool invocation continues to refuse human pauses, including nested pauses; an after hook cannot turn that refusal into success.

This increment implements nested forwarding and durable ephemeral-child recovery alongside the reset/preserve/delegation behavior. Complete SDK/harness parity and PR readiness remain subject to the repository's broader parity, API and maintenance review.

## Direct execution

`Agent.InvokeToolAsync` executes one registered tool without a model call. `Agent.StreamToolAsync` exposes its `ToolStreamEvent` progress followed by one normalized `ToolStreamResultEvent`. Both use direct-call before/after hooks and schema validation. They copy invocation-state dictionary membership while borrowing its values. Model and tool middleware do not run. Disposing a stream cancels and joins its owned operation and iterator cleanup; caller-owned tools and models remain borrowed. Direct execution refuses human pauses.

Method and enumerator cancellation tokens both participate in stream cancellation. Cancellation retains the requested token and any joined cleanup failures under `Exception.Data["Strands.CleanupErrors"]`, including when a Framework consumer observes `MoveNextAsync()` or `DisposeAsync()` through `AsTask()`. An independent, unrequested cancellation exception from a tool remains an error result. Cancellation and early close wait for owned cleanup to finish.

The original three-argument overloads keep their native no-history behavior. The four-argument overloads accept `DirectToolCallOptions` explicitly:

```csharp
var options = new DirectToolCallOptions
{
    Profile = DirectToolCallProfile.Python,
    RecordHistory = true,
    UserMessageOverride = "Inspect the current task",
};
var result = await agent.InvokeToolAsync(toolUse, invocationState, options, cancellationToken);
```

The TypeScript profile records an assistant tool call, its user tool result, and an assistant acknowledgment. The Python profile adds a user preface, accepts an optional `UserMessageOverride`, and applies the configured classic conversation manager after the accepted call, including when `RecordHistory` is false. A TypeScript override is rejected. Recording requires exclusive invocation admission and cannot run while human input is pending.

Python recording filters input by the accepted effective name's registered schema properties. A before hook can select another body without making that body's schema the recording authority. The schema is copied after the before hook and before body execution; recording validation occurs after the accepted attempt and can therefore fail after body effects. This capture timing differs from pinned Python's later registry lookup. Missing registered names retain the complete detached input.

History rows are published in sequence through the normal message hooks and session saves. A hook or save failure retains its accepted prefix and skips subsequent management. When a classic manager is configured, a Python no-record call is refused before its body while another direct operation records history. This protects that operation's message prefix; it does not introduce rollback or a general reentrancy guarantee. Existing standalone and supported nested no-record behavior is retained.

The implementation uses the existing hook and management boundaries. Scott Hanselman's [discussion of abstractions](https://www.hanselman.com/blog/please-learn-to-think-about-abstractions) informs this engineering choice; source behavior is established by the pinned SDK implementations and conformance tests, without an endorsement claim.
