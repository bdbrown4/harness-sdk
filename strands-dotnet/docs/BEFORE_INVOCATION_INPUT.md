# Incoming message replacement

`BeforeInvocationEvent.Messages` exposes a detached snapshot of the incoming manual messages. A hook may replace the whole batch before it is appended to conversation history:

```csharp
hooks.Register<BeforeInvocationEvent>((value, cancellationToken) =>
{
    value.Messages = new[] { Message.User("redacted input") };
    return default;
});
```

Assigning `null` keeps the original input. Assigning an empty list appends no manual input; it differs from a user message containing an empty string or empty content. The existing two-argument event constructor and deconstruction remain available. Manually constructed events initially have `Messages == null`, and that null member is omitted from JSON. This addition does not add invocation context to `MessageAddedEvent`.

The source contract comes from pinned Python `hooks/events.py:35–66`, `agent/agent.py:1627–1669` and `tests/strands/agent/test_agent_hooks.py:657–714` at commit `3bd98e618320f0f286acbfb2252f72388030421b`. Pinned TypeScript does not expose this input replacement field. Native messages remain immutable snapshots rather than Python mutable dictionaries.

Initialization, session restoration, initialized observers, pending-write retries, accepted interrupt answers and pending-tool repair keep their existing order before this hook. Invalid hook output therefore cannot promise that initialization performed no I/O. After successful hooks, the existing cancellation and denial checkpoints run before selecting replacement input. Denial ignores the unused replacement. Hooks receive the original invocation cancellation token; explicit cancellation timing keeps the selected native, TypeScript or Python policy.

The selected batch is detached and checked against the current conversation before any new input message is appended. Existing message roles and tool-result correlation rules apply. The hook cannot replace earlier history, modify provider state, or acknowledge a server-side cursor. Normal session and repository append receipts, accepted-write cleanup and dirty-save retries remain responsible for persistence. Validation failure appends none of the new batch; this does not roll back application state changed by a hook or earlier accepted effects.

Manual hook-resume messages pass through the same boundary each cycle. SDK-owned continuation messages, including accepted background results, stay private and are prepended afterward. Clearing manual input cannot discard those messages or falsely mark their append receipts as settled. This matches Python's separation in `agent.py:1791–1808,1846–1851`. The complete combined batch is validated before append. Denial, failure and early disposal retain existing abandonment cleanup.

Replacement changes the input used by the model, durable history and message-added observations. Existing telemetry records invocation input at its established earlier boundary; this feature does not redefine that tracing policy.

Paused human answers are accepted before the hook. The subsequent input-only validation does not reconsider those response identifiers as newly submitted answers. A rejected hook replacement leaves the accepted answers available in live and saved interrupt state; it does not require answering again or repeating a completed effect.

Forty focused cases pass on the initial modern target: four initial missing-behavior REDs, two actual paused-resume regression REDs corrected after review, and 34 first-green conformance cases. They cover cancellation profiles, denial, malformed batch atomicity, accepted-answer preservation, dirty-save retry, continuation abandonment, original event construction/JSON null omission, and the real Responses adapter's restored cursor through a controlled HTTP handler. Cross-target, API/package and separately compiled binary checks are separate proof gates recorded with the frozen component. The initial cases alone do not establish those gates, complete SDK parity or live-provider behavior.
