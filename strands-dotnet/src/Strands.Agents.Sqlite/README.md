# Strands.Agents.Sqlite

Optional Python-style incremental SQLite FTS5 BM25 search for host LocalFileStorage. Install this package separately from Core and supply a Bm25SearchStrategy to LocalFileStorage; select FileMemoryStoreOptions.UseStorageSearch when memory retrieval should use that backend.

The separate `QmdSearchStrategy` refreshes host files before lexical search or an explicit `UpdateAsync`, including selected local namespaces before ranking and the top-twenty limit. It uses its own versioned index and a declared Unicode 17 reference profile. See `docs/QMD_SEARCH.md` for source anchors, scoring, lifetime and compatibility limits; this is not QMD database or vector-engine interoperability.

The package uses Microsoft.Data.Sqlite 10.0.13 and its default native SQLitePCLRaw bundle. It maintains an incremental index, does not backfill existing files or remove deleted-file rows, and owns only its connection. CloseAsync permits reuse; DisposeAsync is terminal. Storage is borrowed. A byte write and its subsequent index update are separate commits.

Default native memory keyword retrieval and explicit Search overrides are unchanged. This is the pinned Python FTS5 capability, distinct from TypeScript QMD refresh/update semantics. See the repository's docs/BM25_SEARCH.md for query, namespace, cancellation/commit and runtime proof boundaries, including the Framework shadow-copy hosting limitation.
