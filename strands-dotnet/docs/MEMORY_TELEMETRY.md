# Automatic memory search and add tracing

`MemoryManager.SearchAsync` and `AddAsync` emit `memory.search` and `memory.add` activities through the existing `Strands.Agents` activity source. This does not configure an exporter. Calls made through the manager's tools use the same instrumentation. Direct calls to a backend bypass the manager.

Both operations record `gen_ai.operation.name`, the selected convention's provider field, JSON `memory.store.names`, and `memory.store.count`. The names describe the requested selection, including duplicates; backend routing still removes duplicates. Search records an explicit `memory.max_search_results` only when the caller supplied it. Completion records `memory.store.failure_count`, and search also records `memory.result.count`.

Individual search failures are isolated. Even when all backends fail, the returned result is an empty collection and the activity ends successfully with the failed-store count. Unknown store selections are traced errors before backend I/O. Writes attempt every selected backend; an aggregate write error retains the original causes and counts failed stores rather than flattened exceptions. Caller cancellation ends an admitted activity as an error and preserves the caller token. A mismatched backend cancellation token is normalized only after the caller has requested cancellation; the original exception remains the inner cause. Existing pre-admission argument validation and pre-canceled calls do not start an activity.

## Profiles and payloads

`MemoryManagerOptions.Telemetry.Profile` defaults to `MemoryTelemetryProfile.TypeScript`. It uses `storeName` in result JSON and leaves backend activity context unchanged. `Python` uses `store_name`, includes `metadata: null` when absent, and activates the memory activity around backend awaits. Both profiles preserve attributed provenance supplied by the manager. TypeScript omits an absent metadata field.

The query, written fact, and result content are emitted verbatim in `memory.query`, `memory.content`, and `memory.results` events under the `content` key. This follows both pinned upstream memory tracers: the model-content redaction allowlist does not redact memory payloads. The existing attributes-only or Langfuse convention places the same raw value in a span attribute named `content`. For successful searches, the result payload replaces the earlier query in that attribute, matching upstream behavior.

Explicit `Telemetry.TraceAttributes` are a native configuration extension for either profile. The dictionary and array values are copied at manager construction; canonical operation attributes remain authoritative. Agent-level attributes are not inherited implicitly. This differs from Python's manual helper, which accepts per-call custom attributes that can override initial operation attributes. TypeScript's automatic helper has no corresponding custom-attribute input.

Detached `add_memory` writes start root activities, avoiding a parent-child relationship to an ended scheduling operation. Python also activates that root around the backend; TypeScript leaves the inherited backend activity context unchanged. Accepted work retains the existing `FlushAsync` drain semantics. Detached write failure ends its activity as an error while the already accepted tool result and drain remain successful.

Telemetry listener or payload-observation failures do not change application results. A failing diagnostic sink also cannot turn an isolated backend search failure into an application failure. Each operation keeps its own span handle for concurrent calls.

## Scope and evidence

The baseline is `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/telemetry/tracer.ts:664` and `src/memory/memory-manager.ts:451`; Python `src/strands/telemetry/tracer.py:997` and `src/strands/memory/memory_manager.py:296`.

The focused suite contains 30 contracts. Eight cases were observed failing before their implementation or correction: search span creation, add span creation, Python backend context, diagnostic-sink failure isolation, and all four read/write/profile combinations of a backend cancellation with the wrong token. The other 22 cases passed their first run. Modern and portable runs include success, partial/all search failures, unknown selections, aggregate writes, exact cancellation tokens, detached roots, profile payloads, attributes-only/Langfuse placement, custom-attribute detachment, and listener failures.

Public manual memory tracing helpers, automatic `memory.inject`/`memory.extract`, graph/swarm spans, exporter setup, and live-service instrumentation remain separate work. Local `net48` execution uses the installed .NET Framework 4.8.1 runtime; it does not establish a clean Framework 4.8 cloud result for this increment.

The final local gate passed 520 executions across six TRX files: 199 memory/telemetry regressions on each modern target, 30 focused contracts on each portable modern target, and 31 on each actual Framework x64/x86 host including the pointer assertion. This represents 180 executions of the 30 new contracts. No failures, skips, or build warnings were observed. Formatting positively covered all six source files and the test file, then verified no changes. Both independent read-only reviews passed after the cancellation-token correction.
