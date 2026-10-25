# Explicit agent cycle checkpoints

`AgentOptions.CheckpointBehavior` opts an Agent into explicit model/tool cycle
pauses. `Disabled` preserves ordinary execution. `TypeScriptRegenerate` pauses
before retaining an assistant tool request and regenerates that request on resume.
`PythonReplay` retains the request and resumes its pending calls without another
model request. Both modes pause after an ordinary completed tool batch.

```csharp
await using var agent = new Agent(new()
{
    Model = model,
    Tools = tools,
    CheckpointBehavior = CheckpointBehavior.PythonReplay
});
AgentResult paused = await agent.InvokeAsync("Inspect the available information.");
AgentResult next = await agent.InvokeAsync(null, new()
{
    CheckpointResume = paused.Checkpoint
});
```

Check `StopReason` and `Checkpoint` at each boundary; a model can finish without
calling tools. `StopReasons.Checkpoint` carries an immutable `AgentCheckpoint`
with `AfterModel` or `AfterTools` and a zero-based cycle index. Resuming after tools
advances that index. Resuming a checkpoint cannot also append prompt/content input
or supply human-interrupt answers. Disabled execution and an overflowing next
cycle reject resume before idempotency admission. Python after-model replay also
requires the retained assistant tool request in the restored conversation.

The marker is a caller-selected continuation boundary. It is not a lease, an
authenticated receipt, a conversation, or evidence that a provider will accept a
particular restored state. Store the marker separately from a complete native
session or `AgentSnapshot`. Load the conversation/provider state before passing
the marker to a fresh Agent. Ordinary session/snapshot stores preserve the
conversation and provider-input receipts; they do not automatically persist the
marker or enable checkpoints on the new Agent.

When a fresh Agent loads its session during the first invocation, a separately
serialized `CheckpointResume` marker with `CheckpointBehavior.PythonReplay`
preserves restored request/tool-result history across source-profile
initialization, for both `AfterModel` and `AfterTools`. This applies to the
invocation that wins first initialization. It does not recover history already
cleared by a completed `InitializeAsync`; concurrent initialization winners and
`TypeScriptRegenerate` restoration remain separate acceptance work. Native
paired pending tool batches retain their owned history.

A real TypeScript-style discarded response still consumes its provider input.
The existing [receipt/state pair](STRUCTURED_OUTPUT_SOURCE_PROFILES.md) preserves
that fact without appending the discarded assistant or resending consumed input.
Python replay retains the accepted assistant and sends only pending tool results
after the next boundary. A real provider must support regeneration from its
server cursor; providers requiring outputs for every server-side tool call may
reject TypeScript regeneration until their own protocol requirements are met.
Fake-provider restoration tests do not establish that live-provider capability.

Human interrupts, hard caller cancellation, requested cancellation, accepted
EndTurn content, structured-output completion and pipeline/cleanup failures keep
their existing precedence. Answer a human interrupt through `InterruptResponses`;
it has already passed the model boundary. Accepted effects are not repeated.
Checkpoint results disable automatic after-invocation continuations, including
goal evaluation. A failing finish hook repairs a retained pending request before
the error escapes. An invocation policy denial also closes an ordinary pending
checkpoint request before appending its denial, without executing its tool.

Each explicit resume is a new public invocation with fresh invocation usage,
turn accounting and hooks. This API does not add a generic durable checkpoint
protocol to graph nodes, wrapped tools or Swarm orchestration. Those integrations
retain their documented interruption and ownership contracts.

`InvokeStructuredAsync<T>` continues to return only a completed typed value.
A checkpoint or other non-tool-use result throws the existing
`StructuredOutputException` with its additive nullable `Result` property set to
the actual invocation result. No T is fabricated and no converter runs merely
to expose a pause. Hard caller cancellation still throws with its caller token;
provider failures retain their identity. `Result` is live invocation data; use
`AgentResultJson` explicitly when a safe result envelope is needed. The existing
exception constructor and ordinary successful typed result remain compatible.

The private implementation reuses the existing discarded-response acceptance
transition for structured output and checkpoints. The design favors an explicit
state transition over another public abstraction; see Scott Hanselman's
[discussion of indirection](https://www.hanselman.com/blog/im-just-a-caveman-the-hanselman-corollary-to-the-clarkewheeler-laws).
