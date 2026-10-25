# Message-added invocation context

`MessageAddedEvent.InvocationState` is the original caller-owned invocation dictionary for messages appended during an agent invocation:

```csharp
hooks.Register<MessageAddedEvent>((value, cancellationToken) =>
{
    var request = value.InvocationState["request"];
    return default;
});
```

The SDK passes the dictionary by reference. It does not copy it, add keys, persist it, or substitute `Agent.State`. This includes accepted input, assistant and tool-result messages, denials, additional before-model messages, intervention guidance, cancellation text, pending-tool repair, and messages committed while physically joining early tool-stream disposal. Manual hook resumes and protected automatic continuations retain the original public invocation dictionary. A later public invocation, including interrupt resume, uses its own supplied dictionary.

The existing two-argument constructor, two-value deconstruction, record inheritance and old constructor selection remain intact. Old manually constructed events and an out-of-invocation `AbortInterruptsAsync` notification use a shared, genuinely read-only empty dictionary. No ambient or last-invocation state is retained. The new property is excluded from JSON; the legacy native `Agent` and `Message` JSON fields remain unchanged. Bound record equality uses the ordinary reference equality of its dictionary property.

Pinned TypeScript at `3bd98e618320f0f286acbfb2252f72388030421b` supplies this context in `hooks/events.ts:207–233` and the append helpers in `agent/agent.ts:2687–2739`. Python `hooks/events.py:117–133` has no corresponding context field. TypeScript also excludes the agent itself from its wire JSON; this additive native change preserves the existing native JSON shape instead of changing that separate contract.

Only existing append notifications gain context. Constructor/session/snapshot restoration and whole-history context or classic-manager replacements do not gain new events. Native direct tool calls continue to avoid recording conversation messages. TypeScript's separate optional direct-tool recording behavior in `agent/tool-caller.ts:239–242,275–293` is outside this change.

Hook dispatch counts, callback cancellation-token scopes, append validation, repository receipts, provider cursor acknowledgement, dirty-save retry and primary/cleanup exception ordering retain their existing behavior. Cancellation inside the awaited invocation loop can return a `Cancelled` result; the hook still receives and observes the original caller token. An event deliberately retained by an application continues to reference its original dictionary, but it cannot act as ambient state for another agent or a later invocation.

The component's evidence records the initial two runnable identity REDs and the expanded boundary cases. Cross-target, package/API, formatting and old compiled-consumer gates require their separately captured final proof; these tests do not establish full SDK parity or live-provider behavior.
