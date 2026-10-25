# Remote A2A graph nodes

`A2ANode` borrows an `A2AClient` and adapts its real JSON-RPC HTTP/SSE exchange to native graph progress and typed terminal content. It does not own the client, its `HttpClient`, or the remote service. A shared node declaration can participate in separate graph invocations without storing invocation state on the node or adding SDK keys to caller `InvocationState`.

```csharp
using var http = new HttpClient();
var client = new A2AClient(http, new(new Uri("https://remote.example/")));
var graph = new Graph(new()
{
    SessionId = "research",
    SessionStore = new StorageMultiAgentSessionStore(new LocalFileStorage("./graph-state")),
    Nodes = [new A2ANode(client, "remote")]
});
var result = await graph.InvokeAsync("Research the question.");
if (result.Status == Status.Interrupted)
{
    var answer = new InterruptResponse(result.Interrupts[0].Id,
        JsonSerializer.SerializeToElement("Approved"));
    result = await graph.InvokeAsync(MultiAgentInput.Resume([answer]));
}
```

The graph uses the actual remote task state: completed tasks complete the node, failed/rejected tasks fail it, and canceled tasks cancel it. `input-required` and `auth-required` produce native `RemoteAgent` requests correlated with the node identifier. That source cannot enter a native agent's model-issued tool approval ledger. The standalone `A2AAgent` stop-reason compatibility option does not change these graph lifecycle decisions.

For a Strands-style `data.interrupts` declaration, each local opaque question maps privately to one remote `interruptId`. Responses use the server's `data.interruptResponse` protocol without exposing remote IDs as local tool approvals. This protocol data remains intact when visible content uses `A2AContentMode.TextOnly`. Partial responses retain the remaining identities across fresh graph instances. A valid new pause removes only deferred answers that its authoritative outstanding IDs prove consumed, including when the consumer disposes immediately after observing that pause.

A service that supplies no structured questions gets one local `remote-input` or `remote-authentication` request. Its response must be a JSON string, which is sent as ordinary remote text. A subsequent observed request receives a fresh opaque identity. Invalid response types and mixed invalid hook/body answer batches fail before any answer is accepted, persisted, or sent. Authentication itself remains the embedding host's responsibility; a graph question is not a credential exchange implementation.

Stable pauses are captured through the existing per-invocation graph checkpoint codec. The checkpoint carries a versioned source, node/task/context identities, detached protocol pause, and local-to-remote question mapping. It binds hashes of the discovery URL, configured endpoint override, and selected JSON-RPC endpoint. URL credentials and query values are not copied into those binding fields. Configuration mismatch fails during pure load validation; a changed discovered endpoint fails after discovery and before any continuation request. The active visit uses a client bound to that selected endpoint so a separate authenticated-card refresh cannot reroute it mid-visit.

`MaxCheckpointBytes` defaults to 256 KiB of serialized UTF-8, and `MaxQuestions` to 64. Loading validates all node data before replacing graph state, including the captured question source, correlation, name, reason and outstanding membership. The envelope is trusted application persistence, not a signed or authenticated token. Endpoint binding does not authorize access to a remote task; the host must protect its storage and transport credentials.

Accepted answers are saved before node execution when a graph session store is configured. This includes retrying already accepted answers with `Resume([])` after a failed save. A fresh graph can load that journal and continue the original remote task without repeating accepted sibling effects.

Before sending a continuation, the node reads `tasks/get`. An already completed remote response can therefore be recovered after its HTTP response was lost, without resending the answer. Structured IDs missing from a new pause are treated as consumed. A still-active task is not sent another message; the host can retry the safe read later. For unstructured text, a handled failed or prematurely disposed send records uncertain acceptance and refuses automatic replay while the task remains paused. Terminal task reconciliation remains available. The host must resolve that ambiguity explicitly.

These boundaries do not provide an atomic transaction between local storage and remote effects. A process crash during a send, active execution recovery, cross-process leases, general reconnection, and arbitrary remote receipt/idempotency schemes remain outside this stable-pause increment. Canceling or disposing a local wait does not request `tasks/cancel`; remote cancellation remains the explicit client operation.

The pinned TypeScript `strands-ts/src/a2a/a2a-agent.ts` provides an invokable remote adapter with text projection. Python's `strands-py/src/strands/multiagent/a2a/_converters.py` maps remote pause states but explicitly rejects client interrupt-response input, while its `executor.py` exposes the structured response protocol. This native bridge deliberately composes the working native graph checkpoint and server protocols; it is not a claim that both upstream remote clients already implemented durable resume. The protocol transport follows the official [A2A 0.3 specification](https://a2a-protocol.org/v0.3.0/specification/).

Validation uses real loopback TCP HTTP/SSE, file-backed fresh graph sessions, both modern targets and the netstandard asset on a net48 target running installed Framework 4.8.1. Clean Framework 4.8 CI for this increment remains a separate gate. Exact hashes and counts appear in `A2A_GRAPH_SCOPE.json` after capture.
