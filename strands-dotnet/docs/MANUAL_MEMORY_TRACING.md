# Manual memory tracing

`Tracer` exposes `StartMemorySearchSpan`/`EndMemorySearchSpan`, `StartMemoryAddSpan`/`EndMemoryAddSpan`, `StartMemoryInjectSpan`/`EndMemoryInjectSpan`, and `StartMemoryExtractSpan`/`EndMemoryExtractSpan`. These methods observe application work; they do not access a memory backend or configure an exporter. They return the existing disposable `TraceSpan` handle even when no activity is sampled.

```csharp
var tracer = new Tracer();
using var span = tracer.StartMemorySearchSpan(new("query", ["facts"]));
// Perform the application's search, collecting results and isolated failures.
tracer.EndMemorySearchSpan(span, new()
{
    Entries = entries,
    StoreFailureCount = isolatedFailureCount
});
```

The independent option records keep operation inputs discoverable, and the span retains its selected source profile internally. Completion reuses the same implementation as automatic memory telemetry, without a global span registry or another public wrapper. This applies the readability check in Scott Hanselman's [discussion of discoverable APIs and unnecessary indirection](https://www.hanselman.com/blog/im-just-a-caveman-the-hanselman-corollary-to-the-clarkewheeler-laws); it is our design choice rather than an endorsement of this SDK.

## Source profiles

The default `TypeScript` profile inherits ambient parenting. `Python` also supports an explicit recording `ParentSpan` for search, add, and injection; an ended or unrecorded explicit parent falls back to ambient context. Add can force a root, overriding either parent. Extraction always starts a root, optionally linked to valid `AgentSpanContext` IDs. Both use per-operation W3C IDs without changing process settings, and starting a manual helper does not make its span ambient. Applications can use the existing handle's context helpers when needed.

Python manual calls support `CustomTraceAttributes` followed by scalar `ExtraAttributes`, applied after operation attributes in that order. Extraction supports extras, matching its Python signature, but has no named custom-attribute field. Unsupported extra values are ignored. These caller fields can override operation values, including a supplied start-time attribute. TypeScript options reject Python-only parent/custom/extra inputs before starting an activity. The TypeScript provider field uses the configured service name at start; Python defaults to `strands-agents`. Tracer constructor attributes are excluded from these manual memory helpers, as in the pinned sources.

Search result JSON uses TypeScript `storeName`, omitting unavailable store/metadata fields, or Python `store_name` with explicit nulls. The profile is retained by the returned handle, so a completion call cannot accidentally select the other JSON format. Store names, per-call dictionaries, and supported attribute arrays are copied before activity callbacks run.

Query, fact, and result payloads remain raw memory content, independent of the model-content allowlist. They use the `content` field in `memory.query`, `memory.content`, and `memory.results` events. Attributes-only and Langfuse placement instead use a span attribute; successful search results replace the earlier query value in that attribute. Injection and extraction add no raw transcript events.

## Completion and ownership

Search completion defaults to empty entries and zero failed stores. Isolated store failures alone remain successful; an explicit `Error` marks failure and suppresses the results payload. Add defaults to zero failed stores. Injection records its required renderer outcome, defaults its entry count to zero, and remains successful when `FormatError` is true. Extraction omits an unknown entry count and records a supplied zero as known zero.

Null completion handles are no-ops. Completion is idempotent; it does not inspect another result collection after the handle has ended. Propagation-only activities keep their identity and are physically stopped without recording result data or fabricating success status. Listener and payload-observation failures cannot escape the helper or leak ambient context. The application owns borrowed parent handles and its backend work. Invalid start arguments and unsupported profile options fail before activity creation.

Automatic `MemoryManager` instrumentation retains its existing policy: its explicit native trace attributes cannot replace canonical operation values, and its Python backend activation remains separate from these manual helpers. The explicit manual Python per-call override behavior does not change that automatic configuration contract.

## Scope

The pinned baseline is `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/telemetry/tracer.ts:664–862` and `src/telemetry/types.ts:170–260`; Python `src/strands/telemetry/tracer.py:962–1270`. The initial suite has 26 contracts: seven observed runtime RED-to-GREEN scenarios and 19 first-green conformance cases. Specialized `ModelExtractor` chat activities remain a separate increment. Local `net48` tests use installed Framework 4.8.1 and do not establish clean Framework 4.8 or Linux verification for this increment.

The exact component gate over private source `a2c6ecde19ac41719bd1536ef905b8c40b7698a2` passed 614 executions across six TRX files: 254 Memory/Telemetry regressions on each modern framework, the 26 manual contracts on each portable modern framework, and 27 tests on each local Framework architecture including the pointer assertion. All 156 executions of the new contracts passed. The three-target Core NuGet package passed API compatibility validation with zero build warnings. Positive formatter probes selected all six modified production files and the test file; the final clean verification changed none.
