# Explicit cancellation checkpoints

`AgentOptions.ExplicitCancellationTiming` selects where an immediate `Agent.Cancel()` request is observed. Its default, `NativeEarly` (`0`), preserves the existing native checkpoints. Idle retention is a separate `IdleCancellationBehavior` option; choosing a checkpoint pattern does not retain idle requests by itself.

```csharp
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation,
    ExplicitCancellationTiming = ExplicitCancellationTiming.ModelOrTool
});
agent.Cancel();
var result = await agent.InvokeAsync("This input can be recorded before stopping.");
```

`BeforeInputAndModelOrTool` (`1`) follows the pinned TypeScript checkpoint pattern: a requested explicit cancellation prevents input/model preparation, while a request made by a before-model callback can reach the provider boundary. A request made after an ordinary completed model answer retains its natural stop reason; a fresh tool-use response stops before the batch callbacks.

`ModelOrTool` (`2`) follows the pinned Python checkpoint pattern: explicit cancellation can pass input and before-model preparation, enter the provider, and stop before forwarding its first obtained event. For a completed tool-use response, the batch callbacks run before cancelled calls are settled. Neither mode permits a cancelled tool body to start. A newly raised human interrupt takes precedence over this pass's explicit cancellation only after tool-pipeline cleanup completes. A previously paused batch cancelled before resumed execution retains all accepted answers and incomplete questions, returns cancellation, and can be resumed by a fresh agent without repeating completed sibling effects.

In both opt-in modes, an authoritative before-invocation or before-model denial precedes the explicit request. Middleware that fabricates a completion cannot acknowledge provider history or turn the request into success. Deferred `afterCurrentTools: true` cancellation still precedes structured-output success. Explicit cancellation prevents a hook or automatic continuation even when an already accepted ordinary answer keeps its natural terminal reason.

The original invocation `CancellationToken` remains hard in every mode, including initialization, input/pre-model preparation, denials, paused batches and accepted ordinary answers. Preparation can use that caller token while real provider/tool operations retain cancellation derived from the owned pass signal. Async iterator composition can produce a linked operation token; identity with `CancelSignal` is not promised at a provider boundary. Caller sources remain borrowed, each initialized pass owns its signal, and closing a stream still joins admitted iterator cleanup. Independent cleanup failures are retained and cannot be converted into a successful pause or ordinary cancellation result.

Accepted history is not rolled back. An ordinary stateful answer completed before `AfterModelCallEvent` requests cancellation still commits its provider receipt, archive cursor and session writes exactly once; a fresh agent sends only pending input. Partial human answers are accepted durably before callbacks. Closing a consumer at `AfterInvocationEvent` cannot execute an unseen continuation.

## Pinned source and limits

The upstream source is pinned at `3bd98e618320f0f286acbfb2252f72388030421b`.

- TypeScript `strands-ts/src/agent/agent.ts:1667` checks before input at `1710–1719`; `2177` checks before estimation and the before-model hook at `2182–2187`. Denial at `2208–2228` precedes the later provider check. The model iterator advances at `2462` before its cancellation check at `2465`. An ordinary answer returns at `1756–1765`; a cancelled fresh tool-use response is paired before batch callbacks at `1768–1795`. Resumed batches run the before-tools hook at `2505` before checking cancellation at `2531–2544`.
- Python `strands-py/src/strands/agent/agent.py:1431–1436` explicitly permits recording input and issuing a model request before observing a pre-set event. Before-invocation denial is handled at `1630–1664`; input is appended at `1846–1855`. `event_loop/event_loop.py:539–566` handles before-model denial, and `event_loop/streaming.py:461–473` checks cancellation after obtaining the first provider event. Resumed pending tools skip the model at `event_loop/event_loop.py:290–298`; before-tools/interrupt handling precedes cancellation at `868–905`, and accepted end-turn content precedes cancellation at `982–1006`.

These are checkpoint patterns, not complete language modes. This API adds no external soft-event parameter. Python's mutable idle event differs from the native idle token snapshot. Native token estimation remains after the before-model hook, although both pinned upstream loops estimate earlier. Existing native end-of-stream, observer, cleanup, synthetic-result validation, durable pause and continuation-safety rules remain in effect. This increment does not change tool middleware, tool iterator cleanup or journal serialization. Explicit cancellation text retains its existing last-nonnull behavior rather than inventing a default terminal message.
