# Native A2A interoperability

This increment implements a portable remote client and remote-agent adapter. The [executor and HTTP handler](MULTIAGENT_A2A_SERVER.md), [vended endpoint tool](MULTIAGENT_A2A_TOOL.md), [task stores](MULTIAGENT_A2A_TASK_STORE.md), [stable-pause recovery](MULTIAGENT_A2A_RECOVERY.md), [discovery cards](MULTIAGENT_A2A_CARDS.md), and [remote graph stable-pause bridge](MULTIAGENT_A2A_GRAPH.md) have separate acceptance evidence. Hosting adapters and full A2A-family parity remain acceptance work.

The source baseline is `3bd98e618320f0f286acbfb2252f72388030421b`:

- TypeScript: `strands-ts/src/a2a/{a2a-agent,events,executor,adapters,server}.ts`, their tests, and `strands-ts/src/vended-tools/a2a-client/a2a-client.ts`.
- Python: `strands-py/src/strands/agent/a2a_agent.py`, `strands-py/src/strands/multiagent/a2a/{_converters,executor,server}.py`, and `strands-py/src/strands/vended_tools/a2a_client/a2a_client.py`.

Both pinned packages use A2A 0.3 SDKs; the TypeScript server still advertises the legacy `0.2.0` card version. The native client accepts those card versions and uses the [v0.3 JSON-RPC/SSE and HTTP+JSON protocols](https://a2a-protocol.org/v0.3.0/specification/). It rejects v1 cards and unimplemented transports. The official [.NET v0.3 package currently targets net8/net10](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A.V0_3/A2A.V0_3.csproj), so it cannot supply this package's netstandard2.0/Framework 4.8 asset.

## Client and remote agent

```csharp
using Strands.Agents.A2A;

using var http = new HttpClient(); // Configure authentication on this caller-owned transport.
var client = new A2AClient(http, new(new Uri("https://remote.example/")));
var agent = new A2AAgent(client);
var result = await agent.InvokeAsync("Research the issue", cancellationToken: cancellationToken);
```

`A2AClient` performs cached, cancellable card discovery, selects an advertised JSONRPC or HTTP+JSON interface, and supports explicit endpoint and card-path configuration. `CallAsync` and `StreamCallAsync` retain raw protocol JSON and extension fields. Task retrieval, cooperative remote cancellation, resubscription and authenticated extended-card replacement have named methods. JSON-RPC request identifiers are generated locally and verified on every response. Remote errors retain their JSON-RPC codes and detached data, including immediate JSON error envelopes returned to a streaming request. JSON success on that path remains invalid.

`A2AAgent` offers text and typed-content invoke/stream methods. Streams yield detached `A2AStreamUpdateEvent` objects followed by one `A2AResultEvent`. Artifact replacements and appends are aggregated by artifact identifier and first appearance. A final status-update message takes precedence over earlier artifacts, matching both pinned adapters; a complete Task result prefers artifacts over its status-message fallback. The terminal event exposes task/context/state identifiers and raw final data; the same information is retained under `Message.Metadata.a2a` when consuming only `InvokeAsync`.

The default content policy preserves text, object data, base64 images/audio/video/documents and resource links. HTTP image sources become URI file parts. Returned URI file parts remain references; the SDK does not download them. S3 media and guardrail selections are rejected explicitly because the standard A2A part cannot retain their native source-owner or guard semantics. Other unsupported input or output also fails explicitly; raw parts remain reachable through stream events.

`ContentMode = TextOnly` selects the pinned TypeScript text projection. Text parts within a chunk are joined with newlines; appended chunks remain contiguous, and distinct artifacts are separated by a newline. `TaskStateMapping = Protocol` is the default Python behavior: input-required/auth-required map to `interrupt`. `AlwaysEndTurn` selects the TypeScript stop-reason behavior. Remote failure/cancellation/rejection details remain in the remote state metadata, regardless of the selected local stop reason.

Caller `InvocationState` stays local and is returned by reference without SDK keys. Only explicitly supplied protocol configuration and metadata are transmitted. Input content and JSON protocol options are detached before discovery begins. Task continuation is explicit through `TaskId`/`ContextId`; a structured `JsonBlock` can carry the pinned Python `interruptResponse` envelope.

## Ownership and bounds

The caller owns `HttpClient`, handlers and remote conversations. Each call owns its HTTP response and body. Disposing a stream releases the response. Local cancellation closes blocked body reads; it does not silently issue `tasks/cancel`. That remote operation requires an explicit call. When cleanup also fails, the original failure is retained and secondary errors are attached under `Strands.CleanupErrors`.

Complete JSON responses default to 16 MiB. SSE frames have a 4 MiB bound inherited from the shared bounded UTF-8 reader. Retained result content defaults to a conservative 16 MiB budget that also charges identifiers and per-part/chunk bookkeeping; empty artifact identifiers cannot evade it. Repeated empty appends retain no extra chunks. Unsupported, malformed, ambiguous, uncorrelated and prematurely closed streams fail rather than produce a successful empty result. This deliberately differs from the pinned TypeScript empty-stream fallback.

Cards and context identifiers are application data, not authentication boundaries. Credentials, transport authentication and authorization of advertised endpoints belong to the caller's HTTP handler and hosting layer. The client does not implement gRPC or automatic retries. The native server offers explicitly authorized push delivery.

## Validation

The bounded client increment has 76 cases passing in the core test project on net8/net10 and the linked compatibility project on net48/net8/net10, with zero build warnings. Local net48 execution uses the installed Framework 4.8.1 runtime; a clean Framework 4.8 CI run for this new source is still pending.

The first discovery/RPC/SSE/invocation scenarios and targeted defects were developed through runnable failing tests followed by implementation. Additional malformed-stream, rich-content, lifecycle and transport cases are regression/conformance evidence, not a claim that every case was authored in a separate red/green cycle. Real TCP-loopback fixtures exercise normal `HttpClientHandler`, Content-Length/chunked bodies, split UTF-8, input-required continuation and cancellation. Fake handlers cover deterministic correlation, error and cleanup paths. No credentials or live model services are required.

## REST-only remote agents

Card-selected `HTTP+JSON` endpoints use A2A 0.3 REST routes and protobuf JSON. Native calls still expose the SDK's canonical message/task JSON. The client supports send, stream, task get/cancel, GET resubscription, authenticated cards, and all four push-configuration operations. JSON-RPC remains supported; an explicit endpoint override retains the selected transport binding. Standard protobuf defaults and task/security/file/data conversions are covered by focused interoperability tests. JSON-RPC-only extension fields are not a promise of protobuf wire interchange.

The native server continues to host JSON-RPC/SSE. Client REST support does not add REST server routes or gRPC. Authentication and advertised endpoint trust remain application responsibilities.
