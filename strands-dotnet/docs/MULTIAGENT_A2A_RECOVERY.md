# A2A stable-pause recovery

The server can reopen a persisted `input-required` task when its executor factory restores the matching native agent session. Task JSON and native execution state remain separate records. Configure a supplied `IA2ATaskStore` on the server and restore the native session by the factory's context identifier:

```csharp
var sessions = new FileSessionStore(".strands-sessions");
await using var executor = new A2AExecutor(new()
{
    AgentFactory = (contextId, cancellationToken) => new ValueTask<Agent>(new Agent(new()
    {
        Model = model,
        Tools = tools,
        SessionId = contextId,
        SessionStore = sessions
    }))
});
await using var server = new A2AServer(executor, options with
{
    TaskStore = new StorageA2ATaskStore(new LocalFileStorage(".strands-tasks"))
});
```

The application must recreate compatible tools and model configuration. Native session restoration runs during agent initialization. Model and tool resources retain their explicit native ownership rules; the A2A executor owns factory agents by default, and it borrows the configured stores.

An existing task identifier causes the server to load its stored task before admitting a continuation or cancellation. Only a stable `input-required` boundary supports this recovery. The restored native interrupt ledger must contain the same outstanding question identifiers, names, and JSON reasons as the task's published status message. A missing session, unrelated pause, conflicting task/context, altered questions, or unsupported persisted state fails before publishing `working`, accepting an answer, or invoking a resumed tool. Task JSON alone cannot start a replacement execution.

`A2ARequestContext.CurrentTask` exposes the detached, correlated task to trusted executor callers. The HTTP handler supplies this property from its own task store; a remote client's message fields cannot set it. The embedding application still owns authentication and task/context authorization.

Structured answer parts retain the original task and context identifiers. Native session persistence preserves accepted answers and completed sibling tool results, so restarting between partial answers does not replay those accepted effects. Cancellation reattaches the same pause, settles its native ledger using `AbortInterruptsAsync`, and then publishes `canceled`. A failed native abort save leaves the original pause available for a subsequent retry. A failed initial `working` task save accepts no answers and invokes no resumed tools.

One server instance admits one active continuation per task. Native context leases also serialize access within an executor. Caller cancellation during native restoration preserves the caller token and leaves the original persisted ledger intact.

The two stores do not form a distributed transaction. A crash between writes can leave a task projection and native ledger that fail the matching check. Recovery fails closed in that case; the SDK does not infer authorization from incomplete records or replay an active execution. Running-crash recovery, cross-process leases, automatic repair of divergent stores, task retention, and push delivery remain separate capabilities.

Source alignment: both pinned servers accept supplied task stores, and the [official JavaScript request context](https://raw.githubusercontent.com/a2aproject/a2a-js/v0.3.10/src/server/agent_execution/request_context.ts) includes the current task. Native execution reattachment additionally uses the SDK's durable interrupt/session contracts. The task store contract is described in [the official JavaScript implementation](https://raw.githubusercontent.com/a2aproject/a2a-js/v0.3.10/src/server/store.ts); protocol task continuation is defined in the [A2A 0.3 specification](https://a2a-protocol.org/v0.3.0/specification/). Pinned baseline: `3bd98e618320f0f286acbfb2252f72388030421b`.

Regression coverage includes fresh filesystem task/session stores, resume and cancel after reopening, partial answers across multiple restarts, accepted-effect retention, question mismatches, missing native sessions, conflicting contexts, concurrent resume admission, detached request data, unsupported recovery states, canceled restoration, and failed native/task saves. Exact source hashes and validation evidence are recorded in `A2A_RECOVERY_SCOPE.json` when this increment freezes.
