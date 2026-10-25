# Native background tool execution

The core SDK enables background work with `AgentOptions.BackgroundTasks = new BackgroundTasksConfig()`. Null disables it. The harness enables the component by default; set `HarnessOptions.BackgroundTasks = null` to opt out. An explicitly supplied core configuration takes precedence. This is an in-process scheduler, not a durable external job service.

```csharp
using Strands.Agents;
using Strands.Agents.Tasks;

await using var agent = new Agent(new AgentOptions
{
    Model = model,
    Tools = tools,
    BackgroundTasks = new BackgroundTasksConfig
    {
        Agentic = new[] { "research" },
        Always = new[] { "index_documents" },
        Never = new[] { "delete_account" },
        MaxConcurrency = 4,
        Timeout = TimeSpan.FromMinutes(2),
        WaitForCompletion = true,
    },
});
```

## Admission and execution

The default agentic selection is `["*"]`, except when an explicit Always or Never wildcard suppresses that default. Exact names override a wildcard. Assigning the same name to different modes is an error. C# selectors are registered names; use `tool.Spec.Name` when starting with a tool instance. `Validate()` checks limits and conflicting assignments before resolving executable dependencies.

Eligible agentic tool specifications gain an optional boolean `_background_execution` property. The registered specification is unchanged. Before-tool callbacks see the requested call once and may approve, deny, or replace it. Routing then uses the original requested tool's selection mode and the effective selected tool's authority. The flag is removed before the real schema is validated. The management tool, structured output, and context-management tools remain in the foreground. Composite schemas that cannot safely accept a new root property remain in the foreground under a wildcard; an explicit incompatible agentic assignment fails.

Admission returns a correlated successful acknowledgement containing the task ID. It does not invoke the body or an after-tool callback for that acknowledgement. The scheduled execution runs ordinary tool middleware, schema validation, the actual tool, progress callbacks, and the after-tool result callback. Tool errors retain rich content; a pipeline exception becomes an execution error. Invocation state remains the caller's original dictionary and the scheduler adds no entries.

The FIFO scheduler limits **physical executions**. A queued task consumes no physical slot. Cancellation or an execution deadline commits the visible terminal status immediately and signals that execution's token, but an uncooperative body retains its slot until it actually exits. Late output cannot replace the committed cancelled or timed-out status. A resumed input request starts a new execution deadline. Waiting can be cancelled or timed out independently without cancelling accepted work.

The lifecycle is `queued → working → completed | failed | cancelled`, with `working → input_required → queued` for human input. Input-required work is settled for a task waiter and consumes no slot once that physical attempt exits. Submission deduplicates an existing `(assistant tracking ID, tool-use ID)` pair; removing its retained terminal record permits a future admission.

## Results, input, and persistence

`strands_manage_background_task` supports `list`, `get`, and `cancel`. A get result starts with JSON task metadata and retains the original rich result content. Cancelling an already terminal task is idempotent. Removing retained records is internal and validates the entire batch before changing anything.

Completed records are delivered automatically as synthetic assistant management calls followed by correlated successful get results. A failed task is described in the get metadata; retrieving that description succeeds. Complete pairs are committed before callbacks, and accepted records are removed exactly once. A message callback failure retains both history and the accepted delivery state, so the next invocation cannot repeat a side effect or redeliver the pair. Session-write failures retain dirty state for a later write attempt.

The default invocation waits for pending tasks at its after-invocation boundary and starts another complete lifecycle cycle to consume delivered results. Each automatic cycle runs fresh before/after invocation hooks with the original invocation state, while turn and token accounting remains cumulative. `WaitForCompletion = false` returns while work remains active; later model boundaries deliver ready results. Cancelling the invocation stops its wait and returns a cancelled result without cancelling accepted background work. Shutdown cancels outstanding work and joins its physical executions before the agent disposes owned models or transports. A body that ignores cancellation can therefore delay shutdown.

Before-model and after-invocation delivery uses internal input transactions. Each complete tool-use/result sequence is validated and detached before history changes. An accepted batch commits atomically, removes its task records once, and then notifies message observers and saves the session. Observer or session-write failures retain accepted pairs without repeating task bodies or delivery. An unappended batch retains its task records after an invocation hook fails or a stream is disposed at the after-event boundary. Turn/token caps prevent automatic input from being appended; a later invocation can consume the retained result. Explicit hook input follows any prepared automatic pairs. Inputs deferred by a human pause remain pending until a later successful after-invocation boundary; shutdown abandons those deferred callbacks once. Callback diagnostic failures cannot replace the invocation result.

Tool and middleware input requests use task-local, detached interrupt state. Resume with the returned `AgentResult.Interrupts` and `InvocationOptions.InterruptResponses`; do not supply a new prompt. Accepted answers remain stable across replay. A completed body is checkpointed when outer middleware requests input afterward, so resuming that middleware does not repeat the side effect. A before-tool approval occurs before admission and retains the ordinary foreground batch/abort behavior.

An `AfterToolCallEvent` callback can set `Retry = true` to discard that attempt's result and execute it again. Foreground and direct retries repeat the before hook; background retries repeat middleware, the body, and the after hook without a second foreground admission. An explicit retry discards the rejected body's checkpoint, so a later pause cannot restore that rejected result. Progress already streamed by the rejected attempt remains visible. Callbacks requesting retries are responsible for repeated side effects. Cancellation and failed iterator cleanup prevent a retry; cleanup failures retain their original exception and secondary diagnostics. Direct calls continue to refuse human input even when a callback requests retry.

Cleanup disposition belongs to the completed attempt, so clearing an exception's diagnostics or reusing that exception in an unrelated body does not change retry authority. Native interrupted-body journals persist that private disposition through session save and restore; ordinary tool-result/provider JSON excludes it. Older journals without the field cannot reconstruct historical cleanup failures. Rejecting a structured-output attempt also discards only that call's cached value; another accepted call and validation-attempt accounting remain intact.

Task records occupy the reserved application-state key `strands.backgroundTasks` and participate in native sessions/checkpoints. Process restart cannot resume physical work: recovered queued, working, or input-required records become failed execution-error records. Completed records retain their content for delivery. Malformed status/result/error combinations fail before checkpoint mutation. Loading another checkpoint is refused while the live manager still tracks tasks, including retained terminal tasks; recovered display-only records do not masquerade as live executions. Fresh default children inherit configuration and user application state but start without the parent's task records.

## Hook-driven continuation

An `AfterInvocationEvent` callback can set `Resume` to an `InvocationContinuation` constructed from user text, ordered content blocks, complete conversation messages, or human interrupt responses. The constructors detach lists and JSON and reject malformed input. Null stops; `InvocationContinuation.Continue`, empty messages, and empty content append nothing. An explicitly empty string adds a user message. Answers must target the exact outstanding interrupt identifiers, and a paused tool batch cannot accept a new prompt.

Each resumed cycle invokes fresh before/after hooks with the original `InvocationState` dictionary and invocation options. The before and after events are also streamed. Only the last cycle produces an `AgentResultEvent`. The invocation guard stays held throughout: a hook cannot recursively invoke the same agent. Complete input batches commit before message observers run, so an observer failure cannot leave half of an imported tool exchange. Errors, caller cancellation, before-invocation denial, and early consumer disposal prevent a requested continuation. Disposing at the after-event boundary does not append the proposed input or start another model call.

By default, turn and token limits and final result accounting are cumulative across hook-resumed cycles. Reaching a cap stops before appending another prompt; finishing an already accepted interrupted tool batch remains possible. `InvocationOptions.ResetLimitsOnHookResume = true` instead starts a new limit/accounting boundary for each explicit hook resume. In that mode final `Usage`, `Turns`, and `Message` describe the final cycle, including zero usage and no message if that cycle is cancelled before a response. This follows the pinned TypeScript `_streamInvocation` loop and `_stream` invocation-meter reset; the cumulative default is an explicit native policy. Automatic background delivery does not reset these limits in either mode. A structured invocation must obtain a newly accepted validated result from its final cycle; a prior cycle's cached result cannot complete it.

## Harness and portable configuration

The harness forces accepted initial tools named `subagent`, including a caller replacement, into Always, removing an exact assignment from Agentic or Never. Wildcards remain intact. Explicit core background settings retain precedence. Standard delegation registers after plugin tools and before background management and `InitializedEvent`; a denied registration is never inspected or forced. Each child receives its own scheduler and task records; its existing tool filter still narrows generated management tools and ordinary tool authority.

The pinned top-level portable harness schema has no `backgroundTasks` field. Native binding supports the existing `agentConfig.backgroundTasks` passthrough:

```json
{
  "agentConfig": {
    "backgroundTasks": {
      "agentic": ["research"],
      "maxConcurrency": 4,
      "timeout": 120000,
      "waitForCompletion": false
    }
  }
}
```

Use false or null to disable that native binding, or true for defaults. Nested fields are strict and validated before model/component resolution. JSON timeout is a positive integer in milliseconds, following TypeScript; the typed API uses `TimeSpan`. Python's direct configuration uses seconds. This native session representation is not a cross-language snapshot format.

## Source contracts and remaining boundaries

The reference is pinned commit `3bd98e618320f0f286acbfb2252f72388030421b`:

- [TypeScript configuration and task shape](../../strands-ts/src/background-tasks/types.ts), [scheduler](../../strands-ts/src/background-tasks/in-process/engine.ts), [manager](../../strands-ts/src/background-tasks/in-process/manager.ts), [routing, schema, state and delivery](../../strands-ts/src/background-tasks/background-tasks.ts).
- [Python public configuration export](../../strands-py/src/strands/background_tasks/__init__.py), [persistent runtime](../../strands-py/src/strands/background_tasks/_runtime.py), [scheduler](../../strands-py/src/strands/background_tasks/in_process/_engine.py), [manager](../../strands-py/src/strands/background_tasks/in_process/_manager.py), [agent composition](../../strands-py/src/strands/background_tasks/_background_tasks.py).
- [TypeScript harness resolution](../../harness-ts/src/agent.ts), [Python harness resolution](../../harness-py/src/strands_harness/agent.py).

Python rejects any registered tool that declares the reserved selection parameter; TypeScript skips some untransformable wildcard specifications. Native validation follows the stricter Python guard. Native timeouts are bounded by the portable timer range, while Python permits longer finite durations. Shutdown cancellation/join is an explicit native ownership rule.

This slice does not establish full SDK or harness parity. Typed agent-tool delegation and nested human input are documented in [AGENT_TOOLS.md](AGENT_TOOLS.md), and telemetry is documented by its own component. The native automatic continuation keeps cumulative turn/token limits even when explicit hook resumes opt into TypeScript-style counter resets. Internal transaction callbacks are SDK coordination mechanisms, rather than a new public continuation API. External job persistence and arbitrary host process management are not supplied by this component.
