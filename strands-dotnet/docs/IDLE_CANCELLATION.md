# Idle explicit cancellation

`AgentOptions.IdleCancellationBehavior` selects only idle retention. `Ignore` (zero/default) preserves native and TypeScript behavior: `Cancel()` while idle or initializing does nothing. `CancelNextInvocation` retains an immediate idle request for the next admitted invocation. This is an opt-in match for Python's idle-retention rule, not a general Python cancellation mode.

```csharp
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation
});
agent.Cancel("Cancelled before starting");
var result = await agent.InvokeAsync("This input is not accepted");
// result.StopReason == StopReasons.Cancelled
```

The last nonnull cancellation message wins, including an empty string. A null message leaves the preceding message unchanged. An idle `afterCurrentTools: true` request alone does not signal cancellation and is cleared on admission. If an immediate request is also pending, its last nonnull message is retained even if it came from a deferred request. No idle cancellation source, registration, or operating-system handle is allocated.

While idle with a pending immediate request, `CancelSignal` returns an already-cancelled token snapshot. A token read earlier does not become cancelled later. This differs from Python's mutable `threading.Event`; callers must not use an earlier idle token to subscribe to future invocations. Each initialized pass still owns a fresh token, old contexts retain their original token, and `CancelSignal` is `None` during initialization of an admitted invocation and after completion. Standalone `InitializeAsync` does not admit or consume an invocation request; an idle snapshot can remain cancelled during that independent warmup. Caller-supplied cancellation sources remain borrowed.

An admitted request is consumed once. Cancellation requested during initialization under `CancelNextInvocation` is retained without cancelling initialization's original caller token, then applied when the pass enables its signal. Initialization failure, admitted interrupt-validation failure, requested cancellation, exception, or stream disposal clears the admitted cancellation state. Argument/limit validation before admission and rejected concurrent calls do not consume the pending request. Shutdown/disposal accepts no new requests. A child inherits the policy but has independent pending state and messages; cancelled passes cannot start hook continuations or transfer pending cancellation into a subsequent invocation.

Cancellation callbacks run outside the agent's admission lock and the pass lock. Existing synchronous `CancellationTokenSource.Cancel` callback behavior and error precedence are retained. The source remains alive until all active cancellation dispatches finish; closing a stream does not dispose a source beneath its callback.

## Source contracts and timing limits

This implementation is pinned to upstream commit `3bd98e618320f0f286acbfb2252f72388030421b`:

- TypeScript `strands-ts/src/agent/agent.ts:1084-1107` ignores cancellation outside an invocation.
- Python `strands-py/src/strands/agent/agent.py:647-715` records every nonnull message and sets the immediate signal without an idle guard. Lines 1497-1502 clear deferred-only state on entry, while lines 1596-1599 clear all cancellation state on completion. Invalid limits and concurrent admission rejection occur before that invocation cleanup.
- Python lines 1431-1436 describe a later model-stream cancellation checkpoint: pre-cancelled input can be recorded and a model request may start before stopping. Native safe points are unchanged here: cancellation after initialization can stop before the input is accepted and before model I/O. That later Python input/model timing remains a separate parity difference.

This option adds no background cancellation, remote-process termination, persisted cancellation state, or changes to durable human-interrupt recovery. It does not change the cancellation rules documented in `AGENT_CANCELLATION.md` for initialized work.
