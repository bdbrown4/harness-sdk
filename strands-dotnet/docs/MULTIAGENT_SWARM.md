# Native swarm orchestration

The implementation follows the union of `strands-ts/src/multiagent/swarm.ts` and `strands-py/src/strands/multiagent/swarm.py` at upstream commit `3bd98e618320f0f286acbfb2252f72388030421b`. The two SDKs use different routing protocols and execution policies. Native configuration makes those choices explicit. This document describes the implemented subset; it does not close the entire multi-agent export family.

## Routing and compatibility policies

`SwarmOptions` defaults to TypeScript's structured routing. Every completed agent visit produces a validated object containing `message`, optional `agentId`, and optional object `context`. The schema advertises only other declared agents. Omitting `agentId` finishes the swarm; a handoff passes its chosen message and formatted JSON context to the target. A committed handoff retains that message and context when a later process restores its target.

`SwarmOptions.Python` selects the invocation-local `handoff_to_agent` tool, ordinary final agent output, reset baselines, two 20-visit budgets, 900-second execution and 300-second node deadlines, and failed-result policies. It uses `default_swarm` as its identifier. Nodes have explicit native identifiers; callers can use `new AgentNode(agent, agent.Name)` when matching Python agent-name routing.

The Python profile inspects completed execution history for repetitive handoffs and accumulates active execution time across resumes. The TypeScript defaults start fresh repetition and timeout windows for each invocation. `RepetitionScope` and `TimeoutScope` expose both policies. Human wait time between invocations is excluded. `MaxSteps` counts logical visits; continuing a pending visit does not consume another step. `MaxHandoffs` follows Python's completed-visit counter independently. A terminal answer on the final allowed visit succeeds.

Tool mode collects successful handoff contributions in `SharedContext`. `ContextCommit.CompletedVisit` keeps them private until the visit completes. The Python profile selects `AcceptedResults`, making already accepted contributions visible at a human pause. A durable applied flag prevents replay from overwriting a later application update to the same key.

Native handoffs use the normalized successful tool result after middleware and result hooks. Multiple accepted calls commit in assistant call order, with the last accepted target selected. This is deterministic even when calls finish in reverse order. It deliberately differs from Python's timing-dependent mutation inside tool bodies. Invalid context keys produce a correlated tool error before any contribution is committed.

## Borrowing and initialization

Agents, models, tools, callback registrations, and session repositories remain caller-owned. Swarm coordination tools are bound to one agent invocation; they never enter the agent's persistent registry or a child's inherited registry. Caller `InvocationState` is forwarded by reference without SDK keys.

`RestoreAfterExecution` snapshots and restores a borrowed agent around every visit. `Retain` preserves its conversation. Swarm's `ResetOnRevisit` uses the same initialization baseline on every visit and across invocations. Use `await Swarm.CreateAsync(options)` to capture those baselines before the factory returns. The synchronous constructor defers capture until `InitializeAsync` or the first invocation. A restored reset-mode checkpoint must contain the original, consistent baselines; it cannot silently recapture current caller state.

Initialization is guarded and memoized. Overlapping invocation, initialization, and snapshot operations fail. A swarm never adopts a borrowed agent's unrelated existing pause.

## Human input and recovery

`BeforeNodeCallEvent.Interrupt` requests scoped human input. Agent tool, hook, and middleware interruptions retain the native agent's paused batch. Both routes expose `CanResume` and detached `PendingInterrupts`. `MultiAgentInput.Resume(responses)` accepts matching outstanding answers; an empty response list can retry already accepted work. New task content cannot replace a human pause.

Answers enter retained state before invocation callbacks. With a session repository, they are persisted before those callbacks run. A callback failure, cancellation, or a fresh instance therefore does not require the human to answer again. Resuming replaces the interrupted or aborted visit's result and retains its prior usage rather than adding a duplicate terminal row.

Tool-mode recovery uses opaque batch receipts containing detached calls, completed body checkpoints, normalized results, and handoff decisions. It does not assume unique caller message IDs or retain model history as an identity index. Earlier completed receipts remain valid when context reduction removes their model-visible messages. The current receipt must match the authoritative paused agent batch, effective inputs, and normalized results. Checkpoints are application-owned trusted data, not authenticated security tokens.

`TakeSnapshotAsync`, `ValidateSnapshot`, and `LoadSnapshotAsync` use the native `multiAgent`/`1.0` envelope. Validation prepares all replacement state before mutation or agent I/O. Identity, topology, routing shape, pending questions, tool receipts, baselines, and nonnegative counters are checked. Content and JSON metadata are detached. Durable errors retain their CLR type name and message through `RestoredNodeException`; an exception instance is process-local.

Pair `SessionId` with a borrowed `IMultiAgentSessionStore`. `SaveLatestOn.Node` persists completed bodies before publishing their result or invoking a handoff callback, then saves again during invocation cleanup. `Invocation` saves only during cleanup. A recoverable frontier identifies the next committed handoff target or an aborted current visit. Ordinary writes honor caller cancellation; final cleanup attempts a save without that cancelled token. A failed initial load cannot overwrite unread history.

Recovery is at least once for an uncommitted body. Application state and external tool effects are not an atomic database transaction. Completed bodies already represented by an agent's pending tool ledger are reused. Callback side effects require their own idempotency policy.

## Streaming and failure behavior

The swarm emits its own lifecycle, node progress, node results, handoffs, and aggregate result. Callbacks observe the same event instances published to consumers. A nested swarm retains its own identity inside a `MultiAgentNode`, including durable interruption through a parent graph.

Cancellation is cooperative and drains every admitted iterator before exit. Early stream disposal also drains and restores borrowed state. Cleanup attempts preserve the original failure and attach later failures under `Strands.CleanupErrors`. Caller cancellation propagates even when failed-result policies are selected. Native after-node cleanup callbacks run for interrupted visits too; Python skips that callback while a visit is paused.

Tool-mode terminal content retains media, JSON, citations, and other supported native blocks. Display text uses the common message projection. Typed task input remains typed; media and JSON are not stringified into a prompt.

## Verification and remaining work

The focused suite is `tests/Strands.Agents.Tests/MultiAgent/SwarmTests.cs`, linked into the compatibility project. It covers both routing protocols and profiles, deadlines and budgets, reset state, parallel ordering, cancellation, cleanup errors, human input, JSON roundtrips, selected session policies, malformed checkpoints, and graph nesting. Local `net48` execution uses the installed .NET Framework 4.8.1 runtime; clean 4.8 and cross-platform CI evidence must reference the exact immutable source checkpoint.

Multi-agent plugin lifecycle contracts and OpenTelemetry trace/span integration remain outside this slice. Timing-dependent Python handoff mutation and its interrupted after-hook suppression are explicit native behavior differences. Foreign SDK snapshot formats and Python's vended swarm tool are separate acceptance work. A2A and bidirectional agents are separate families.
