# MCP tool providers

`McpToolProvider` connects the existing native MCP client to `AgentOptions.ToolProviders`. It is a borrowed provider object: the agent admits its consumer before loading tools and releases that consumer after shutdown hooks. Construction and `AddConsumerAsync` perform no connection or discovery I/O.

```csharp
using var http = new HttpClient(); // remains application-owned
var provider = new McpToolProvider(
    token => McpClient.CreateHttpAsync(http,
        new() { Endpoint = new Uri("https://example.test/mcp") },
        cancellationToken: token),
    new()
    {
        Name = "catalog",
        Discovery = new() { Prefix = "catalog" },
    });

await using var agent = new Agent(new AgentOptions
{
    Model = model,
    ToolProviders = [provider],
});
await agent.InitializeAsync();
```

The connector transfers a **fresh, owned `McpClient`** for each connection generation. Do not return a borrowed client or an earlier, disposed client. This transfers the protocol session; ownership of a separately supplied `HttpClient` still follows `McpClient.CreateHttpAsync` and remains with the application. Stdio connectors use the existing owned process transport, without another protocol loop. Connector options continue to select authentication, task negotiation, progress and transport behavior.

Each distinct `ToolProviderContext.ConsumerId` holds one consumer admission. Repeated declarations in the same agent use the same ID rather than a reference count. Loading connects and lists once for that generation; concurrent consumers join the same operation. Successful discovery preserves server order and caches the exact `McpTool` objects in a read-only list. Each agent still filters and captures its own admitted metadata through the generic provider pipeline. Nonempty repeated declarations can therefore fail the agent's ordinary duplicate-name validation; cleanup remains idempotent.

`ContinueOnError` defaults to `false`. When enabled, a noncancellation **connection** exception yields no tools and remains sticky until all consumers leave. It also enables the existing discovery rule which skips an otherwise valid tool whose nonempty prefix makes its name exceed the length limit. Other discovery, schema and protocol exceptions propagate. A failed discovery is retried on the same connected client on a later load; it is never converted to a cached empty success. Operation cancellation is never a sticky successful load. Native errors retain their original exception rather than introducing Python's `ToolProviderException` wrapper.

The connector and discovery receive the generation's lifetime token. Canceling a load caller cancels only that caller's wait, with that caller's original token; another admitted consumer can continue the same connection. The agent releases its canceled consumer through its existing initialization rollback. Removing the final ID publishes one shared close operation, cancels the generation, joins admitted connector/discovery work and releases the acquired client. A new admission is allowed during teardown, but its load waits for the old physical cleanup before opening another connection.

Cleanup has no timeout or detached completion mode. Connector, discovery callbacks and owned cleanup must cooperate or be released by their application. `RemoveConsumerAsync` checks a supplied cancellation token before changing admission; after removal is accepted, it joins cleanup even if that token is canceled later. Already reported load failures are not repeated as cleanup errors. Unobserved late failures remain observable through last-consumer removal; independent cleanup failures are attempted and attached through `Strands.CleanupErrors` without replacing the primary exception. Callback code cannot enter the same provider's own lifecycle and wait on work it is responsible for completing. Context-captured generation callbacks retain this guard until that generation closes.

The provider does not own agents, dispose borrowed tools, or add a generic refresh method. This increment does not change eager Harness `mcpServers`, named server delegation, list-changed notifications, live registry replacement, authentication policy or module reload. Ordinary MCP tools returned by a connector remain subject to the existing rich result, task and native transport contracts. Tests separately cover real loopback HTTP and real stdio processes; installed Framework 4.8.1 execution is not clean Framework 4.8 or hosted platform proof.

## Source contracts

Pinned upstream: `3bd98e618320f0f286acbfb2252f72388030421b`.

- Python `strands-py/src/strands/tools/mcp/mcp_client.py:526–574`: cached tool load; only `start()` is inside the continued connection-error catch. Discovery failures remain outside it.
- The same file at `576–635`: ordered paginated discovery, set-based consumer IDs, idempotent removal and last-consumer teardown; `700–717` resets connection/cache/failure state for a later generation.
- Python `strands-py/src/strands/tools/tool_provider.py:13–48`: add/load/remove without generic refresh. Native callbacks are asynchronous and explicitly receive the consuming agent context, avoiding Python finalizer/thread coupling.
- TypeScript `strands-ts/src/agent/agent.ts:861–870` instead lists MCP clients during initialization and subscribes the registry to their tools-changed callback. Its `shutdown()` at `1151–1153` does not implement Python consumer counting. That separate runtime behavior is not silently attributed to this adapter.
