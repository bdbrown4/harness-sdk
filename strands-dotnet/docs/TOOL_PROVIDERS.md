# Generic tool providers

`AgentOptions.ToolProviders` accepts borrowed `IToolProvider` instances. Construction copies and validates the definitions without calling them. `InitializeAsync` restores the session, then processes providers in declaration order before plugins, generated delegation tools, background policy and `InitializedEvent`. Application tools precede all providers. Unlike Python's mixed `tools` input, these two native lists cannot interleave ordinary tools and providers.

Each agent has one `ToolProviderContext` shared across every declaration. Its opaque `ConsumerId` is independent of the persisted agent/session ID. Repeated references are processed repeatedly with that same context, including repeated empty providers. Children inherit the definitions but create a new context and reload tools; they never copy their parent's provider bindings.

The agent tracks each declaration before `AddConsumerAsync`, then awaits `LoadToolsAsync`. Returned tools are copied, filtered before reading their specifications, and the entire accepted batch is validated before publication. Registration keeps each exact borrowed tool object, including rich, streaming, structured-output and delegation capabilities. A detached specification and compiled input schema are fixed for that consumer; later provider changes require a new consumer. The public `Agent.Tools` view still contains the original objects. Application callbacks that explicitly read those objects' own `Spec` getters remain application code, and implementation state inside the borrowed tool is not cloned.

Any initialization failure removes only provider publications and calls `RemoveConsumerAsync` for every tracked declaration in forward order, including a declaration whose add/load failed. The primary failure is retained, with secondary failures in `Strands.CleanupErrors`. Once a provider consumer has been admitted, an initialization `OperationCanceledException` is retained as the fault cause rather than replaced by a task-cancellation sentinel. Cleanup always receives `CancellationToken.None`. All callbacks must cooperate with cancellation; arbitrary application callbacks cannot be forcibly terminated.

Shutdown runs all `ShutdownEvent` callbacks while provider consumers remain available, then removes consumers once. Disposal joins that memoized shutdown before releasing separately transferred `OwnedResources` and the explicitly owned model. Every removal is attempted even when another fails. Providers and tools are never implicitly disposed. Providers manage their own shared resources after their last consumer leaves; removal must be idempotent. Explicit ownership transfer through `OwnedResources` remains available independently.

Lifecycle callbacks run under the agent's existing reentrancy guard. They cannot await their own initialization, shutdown or disposal, or form an ancestor lifecycle wait. Calling a provider from multiple agents is supported; the provider is responsible for synchronizing its own shared state. No provider callback is bound through ambient per-agent adapters.

This slice has no provider refresh method, runtime provider mutation, MCP-provider adapter, or generic automatic reload. Existing application-tool mutation APIs cannot overwrite provider registrations. Eager harness `mcpServers` behavior is unchanged.

## Source contract

Pinned upstream `3bd98e618320f0f286acbfb2252f72388030421b`:

- Python `src/strands/tools/tool_provider.py:13–48` defines asynchronous `load_tools`, synchronous `add_consumer` and idempotent synchronous `remove_consumer`, with no refresh method.
- Python `tools/registry.py:127–142` tracks providers before add/load and registers returned tools in order; `695–710` removes every tracked declaration in forward order and raises the first failure after attempting all.
- Python `agent/agent.py:1215–1225,1351–1356` exposes explicit registry cleanup plus a finalizer fallback. Native asynchronous initialization/shutdown replace synchronous constructor blocking and finalizer work; no asynchronous cleanup is promised from a .NET finalizer.
- TypeScript `src/plugins/plugin.ts:68–76` vends tools after `initAgent`, already represented by native `IPlugin`; it has no equivalent generic three-method provider interface.

Native asynchronous add/remove and explicit `Agent` context are C# integration choices. These do not execute Python/TypeScript modules or grant filesystem/process authority.
