# Default memory extraction model

When the harness creates its default file-memory store, extraction selects the same content model policy as `web_fetch`: an explicit `WebFetch.Model`, then a configured `web_fetch.model` identifier through the existing model factory/resolver, then the ordinary auxiliary model. The auxiliary fallback keeps its current core/options override and provider-family behavior.

This matches the pinned TypeScript `harness-ts/src/memory.ts:79–92` and Python `harness-py/src/strands_harness/memory.py:92–108` default-store builders at commit `3bd98e618320f0f286acbfb2252f72388030421b`. Their memory resolver passes the web-fetch model override into `resolveWebFetchModel` / `resolve_web_fetch_model`.

The native harness resolves this content model at most once during one `Harness.CreateAsync` call when default extraction or the selected `web_fetch` tool needs it. This small construction-time memo avoids acquiring duplicate native transports for those consumers. It does not create a process-wide cache or change ownership: explicitly supplied models and application factory/resolver results remain borrowed; existing native-owned auxiliary cleanup remains unchanged.

Explicit memory managers, manager plugins and supplied stores keep their own extraction configuration. Disabled memory does not resolve an extraction model. An inherited read-only child retains recall while its extraction and write capabilities remain disabled; it does not construct a new memory extractor. The web-fetch tool may still need a model independently when memory is disabled.

The focused tests perform real native agent extraction and a local HTTP web-fetch request. They cover selection, factory/resolver precedence, fallback, bypasses, child recall and borrowed lifetime. This scope adds no new backend, cloud request, public API or full memory-catalog parity claim. TestMemoryStore, S3, knowledge-base ingestion and QMD/SQLite BM25 remain separate catalog work.
