# A2A client tool

`A2AClientTool` exposes the shared pinned `a2a_client` tool: `discover` returns the remote card, and `send_message` returns an assistant message from a fresh remote task. Both TypeScript and Python require an explicit endpoint allowlist and reject noncompleted remote tasks.

```csharp
using var http = new HttpClient();
var endpoint = "https://research.example/";
var client = new A2AClient(http, new(new Uri(endpoint)));
var tool = new A2AClientTool(new()
{
    AllowedEndpoints = new Dictionary<string, A2AClient> { [endpoint] = client }
});
```

Register the tool in `AgentOptions.Tools`. The native rich-tool path returns a `JsonBlock` and matching `StructuredContent`. Discovery preserves the raw agent card and its extensions. A send returns `{ "message": { "role": "assistant", "content": [...] } }` using the native .NET content-block JSON representation. Remote task metadata and native tracking IDs are excluded from this result.

Endpoint lookup uses ordinal exact matching before network access. A configured key ending in `/` does not authorize the same string without `/`. The constructor copies the bindings, so later dictionary mutation does not change authorization. Each binding supplies a borrowed `A2AClient`, allowing separate HTTP credentials and handlers per endpoint. The tool never disposes those clients or HTTP transports.

The keys authorize tool selection. Actual HTTP destinations, card-advertised RPC URLs, redirects, and authentication follow each supplied client and its `HttpClient`. Applications that require destination restrictions must configure those transports and, where appropriate, an explicit `A2AClientOptions.Endpoint`. This allowlist does not inspect redirect targets.

Each send creates an invocation-local `A2AAgent` without continuation IDs. Two calls therefore start independent remote tasks and contexts. The borrowed client's validated discovery cache remains shared. Caller invocation state stays local; no internal task or context IDs are written into it.

`A2AClientToolException` reports denied endpoints and unsuccessful remote operations. Transport or protocol failures remain available through `InnerException`, including their cleanup diagnostics. Caller cancellation propagates as `OperationCanceledException` with the caller token; a blocked response body is closed through the underlying client. Every task state other than `completed`, including `input-required` and `auth-required`, fails this stateless tool call. Use `A2AAgent` directly for resumable interactions.

The default name and base description match the pinned implementations. The generated description appends sorted permitted endpoints; custom name and description do not change authorization. `message` is optional for discovery and required and nonempty for sending.

The source baseline is `3bd98e618320f0f286acbfb2252f72388030421b`:

- `strands-ts/src/vended-tools/a2a-client/a2a-client.ts`
- `strands-py/src/strands/vended_tools/a2a_client/a2a_client.py`

Validation covers 25 focused tool cases on each modern core target and each linked compatibility target (`net48`, `net8.0`, `net10.0`). The net48 target ran on installed .NET Framework 4.8.1; clean 4.8 CI for this new slice remains pending. Real TCP loopback verifies that repeated tool sends create separate remote contexts. Per-endpoint authentication, exact denial before I/O, copied bindings, rich JSON, malformed input, all noncompleted states, blocked-read cancellation, cancellation during successful discovery-response cleanup, and primary/cleanup error identity are covered.

Three scenarios were observed failing against the initial tool scaffold before their implementations: endpoint authorization, native rich discovery, and real HTTP sending. Independent review then identified the discovery-cleanup cancellation edge; its regression failed before the cancellation check was added. Additional cases passed when first run and are regression evidence. The source and test manifest is `A2A_TOOL_SCOPE.json`.

This tool inherits the bounded JSON-RPC/SSE transport subset documented in `MULTIAGENT_A2A.md`. It does not add REST, gRPC, push delivery, or cross-process task persistence.
