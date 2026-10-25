# Automatic Graph and Swarm traces

`Graph` and `Swarm` emit native `System.Diagnostics.Activity` operations through the existing `Strands.Agents` activity source. Register that source with the application's tracing provider. This feature creates no exporter or telemetry provider and takes no ownership of nodes, agents, listeners, or application transports.

`GraphOptions.Telemetry` and `SwarmOptions.Telemetry` select an explicit source policy. The default `TypeScript` profile emits an internal `invoke_graph {id}` or `invoke_swarm {id}` operation and a separate internal `node {id}` operation for every visit. Agent calls, nested orchestrators, and function-node callbacks run under their actual visit context, including iterator advancement and disposal. Parallel visits retain separate parents. Revisited nodes receive new span identities.

The `Python` profile emits one client `invoke_graph` or `invoke_swarm` operation, activates it around execution, and parents agent calls directly under it. `GraphOptions.Python` and `SwarmOptions.Python` select this profile automatically. It follows the pinned Python call sites: they record input but do not invoke the aggregate completion helper, so this profile adds no automatic aggregate usage, execution-time, output, or explicit end-time attributes. Successful context completion keeps its unset status; escaping ordinary exceptions record an error. Requested caller cancellation and early consumer close do not invent an exception event.

```csharp
var graph = new Graph(new GraphOptions
{
    Nodes = nodes,
    Telemetry = new MultiAgentTelemetryOptions
    {
        Profile = MultiAgentTelemetryProfile.Python,
        TraceAttributes = new Dictionary<string, object?> { ["workflow"] = "review" }
    }
});
```

Attributes and supported array values are copied at construction. Application attributes override operation attributes as in the source SDKs. Inspecting the copied public options cannot modify the private tracing configuration.

The TypeScript policy exports its raw JSON input in `gen_ai.agent.input`; its source contract does not apply the model content-redaction policy to that attribute. The Python policy uses the existing semantic-convention, redaction, and attributes-only settings. Its media-byte encoder uses the source `<replaced>` placeholder. Typed reasoning, citations, cache points, media sources, and interrupt responses use the selected source SDK's serialized data shape. These profile choices affect observability and do not change the model's input.

TypeScript aggregate execution time is milliseconds since the retained execution state's `StartTime`, including a human pause. The public result's duration retains the native per-call behavior. Aggregate usage is summed once from the retained results when execution naturally completes. A hook-denied or hook-interrupted visit records zero execution time and omits token-usage attributes because its body did not run. Ordinary node spans close before `AfterNodeCall`; gated node spans remain open through that callback and its public event. TypeScript's orchestration span closes before `AfterMultiAgentInvocation`; Python's context encloses that callback.

Native cleanup also closes activities when initialization, callbacks, iterator disposal, or early consumer disposal interrupts a source lifecycle. It preserves the original execution exception and cleanup diagnostics. Listener or context-observer failures cannot change execution results. This deliberately repairs source leak paths rather than leaving spans open after exceptional exits. A failed independent graph node records its own span error even when graph failure policy permits other work to complete.

The source contract is pinned to commit `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/multiagent/{graph,swarm}.ts` and `src/telemetry/tracer.ts`; Python `src/strands/multiagent/{graph,swarm}.py` and `src/strands/telemetry/tracer.py`. Python context-completion behavior follows the native [`use_span` contract](https://github.com/open-telemetry/opentelemetry-python/blob/v1.30.0/opentelemetry-api/src/opentelemetry/trace/__init__.py#L516-L563).

Behavioral coverage lives in `MultiAgentTelemetryTests`, `GraphTelemetryTests`, and `SwarmTelemetryTests`, including real native activity listeners, parallel and nested execution, repeated visits, stored human resumes, cancellation, hook ordering, iterator cleanup, sampling, and failing listeners. This bounded increment covers traces; it makes no new automatic Graph/Swarm metrics, exporter, live backend, clean Framework 4.8, or full SDK-parity claim.
