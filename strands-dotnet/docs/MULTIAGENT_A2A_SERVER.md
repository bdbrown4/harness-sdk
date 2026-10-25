# Portable A2A task hosting

This increment implements native-agent execution and a framework-neutral HTTP handler for the pinned A2A 0.3 JSON-RPC/SSE contracts. It supplements the [client increment](MULTIAGENT_A2A.md); it does not complete every upstream server, hosting, storage, or vended-tool surface.

Later increments add the [endpoint-bound client tool](MULTIAGENT_A2A_TOOL.md) and [pluggable task persistence](MULTIAGENT_A2A_TASK_STORE.md). Their scope manifests record the newer source and validation; the counts below describe the original hosting checkpoint.

The baseline remains `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `strands-ts/src/a2a/{executor,server,express-server}.ts`, Python `strands-py/src/strands/multiagent/a2a/{executor,server,_converters}.py`, and the [official v0.3 protocol](https://a2a-protocol.org/v0.3.0/specification/).

## Execution and hosting

```csharp
await using var executor = new A2AExecutor(new A2AExecutorOptions
{
    AgentFactory = (contextId, cancellationToken) => new ValueTask<Agent>(CreateAgent(contextId))
});
await using var server = new A2AServer(executor, new A2AServerOptions
{
    Name = "Research assistant",
    Description = "Investigates customer questions",
    Url = new Uri("https://example.test/a2a")
});

// In the application HTTP host:
using HttpResponseMessage response = await server.HandleAsync(request, cancellationToken);
// Forward status, headers and response.Content to the outgoing connection.
```

`A2AExecutor` creates and initializes one independent native `Agent` per context. It serializes visits within that context while allowing independent contexts to run concurrently. The bounded context pool defaults to 1000 entries and evicts only inactive, unpaused entries in least-recently-used order. Active, initializing and paused contexts retain their reservations. Failed initialization releases its reservation and cleans up transferred agents. Returning the same agent for different cached contexts is rejected without disposing the existing agent.

Factory results transfer ownership by default; `OwnsAgents = false` keeps them borrowed. `A2AServer` borrows its executor by default; `OwnsExecutor = true` transfers that resource explicitly. The HTTP host owns requests and returned responses. Response disposal releases the stream listener, and a disconnected listener does not silently cancel the underlying task. The host remains responsible for authentication, task/context authorization and transport hosting. Opaque identifiers are correlation values rather than authorization checks.

`InvocationOptionsFactory` can provide application invocation state and policies from `A2ARequestContext`. Protocol configuration and metadata remain explicit detached JSON. No SDK keys are inserted into caller invocation state. Discovery uses explicit metadata and never allocates a hidden representative agent.

## Implemented protocol operations

The handler serves the public card at `/.well-known/agent-card.json` and JSON-RPC at the configured URL path. It implements synchronous and nonblocking `message/send`, live `message/stream`, `tasks/get`, `tasks/cancel`, active-task `tasks/resubscribe`, and explicitly configured `agent/getAuthenticatedExtendedCard`. Completed, failed, canceled and rejected tasks cannot be restarted. A supplied context must match the existing task.

Resubscription captures its initial task snapshot and event cursor atomically, then follows subsequent updates. It does not duplicate an artifact at the snapshot boundary. The event buffer uses bounded replay and explicit listener limits; a reader that falls behind the retained cursor receives a protocol error instead of a silently incomplete successful stream. Malformed requests and invalid parameters retain JSON-RPC classification; unexpected backend initialization failures become correlated, sanitized internal errors.

Input and output MIME lists default to `text/plain` and `application/json`. Configure the corresponding file MIME types to enable media. Lists are copied at construction. `acceptedOutputModes` is negotiated before native-agent construction and enforced for generated artifacts. Push is disabled by default and enabled through explicit authorization and sender callbacks. Host authentication, gRPC and REST/HTTP+JSON serving remain outside this handler. [Authenticated extended cards](MULTIAGENT_A2A_CARDS.md) require an explicit host authorization callback.

## Human input and cancellation

A native paused batch becomes `input-required`, with opaque question identifiers in the pinned Python `interrupts` data envelope. Homogeneous `interruptResponse` data parts resume the original task and context. New prompts, mixed content, duplicate answers, unknown questions, replacement accepted answers and unrelated task continuations are rejected before publishing a working state. Accepted sibling effects are retained by the native agent ledger.

Active task cancellation cancels its execution lease and awaits native cleanup. Paused cancellation calls the native `Agent.AbortInterruptsAsync` transition: accepted sibling results remain successful, unfinished calls receive explicit errors in original assistant order, and unaccepted body checkpoints never become approved results. The projected cleared ledger and result message are persisted before changing in-memory history. A failed persistence attempt leaves the in-memory pause resumable; retry adds no duplicate message. The session backend supplies atomic SaveAsync semantics.

Abort requires an inactive agent. Cancellation before admission retains the caller token. Once persistence accepts the transition, it completes without cancellation; a late message callback failure still reports its original exception while preserving the durable aborted state and attaching cleanup failures under `Strands.CleanupErrors`. Concurrent invocations, snapshots, direct tools, shutdown and disposal cannot enter the transition. Executor and server disposal are idempotent, drain owned work and reject callback cycles; cleanup attempts continue after failures.

The native factory mode and explicit ownership are the supported construction path here. This does not reproduce the deprecated TypeScript borrowed single-agent snapshot mode. Strong cooperative cancellation includes Python behavior beyond the pinned TypeScript executor, whose cancellation method is unsupported.

## Bounds and projection

Defaults are 1 MiB request bodies, 16 MiB task snapshots, 1024 replay events, 16 MiB replay bytes, 32 listeners per execution, and 1000 cached tasks. Active and paused tasks are retained; terminal task entries can be evicted. These limits are configurable and do not replace host-level admission policies. The client has its own bounded response and SSE framing limits.

Artifact append preserves protocol part boundaries. The explicit TypeScript text-only policy therefore retains the pinned difference: complete Task text parts are joined with newlines, while text chunks from a streamed artifact remain contiguous. The [pinned official JavaScript result manager](https://github.com/a2aproject/a2a-js/blob/v0.3.10/src/server/result_manager.ts) appends artifact parts; the pinned Strands executor also emits an empty closing text part. Native rich projection preserves typed text content rather than changing those wire boundaries.

## Validation and remaining acceptance work

The complete core suite passed 1383 cases on each of net8/net10. All 138 A2A and abort cases passed in the linked compatibility project on net48/net8/net10, including three normal `HttpClientHandler` tests against a bounded TCP-loopback host. Local net48 execution uses installed Framework 4.8.1; clean Framework 4.8 cloud validation for this new server source is pending.

The initial execution, HTTP/RPC, live delivery, resubscription, cancellation, abort, negotiated output, and concrete review defects were developed with runnable failing assertions followed by implementation. Additional immediately passing lifecycle and wire cases are regression evidence. One initially failing text-projection expectation was disproved by the pinned source and corrected as a conformance assertion; it is not reported as a production defect or a red/green implementation fix. Formatting uses absolute includes with confirmed file coverage.

Subsequent increments implement [trusted stable-pause recovery](MULTIAGENT_A2A_RECOVERY.md), [configurable cards, skills and authenticated extended cards](MULTIAGENT_A2A_CARDS.md), and [remote graph stable-pause checkpoints](MULTIAGENT_A2A_GRAPH.md). Task JSON alone cannot authorize a replacement native execution. Remaining work includes active execution crash recovery, standalone/framework hosting adapters and separately scoped protocol extensions. These capabilities require separate acceptance tests and must not be inferred from the existing type names. Artifact append still projects generated artifacts through their identifiers and parts; broader generated artifact metadata remains separate acceptance work.

## Authorized push notifications

Set `A2AServerOptions.PushNotifications` to opt in. `A2APushNotificationOptions` requires a borrowed `SendAsync` callback and an `AuthorizeConfiguration` callback. The latter receives the current HTTP request and detached configuration before a message or set operation can store it or construct an agent. Use it to authorize the destination and credentials. The embedding host still authenticates RPC requests and authorizes task access.

The sender receives a detached task snapshot and authorized configuration. It can use an existing webhook service or a caller-owned HTTP client. For an HTTP sender, send the task as JSON, pass the configured token in `X-A2A-Notification-Token`, and configure the HTTP pipeline's authentication, TLS and redirect policy explicitly. Keep borrowed dependencies alive until server disposal finishes.

Push configuration operations are `tasks/pushNotificationConfig/set`, `/get`, `/list` and `/delete`. Set accepts `{ taskId, pushNotificationConfig }`; the others accept the task `id`, and get/delete can select `pushNotificationConfigId` (required for delete). Get returns one `{ taskId, pushNotificationConfig }`, list returns an array of those records, and successful delete returns null. A missing configuration id in an incoming message defaults to its allocated task id.

The default `InMemoryA2APushNotificationConfigStore` bounds total configurations to 1000 and retained JSON to 1 MiB; both limits are configurable. Supply a borrowed `IA2APushNotificationConfigStore` for another backend. Delivery follows task publication and any configured durable task save. Nonpersistent task-cache eviction and failure before the first publication retire unused configurations before the next execution. Persistent, addressable tasks keep their configurations.

Delivery is awaited. `DeliveryFailed` observes sender/store failures without changing an already published task outcome; the default trace records the failure type without credentials. Shutdown cancels and joins admitted configuration operations and sender callbacks. Callbacks that ignore cancellation must finish before disposal returns. An unconfigured server continues to reject push requests and advertises no push capability.
