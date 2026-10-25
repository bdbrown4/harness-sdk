# Invocation telemetry scope

`AgentOptions.InvocationTelemetryScope` selects the boundary of an invocation span and a metrics-history entry when an after-invocation hook requests another pass.

The default, `PublicInvocation`, retains one boundary across the public call and its hook continuations. This preserves the existing native behavior and follows Python's public-run grouping. `AgentPass` starts a new span and metrics-history entry for each pass accepted by its before-invocation hook, and completes that span before after-invocation callbacks. This follows the TypeScript pass boundary. Before-hook denial does not start an accepted pass.

```csharp
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    InvocationTelemetryScope = InvocationTelemetryScope.AgentPass,
});
```

This option selects the accounting/span boundary. It does not select every other language's telemetry policy, change cancellation results, reset model-turn/token limits, or alter committed history. Explicit `ResetLimitsOnHookResume` remains independent. Lifetime usage stays cumulative; history retention, latest-invocation token selection and context-size policies retain their existing options. Per-pass local traces describe the latest accepted pass, while cumulative metrics retain earlier completed work.

After-hook failures occur after an `AgentPass` span has completed and cannot rewrite its status. Provider and iterator failures within a pass still complete that span with the original error. Closing a public stream at its after-invocation event never starts a hidden continuation. Cleanup closes the current span and releases invocation ownership on every exit.

Built-in memory triggers capture an immutable recording-context receipt before an accepted pass ends. That receipt remains available through the after-invocation enqueue boundary, allowing background extraction to link to the completed pass. Every attempted pass first clears the receipt; an unsampled or denied resumed pass cannot reuse an earlier sampled link. Outer cleanup clears it again. Custom memory triggers retain their actual ambient context, and explicit flush remains unlinked. This is an explicit native causality policy, rather than a claim that TypeScript keeps a completed span ambient during after-hooks.

The source references are repository commit `3bd98e618320f0f286acbfb2252f72388030421b`: `strands-ts/src/agent/agent.ts` (public resume loop, per-pass span/meter start, span completion before after-hooks) and `strands-py/src/strands/agent/agent.py` (one public invocation wrapping `_run_loop`). The default retains the native span's existing initialization boundary; this option does not change provider initialization or other lifecycle-event ordering.

Twenty focused contracts include three observed runtime failures before implementation. They cover both scopes, hook resumes, denial, cumulative/reset limits, callback/provider errors, cancellation, public-boundary disposal and sampled-to-unsampled resumed memory links in both memory profiles. Exact-source component gates, package/API checks and platform classification are recorded separately. This increment does not establish complete telemetry, SDK or harness parity.
