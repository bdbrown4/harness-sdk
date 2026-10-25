# MCP router

`McpRouter` is an ordinary `IRichTool` for the five upstream commands: `connect`, `list_connections`, `list_tools`, `call_tool`, and `disconnect`. Its default name is `mcp_router`; the connection limit defaults to ten per agent. Command arguments retain the protocol-facing names `connection_id`, `server_name`, `tool_name`, and `arguments`.

The application supplies a fixed allowlist. Model input selects a name from that map; it cannot supply a URL, command, transport, or credentials. A stdio allowlist still authorizes execution of the configured executable with the process owner's permissions. Neither the router nor the local sandbox provides host isolation.

```csharp
using Strands.Agents;
using Strands.Agents.Mcp;

await using var router = new McpRouter(new()
{
    Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
    {
        ["catalog"] = cancellationToken => McpClient.CreateStdioAsync(
            new() { Command = "catalog-mcp", Arguments = ["--stdio"] },
            cancellationToken: cancellationToken)
    }
});
// Add router to AgentOptions.Tools alongside the application's supplied model.
```

Each connector transfers a fresh connected `McpClient`. Returning a borrowed client or reusing one already transferred to this router is rejected. The router copies the connector map and settings; application callbacks retain responsibility for any external state they capture. No connection starts in the constructor.

## Declarative harness binding

The harness connection configuration also creates a router without connecting or discovering tools:

```csharp
using Strands.Harness;
using Strands.Harness.Mcp;

var servers = McpServerConfiguration.Parse(
    """{"catalog":{"url":"https://catalog.example/mcp"}}""",
    continueOnError: false);
await using var router = await servers.CreateRouterAsync(selectedSandbox);
```

`selectedSandbox` is an application-supplied `ISandbox`. Configuration files are read through that exact backend. Commands, separated arguments, the official default stdio environment plus explicit overrides, working directories, headers, client credentials, and task profiles reuse the existing MCP connection implementation. There is no fallback to another filesystem or transport.

`CreateRouterAsync` snapshots and resolves the configuration and environment once. This is an explicit native timing choice: the upstream factories keep their JavaScript/Python server configuration and resolve the chosen entry when `connect` runs. The native allowlist retains disabled and continued-invalid server names, but connecting those names fails without acquiring a transport. Strict configuration errors fail during native router creation. Private HTTP clients and their authentication handlers are owned by the acquired MCP client.

The existing harness `McpServers` option remains eager: it connects and discovers tools during harness construction. Neither upstream harness automatically registers `mcp_router`, so native registration is also explicit.

## Commands and results

- `connect` requires `server_name` and `connection_id`. Admission checks the per-agent cap and duplicate ID after connection awaits. A rejected candidate is closed; an existing connection is retained.
- `list_connections` returns a comma-separated ordinal-sorted list of IDs, matching both upstream routers.
- `list_tools` requires `connection_id` and returns specifications in server order across bounded pagination. Names remain the original server names, with no registration prefix. These tools are not added to `Agent.Tools`, so the agent's 64-character registration-name limit does not reject a server-only name. Schema, pagination, count, and duplicate-name validation remain active.
- `call_tool` requires `connection_id` and `tool_name`; omitted `arguments` becomes an empty object. Prior discovery is optional. Discovery filters select advertised tools and do not authorize or prohibit a direct server-side call.
- `disconnect` removes the ID and drains operations already using that connection. A normal close failure is swallowed, matching upstream disconnect semantics. Cancellation stops only the caller's wait; the owning agent/router retains the pending close and joins it during shutdown/disposal.

`InvokeResultAsync` retains correlated native content blocks and structured results, including JSON `false` and `0`. `InvokeAsync` projects text and JSON to a string and rejects media rather than silently discarding it.

`McpRouterErrorPolicy.Throw`, the default, throws `McpRouterToolException` on an MCP `isError` result, matching TypeScript. `ReturnResult` returns the native rich error result, matching Python's status-bearing result behavior. This does not replace protocol failures with successful results.

Nonempty MCP `_meta` is preserved as `ToolResultBlock.Metadata`, a detached JSON object. Empty metadata is omitted, following Python dictionary truthiness. The native JSON field is `metadata`; provider payloads exclude it. It survives tool normalization, committed history, snapshots, file sessions, and input-content redaction. Native `Success`/`Error` represents protocol error status; it does not separately preserve whether the optional raw `isError` field was absent or explicitly false. `McpClient.CallToolAsync` remains available for the complete official protocol result.

## Ownership and cancellation

Connections are independent for each `Agent`, including agents created with `CreateChild`. They survive invocations but are not serialized into sessions or snapshots. The router never uses application invocation state for connection coordination.

Agent shutdown closes that agent's connections. Explicit router disposal closes every live per-agent connection set, attempts all owned cleanup, and retains secondary errors. Both memoize their cleanup tasks. Active command and cleanup callbacks cannot await their own shutdown/disposal, including a connection close retained after a canceled disconnect wait. Garbage-collection cleanup is best effort and supplements explicit lifetime management.

Cancellation propagates to official MCP requests. A connector can ignore cancellation; the router stops waiting, observes its eventual result/fault, and closes a late fresh client. Its physical connection slot remains occupied until that connector finishes, so repeated cancellation cannot start unbounded connector work. Router shutdown does not wait forever for a factory that has not returned a client. The cancellation exception exposes a no-fault `Task<IReadOnlyList<Exception>>` at `Data["Strands.CleanupTask"]` for that deferred cleanup. A canceled disconnect wait uses the same diagnostic while its already acquired connection remains owned by the router. These are process-local diagnostics, not a binary exception-serialization contract.

`McpClientSettings.OwnsTransport` explicitly transfers a caller-supplied `IClientTransport` factory. The default is false. When true, the session closes before the factory's `IAsyncDisposable` or `IDisposable` cleanup; connection failure and cancellation also release it while retaining the primary error. HTTP/stdio convenience methods own the factories they allocate. Supplying an `HttpClient` to `CreateHttpAsync` always leaves that client borrowed.

## Source contract and validation scope

The reference is upstream commit `3bd98e618320f0f286acbfb2252f72388030421b`: [TypeScript router](../../strands-ts/src/vended-tools/mcp-router/mcp-router.ts), [Python router](../../strands-py/src/strands/vended_tools/mcp_router/mcp_router.py), and [Python result conversion](../../strands-py/src/strands/tools/mcp/mcp_client.py). TypeScript throws on remote tool errors and returns content arrays; Python returns its status/content/metadata envelope. The native explicit policies and typed result cover those meaningful alternatives without claiming identical foreign-language object layouts.

Deterministic protocol and loopback tests exercise admission races, cancellation, ownership, rich metadata persistence, and provider serialization. Real stdio fixtures cover arguments, UTF-8, environment, working directory, process reaping, and selected task generations. Live remote servers, identity providers, and provider credentials are separate integration evidence. This slice does not establish full SDK/harness parity or PR readiness.
