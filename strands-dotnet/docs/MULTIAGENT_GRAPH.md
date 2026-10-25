# Native graph orchestration

This implementation is the executable graph foundation of `Strands.Agents.MultiAgent`.
It is not a claim that the entire upstream multiagent family is complete. The source
baseline is commit `3bd98e618320f0f286acbfb2252f72388030421b`.

The public entry points are `Graph`, `GraphBuilder`, `Node`, `FunctionNode`,
`AgentNode`, and `MultiAgentNode`. Graphs borrow their nodes, agents, models, and
nested orchestrators. Each invocation owns its cancellation sources, event queue,
and acquired asynchronous iterators. Disposing a stream cancels and drains its
active visits; it does not dispose borrowed dependencies.

## Explicit compatibility policies

`new GraphOptions()` and `new GraphBuilder()` use the TypeScript policies:

- All incoming dependencies must be completed and all their conditions true.
- Ready successors may start while independent nodes are still running.
- A failed node is recorded while independent branches continue.
- Configured execution limits throw `GraphLimitException`, whose `Result` retains
  the drained partial results.
- Dependency input preserves typed content after a `[node: id]` text marker.
- Borrowed agents are snapshotted before each visit and restored afterward.

`GraphOptions.Python` selects the Python routing, completed-batch scheduling,
fail-fast behavior, failed-result limits, textual dependency labels, and retained
agent state. These options are independently selectable; the profile does not
hide the chosen behavior. `AgentNodeOptions.ContextPolicy` overrides the graph's
state policy for one node. `ResetOnRevisit` captures the state on first entry to
that graph invocation and restores it before later visits. Python captures its
initial agent state when constructing its node, so the native reset baseline is
an explicit difference.

The source comparisons are
[`strands-ts/src/multiagent/graph.ts`](../../strands-ts/src/multiagent/graph.ts)
(in particular its documented cross-language differences, `_findReady`,
`_resolveContent`, and `_resolveNodeInput`),
[`strands-ts/src/multiagent/nodes.ts`](../../strands-ts/src/multiagent/nodes.ts),
[`strands-ts/src/multiagent/state.ts`](../../strands-ts/src/multiagent/state.ts), and
[`strands-py/src/strands/multiagent/graph.py`](../../strands-py/src/strands/multiagent/graph.py)
(`GraphNode`, batch scheduling, edge readiness, and `_build_node_input`).

## Execution and data contracts

Each custom node emits zero or more `NodeStreamEvent` updates followed by exactly
one `NodeResultUpdateEvent`. Missing, duplicate, unknown, null, or post-terminal
updates become explicit failed-node results. The framework assigns node identity;
custom object equality and nested-looking payloads cannot replace it. Actual
`MultiAgentNode` progress preserves inner node identities.

`InvocationState` is passed by reference to conditions, callbacks, and nested
executions without framework keys. `MultiAgentState.App` stores graph application
state separately. Result lists retain visits in completion order; aggregate
terminus content follows node declaration order. Usage is counted once at each
orchestration boundary and rejects negative counters. Terminus selection follows
the pinned TypeScript status-based rule, including its behavior for cycles.

Typed input preserves media and JSON. Dependency projection filters reasoning
blocks, including signed/redacted and supported provider-native reasoning.
Visible Anthropic text and normalized citation text become ordinary text blocks;
opaque Anthropic server-tool replay stays with its originating provider.
Python input mode deliberately projects predecessor text; its nested predecessor
projection currently uses the nested aggregate content rather than Python's full
`get_agent_results()` flattening. Structured agent responses are additionally
available as `NodeResult.StructuredOutput`; the original terminal content remains
intact. An agent successor rejects orphan tool-call/result traffic through the
ordinary typed-input validation boundary instead of silently converting it.

`MaxConcurrency` bounds active visits. `MaxSteps` stops admission and drains work
already admitted before reporting exhaustion. Graph and node deadlines are
cooperative. External cancellation and parent deadlines propagate into nested
orchestrators, a stronger cancellation policy than the pinned TypeScript nested
execution behavior. Native node deadlines also apply to custom nodes and nested
graphs. A node that ignores cancellation can still delay cleanup.

Callbacks are awaited before the corresponding streamed event; independent-node
callbacks may execute concurrently. Callback registration priority is ascending;
equal-priority after callbacks execute in reverse registration order. All after
callbacks are attempted. Cancellation, iterator disposal, snapshot restoration,
and after callbacks retain the primary exception, attaching secondary cleanup
errors under `Exception.Data["Strands.CleanupErrors"]`. A graph rejects overlapping
or reentrant invocations and is reusable after its prior stream is disposed.

## Typed result references and snapshot data

`NodeResult.AgentResult` and `NodeResult.MultiAgentResult` expose the original
single-agent or nested-orchestrator result for an accepted node visit.
`NodeResultUpdateEvent` carries the same optional typed references. At most one
arm can be supplied. These are borrowed live references: the Agent result keeps
its original local metrics and caller invocation-state reference. Existing
detached `Content`, `StructuredOutput`, visit status and usage remain available.
The original eight-argument `NodeResult` and six-argument update constructors,
their optional defaults, and ordinary JSON shape are retained. New all-required
overloads carry the two arms; both properties are excluded from ordinary JSON.

Graph snapshots use native orchestration schema `1.1` when a stored visit carries
a typed result; snapshots without typed arms retain schema `1.0`. Restoring
legacy `1.0` custom-node snapshots remains supported. A recognizable typed arm
under `1.0`, null or simultaneous arms, invalid safe-result data, unsupported
versions, or duplicate reserved typed headers is rejected before replacing live
graph state. Opaque JSON and metadata are left to the existing native codecs.

Snapshot data uses the native `AgentResultJson` safe envelope, including its
optional checkpoint marker and structured-output presence (even JSON null),
plus a closed native nested-result envelope. Rehydration creates fresh empty
inner Agent metrics, traces and invocation state; it does not serialize caller
delegates or resume an Agent execution. Preserved visit usage remains the source
for nested aggregate totals and is counted once. This data format does not claim
TypeScript/Python session-wire interchange or arbitrary CLR object restoration.

Capture rejects typed-result paths deeper than 64 result levels before
projecting the next result. The JSON serializer and parser impose their own
independent depth limits, which can reject shallower result trees containing
additional nested JSON. This is a native resource boundary, separate from
checkpoint execution, the pending nested-orchestrator resume state, and result
text rendering.

## Durable interruptions and sessions

`InitializeAsync` restores the optional session and initializes callbacks once.
`TakeSnapshotAsync` and `LoadSnapshotAsync` provide detached native orchestration
envelopes. A restore validates the graph identity, topology, compatibility policies,
node/result state and embedded agent snapshots before replacing the live state.
It performs no agent I/O. Delegates and application dependencies are recreated by
the application; their executable behavior is not serialized or compared.

`AgentNode` captures a paused agent before restoring its borrowed baseline.
`MultiAgentNode` does the same for checkpoint-capable nested orchestrators.
`MultiAgentInput.Resume` routes responses by opaque question identity. Completed
siblings are retained, and accepted responses survive callback failures,
cancellation and JSON restoration. Responses for queued children are saved before
admission and remain accepted if another child pauses again. An empty explicit
`Resume([])` retries accepted answers after a later failure. `CanResume` describes
the retained execution separately from its last failed result; `PendingInterrupts`
contains only unanswered requests. An unrelated preexisting pause on a borrowed
agent or nested graph cannot be adopted by a fresh parent execution.

`BeforeNodeCallEvent.Interrupt` gates a node without executing its body. Multiple
callbacks collect independent requests, and accepted answers are consumed before
forwarding remaining responses to the child. The helper is valid only during its
originating callback dispatch. Callback request names must be distinct within that
dispatch. Hook approvals are durable before further external callbacks run.

`IMultiAgentSessionStore` is independent of the single-agent session format.
`StorageMultiAgentSessionStore` borrows any native `IStorage` backend, hashes opaque
session identifiers into fixed keys and bounds stored JSON (64 MiB by default).
`SaveLatestOn` defaults to `Node`, matching both upstream implementations: the
scheduler saves a detached committed view after each node's after callbacks and
readiness decisions, then saves again after invocation cleanup. `Invocation`
defers these writes until cleanup. The scheduler serializes writes, and an active
sibling's terminal output is excluded until its own after callbacks finish.
Ordinary node-boundary writes honor invocation cancellation; final cleanup writes
run after admitted bodies are drained. Failed loads do not overwrite unread
sessions. The native envelope does not claim binary or JSON interoperability with
TypeScript/Python session formats.

Completed visits and pending routing decisions are recorded separately. If a
condition fails after a source completes, restoration retries that routing decision
without running the source again. Previously queued successors are preserved, and
the completed-batch policy waits for the remainder of the recovered batch before
evaluating its deferred transitions.

A stored unfinished non-interrupt execution retains its ready and active node
identifiers. Loading it into a fresh graph preserves committed siblings and can
continue with either new content or `MultiAgentInput.Resume([])`. `RestoredInput`
defaults to `Incoming`, matching TypeScript's use of newly supplied task content;
the Python profile selects `Stored`. Explicit response-only resume always keeps
the stored typed task. Human-paused executions continue to require explicit
responses. Cancellation and failed checkpoint writes retain a recoverable
committed view instead of overwriting it with an unresumable cleanup result.

This is an at-least-once boundary: a body whose effects occurred before its visit
was committed can execute again after recovery. Application state is a shared JSON
store, not a transaction across concurrent nodes and external effects. Applications
must make their own effects idempotent where repetition matters. Delegates, tools,
models and borrowed agent state outside a captured interrupt remain dependencies
that the application recreates.

## Remaining graph and family work

Pinned orchestrator sessions store the latest snapshot only; the upstream
trigger/history/manifest features belong to single-agent sessions. Graph plugins
and telemetry integration remain. Direct native agents are supported by
`AgentNode`; other execution
contracts can be adapted through `Node` or `FunctionNode`. Native Swarm's
implemented subset and explicit policy differences are described in
[MULTIAGENT_SWARM.md](MULTIAGENT_SWARM.md). A2A and bidirectional agents remain
separate acceptance families.

The focused tests cover chain and diamond dependencies, typed projection,
AND/OR readiness, both schedulers, bounded concurrency, cycles and limits, state
policies, structured output, nested graphs, callback order and cancellation,
malformed streams, original-error retention, and early stream disposal. Portable
scenarios additionally cover fresh/nested interruption restoration, partial human
answers, accepted-answer failures, queued answers under changed concurrency,
hook approvals, session failures, and atomic invalid or cancelled loads. Portable
graph scenarios execute against the `netstandard2.0` asset through the `net48`
target on the installed Windows Framework 4.8.1 runtime, and against the modern
assets on .NET 8 and .NET 10. Clean Framework 4.8 execution for this new source
still requires a fresh immutable cloud checkpoint.
