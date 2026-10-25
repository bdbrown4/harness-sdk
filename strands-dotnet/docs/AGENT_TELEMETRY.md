# Automatic agent telemetry

Ordinary `Agent.InvokeAsync` and `Agent.StreamAsync` calls collect local metrics and traces and emit native .NET activities. Applications configure exporters separately through their existing tracing provider or the optional `Strands.Agents.OpenTelemetry` package. Constructing an agent does not install a global provider or exporter.

```csharp
await using var agent = new Agent(new AgentOptions
{
    Model = model,
    TraceAttributes = new Dictionary<string, object?> { ["application.component"] = "assistant" }
});

AgentResult result = await agent.InvokeAsync("Explain this document.");
AgentMetrics statistics = result.Metrics!;
IReadOnlyList<AgentTrace> cycles = result.Traces;
```

`agent.Metrics` contains lifetime totals and, by default, the latest fifty invocation histories. `AgentOptions.MaximumInvocationHistory = null` selects Python's unbounded retention; a positive value selects a fixed history bound. Eviction never resets lifetime totals. `MetricsContextSizePolicy = ContextSizePolicy.LatestInvocationCycle` selects Python's latest-cycle context accounting; the default `LastReportedUsage` retains TypeScript's most recent reported estimate across boundaries. `result.Metrics` is a detached snapshot taken when that result is assembled. Later calls and late background completions do not mutate an earlier snapshot. `result.Traces` contains the current outer invocation's local cycles; it does not contain the agent span itself. Collection getters are detached and read-only. Individual `AgentTrace` objects retain their existing explicitly mutable API.

Invocation activities report lifetime accumulated model usage by default, matching both pinned SDKs. Include `gen_ai_use_latest_invocation_tokens` in `OTEL_SEMCONV_STABILITY_OPT_IN` to select Python's latest-invocation token reporting. This setting is captured with the other conventions at tracer construction; changing the environment afterward does not change an existing agent. History eviction does not change lifetime accounting. This choice affects invocation activity attributes, while each result retains its own invocation usage and its detached lifetime metrics snapshot.

The invocation activity inherits the caller's ambient context. Each foreground cycle parents its provider and tool operations explicitly. Actual provider calls run with their model activity as ambient, including asynchronous stream advancement and disposal. Tool bodies run with their tool activity as ambient. Each boundary restores its caller's context and preserves the original application failure.

Provider activities are created inside the terminal middleware callback. A cached middleware response emits no provider activity. Routing uses the effective model identity and input, and explicit middleware retries emit one activity per actual attempt. Provider activities retain the provider's original output and accounting; local cycle metrics use the accepted middleware result. Reported timing passes through the provider timing contract without estimating missing latency or time to first token. [Usage presence](MODEL_USAGE_PRESENCE.md) distinguishes an absent measurement from an explicitly reported zero through metadata, middleware and after hooks. Replacing middleware usage explicitly reports the supplied counters; message-only transformations preserve the existing presence flag.

Tool statistics describe actual terminal attempts before after-hooks or output middleware transform their results. Hooks and middleware can explicitly retry an operation; each executed attempt is counted. Cache hits, denied calls and reused completed bodies do not fabricate executions. Unknown tools and invalid schemas count as unsuccessful terminal attempts. An interrupted body counts as an unsuccessful attempt without fabricating a tool result or exception event. The default TypeScript-compatible status policy marks a span as an error for an explicit execution exception. `AgentOptions.UsePythonToolErrorStatus = true` additionally selects Python's error-result status policy.

Background bodies share the agent's thread-safe meter and use independent tracers. They can finish after a foreground result or during another invocation without attaching to a mutable foreground cycle cursor. A completed result retains its earlier statistics; `agent.Metrics` exposes subsequent completions. Reused background bodies are not counted again. Background tool spans do not appear among the foreground cycle's local children.

Custom attributes and their array values are copied at agent construction. Default children receive independent copies. Foreground and background activities use those attributes consistently. Invocation content records newly supplied input and the configured instructions; provider content records the effective middleware input. The content and redaction policies in [native telemetry](TELEMETRY.md) apply to both.

Cleanup closes model, tool, cycle and invocation activities on success, failure, cancellation and early stream disposal. An early disposal failure retains its original identity and marks the invocation as an error. A continuation refused at a turn limit records the limit as the invocation's terminal reason. Direct `ValueTask` awaiting preserves cancellation identity and tokens on the `netstandard2.0` asset used by Framework applications; converting an `IValueTaskSource` cancellation through `AsTask()` would lose that information on Framework.

## Scope and remaining work

This increment integrates ordinary foreground calls and background tool bodies. Standalone tracing, metrics, content conventions, provider timing and optional exporters retain their separate contract evidence. The later metrics-profile increment exposes both history-retention and context-selection alternatives. Python's `reset_usage_metrics()` starts a fresh invocation history entry while retaining lifetime totals; it does not reset accumulated usage. Complete telemetry parity still requires specialized memory and multi-agent spans, harness-level configuration and the distinct TypeScript hook-resume telemetry boundary. Current macOS and clean 4.8-only validation are separate release gates.

The model and tool boundaries follow pinned `strands-ts/src/agent/agent.ts` and `strands-ts/src/tools/executors/executor.ts`; Python's result-status behavior follows `strands-py/src/strands/telemetry/tracer.py`. Runtime red-to-green evidence is recorded separately from first-pass conformance tests.
