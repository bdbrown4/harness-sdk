# Explicit invocation cancellation

`Agent.Cancel(string? message = null, bool afterCurrentTools = false)` requests cooperative cancellation of the initialized active invocation pass. `Agent.CancelSignal` exposes its read-only `CancellationToken`. Idle requests and requests during initialization are ignored; the original invocation token still cancels initialization. Every continuation pass receives a fresh signal, and the agent returns to `CancellationToken.None` while idle.

```csharp
// Borrowed cancellation remains owned by the application.
using var caller = new CancellationTokenSource();
using var stop = agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
{
    value.Agent.Cancel("Stopped before model I/O");
    return default;
});
AgentResult result = await agent.InvokeAsync("perform the work", cancellationToken: caller.Token);
```

The request takes effect at the next safe point. Model and foreground tool I/O receive an SDK-owned token that observes both the caller and explicit cancellation. A tool may also inspect `ToolContext.CancelSignal`: foreground contexts capture the same signal as the agent, direct calls capture their operation token, and detached background contexts capture their accepted execution token. A retained context never switches to another invocation's signal. Cancelling the foreground wait does not cancel already accepted background work.

`afterCurrentTools: true` leaves the accepted batch's tokens live, finishes sequential or concurrent peers, commits their correlated results, then returns `StopReasons.Cancelled` before another model call. A natural response with no tool batch retains its natural stop. An immediate request takes priority over an earlier deferred request. Among requests accepted before the terminal decision, the last non-null text wins; an empty string is explicit text. A null message does not add a synthetic assistant message.

An accepted `AfterToolsEvent.EndTurn` precedes internal cancellation, matching Python's ordering. Requested caller cancellation and independent pipeline or iterator cleanup failures retain priority. Explicit cancellation precedes structured-output completion. Cancellation prevents hook-requested and automatic continuation from repeating an accepted tool body.

## Callback and cleanup boundaries

Before-invocation, before-model, model middleware, regular message-added, and invocation interrupt callbacks retain the original caller token. Foreground tool and progress callbacks retain their existing batch operation token, which also observes stream disposal. After callbacks retain the existing uncancelled cleanup parameter. Hooks and middleware that need to observe an explicit request independently can inspect `event.Agent.CancelSignal` or `context.Agent.CancelSignal`; an uncooperative application callback cannot be forcibly stopped.

Cancellation callbacks follow synchronous .NET `CancellationTokenSource.Cancel` semantics. A throwing callback is reported to the code calling `Cancel` as an `AggregateException`; it is not invented as a provider error. Callbacks must not synchronously wait for their own executing invocation or depend on later callbacks running first. Cancellation and resource disposal execute outside the agent's cancellation locks. Physical iterator cleanup finishes before the invocation completion boundary, and independent cleanup failures remain observable with the primary error.

## Accepted history and persistence

Cancellation before input acceptance prevents the new prompt from being appended. Once a message or real provider receipt is accepted, its history/state commit and cleanup finish before logical cancellation is reported. Already-requested cancellation does not abandon that accepted save. Failed writes remain dirty, a final save retries the accepted snapshot, and write/observer failures retain their original identity with secondary failures attached. This does not provide a transaction across arbitrary application storage or external tool effects.

Completed server-managed provider state and its acknowledged cursor survive cancellation; pending tool outputs remain available to a fresh agent without repeating completed bodies. Synthetic middleware responses remain local pending history and cannot manufacture provider acknowledgement. Cancellation plus early stream closure also commits explicit terminal text after tool settlement.

A paused human-input ledger and accepted answers remain durable. Cancellation is not an abort of that ledger. Explicit cancellation text is not inserted into its incomplete tool exchange; resume or explicitly abort the pause through the existing APIs.

## Source choices and limits

This surface combines pinned TypeScript's initialized-active/idle-no-op signal lifecycle with Python's optional text and deferred-batch controls. Python also supports pre-cancelling a future invocation; that behavior is not selected here. TypeScript adds the fixed text `Cancelled by user` when cancelling; this API instead uses Python's null-message behavior, and callers can supply that text explicitly. Native `CancellationToken` composition and guaranteed cleanup differ from JavaScript `AbortSignal` and Python thread events.

The source baseline is `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/agent/agent.ts:1059-1108,1231-1244,1929-1973`, Python `src/strands/agent/agent.py:647-715` and `src/strands/event_loop/event_loop.py:980-1019`, and TypeScript executor cancellation-signal tests. The verification scope is managed SDK behavior with deterministic local fixtures; it does not claim live provider, whole-SDK parity, or clean .NET Framework 4.8 OS validation.
