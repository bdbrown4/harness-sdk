# Native agent metrics

`Strands.Agents.Telemetry.AgentMeter` collects local metrics independently of exporters. The collector returns detached, read-only `AgentMetrics` snapshots containing lifetime usage and latency, cycle counts and durations, recent invocations, and tool execution statistics. The later [automatic agent integration](AGENT_TELEMETRY.md) connects it to ordinary calls and attaches snapshots to `AgentResult`.

```csharp
var meter = new AgentMeter();
meter.StartNewInvocation();
using (var cycle = meter.StartCycle())
{
    cycle.Update(new Usage(10, 2),
        new ModelPerformanceMetrics(TimeSpan.FromMilliseconds(70)));
}
AgentMetrics snapshot = meter.Metrics;
```

Each cycle handle retains its originating invocation. Concurrent or out-of-order completion, a newer invocation, and history eviction cannot move usage into another invocation. Completion is idempotent. Elapsed durations use a monotonic clock; an explicit nonnegative `TimeSpan` can also be supplied to `End`. `RecordToolCall` records duration and success for one completed attempt and copies the request's JSON input and provider metadata. Per-tool snapshots retain the latest call and expose success rate and average duration.

The default history retains fifty invocations, matching the pinned TypeScript collector. `new AgentMeter(maximumInvocationHistory: null)` enables unbounded history. Eviction leaves lifetime usage, tool statistics and cycle totals intact. `UpdateCycle` is a convenience for the newest invocation and cycle; an explicit cycle's `Update` method provides stable ownership when operations overlap. Usage updates outside an invocation contribute to lifetime totals. Timing-only metadata leaves token context unknown.

Context-size accounting follows the shared explicit-total rule: when input plus output equals the provider total, input already includes cached tokens; otherwise cache-read and cache-write tokens are added. Projected context adds output tokens. The default `ContextSizePolicy.LastReportedUsage` retains TypeScript's latest reported update across invocation/cycle boundaries. The additive three-argument constructor accepts `ContextSizePolicy.LatestInvocationCycle` for Python's newest cycle in the latest invocation: a new invocation without cycles has no estimate, and a newly started cycle initially has zero usage. Both profiles retain cumulative lifetime totals. Accumulated latency sums request latency; time to first token remains a per-request measurement. All public durations use `TimeSpan`.

Agents expose the same choices through `MaximumInvocationHistory` and `MetricsContextSizePolicy`. Null history selects unbounded retention; nonpositive bounds and unknown context policies fail before construction callbacks run. Python's `reset_usage_metrics()` appends a new invocation entry and does not clear lifetime usage. These options preserve the original two-argument `AgentMeter` constructor and default behavior.

Native instruments use the `Strands.Agents` meter. The collector emits both pinned naming families: TypeScript `gen_ai.agent.*` counters and millisecond histograms, and Python `strands.*` counters, token histograms and second-based cycle/tool durations. Model latency is milliseconds in both families. Semantic `gen_ai.server.time_to_first_token` uses seconds and excludes zero; Python `strands.model.time_to_first_token` uses milliseconds and retains explicitly reported zero. Dashboards can select the desired family. Tool measurements preserve each family's tool-name tags, and Python includes the tool-use identifier. The native `Usage` type always represents cache fields, so Python cache histograms also record zero when no cache usage is supplied.

Applications can supply their own `System.Diagnostics.Metrics.Meter`, including one created through dependency injection, with `new AgentMeter(meter: applicationMeter)`. The collector borrows that meter and never disposes it. No global provider or exporter is installed. The existing DiagnosticSource dependency supplies the same instruments for the `netstandard2.0` asset and both modern assets.

Local state commits under a lock before emitting measurements. Publication and measurement callbacks run outside that lock; their failures cannot replace agent execution or invalidate local collection. Negative values and arithmetic overflow are rejected before any part of a local update is committed. Token counters and histograms retain native `Int64` precision. Repeated emissions reuse .NET instrument instances.

The implementation follows pinned `strands-ts/src/telemetry/meter.ts` and `strands-py/src/strands/telemetry/metrics.py`. Contract tests cover snapshots, history eviction, overlapping cycles, concurrency, accounting, units, real `MeterListener` callbacks, dependency injection and failing instrumentation. Later profile tests cover both context-selection policies and actual agent history retention. Clean Framework 4.8-only, current macOS and the latest combined source verification remain separate validation gates. Collector/exporter loopback evidence is distinct from live backend interoperability.
