# JSON-record memory store

`Strands.Agents.Memory.TestMemoryStore` is a small lexical memory backend. Each add reads and rewrites one JSON file. It preserves record IDs and metadata and deduplicates facts by trimmed content. This is a separate capability from `FileMemoryStore`, which groups facts into topic markdown files.

```csharp
var store = new TestMemoryStore(new()
{
    Name = "notes",
    Path = "./memory/notes.json"
});
var added = await store.AddAsync("User prefers dark mode");
var recalled = await store.SearchAsync("dark mode");
var manager = new MemoryManager(new() { Stores = [store] });
```

The constructor performs no backend I/O. Stores are writable and persistent by default. Without an explicit path, the file is `<user-profile>/.strands/memory/<sanitized-name>.json`. Sanitization replaces `..`, path separators and non-ASCII filename characters with underscores, matching both pinned stores. Tests use owned task paths or ephemeral stores and never write the real user profile.

`Persist = false` selects a private in-memory byte backend; a new store instance starts empty. An explicit path is still checked for a blank value, then ignored for storage selection. The store does not accept public `IStorage` injection or inherit agent storage. Set `Writable = false` to reject adds. Explicit extraction settings integrate with the existing memory manager and read-only views.

Records use the shared TypeScript/Python camelCase array format: `id`, `content`, optional `metadata`, and `createdAt`. New timestamps use UTC millisecond precision and a `Z` suffix. IDs generated natively are GUID v4, matching Python; TypeScript generates UUID v7. Imported IDs remain opaque strings. New records retain the original content string; trimming only determines equality. A duplicate returns the first matching record's original ID without changing its content, timestamp or metadata, and performs no write. Unknown existing record fields survive later appends.

Every operation reads fresh. Missing files start empty; malformed JSON, invalid record fields, and non-object/non-null metadata are rejected without replacing the file. Imported `createdAt` values must be strings but are not reparsed or normalized. UTF-8 files may include a leading byte-order mark. Metadata is detached before asynchronous work and search results contain a detached copy with `_relevanceScore` set to the number of distinct query tokens present in the content.

Search matches content only, then sorts by decreasing overlap and decreasing timestamp, retaining record order for ties. An explicit caller limit overrides the store limit; direct searches otherwise default to ten. The memory manager retains its own existing default limit. Tokenless queries do not read the backend. Tokenization uses Unicode letters, numbers and underscore, including supplementary codepoints. Native casing, whitespace trimming and Unicode data follow the installed .NET runtime. Timestamp ordering is invariant ordinal: standard UTC timestamps sort as intended; arbitrary non-ISO strings need not have the same ordering as TypeScript's locale-sensitive `localeCompare`. Python uses lexicographic comparison. These are explicit runtime differences, not byte-for-byte language equivalence.

Adds serialize the entire read-modify-write operation within one native store instance. Separate instances and processes have no shared lease or transaction and may overwrite one another's changes. The private file backend supplies its existing atomic replacement behavior. A failed write remains observable and does not wedge subsequent adds. There is no new backend ownership or disposal contract.

Cancellation is a native `CancellationToken` extension. Operations join physical backend work before returning cancellation, preserve matching cancellation exceptions and real cleanup-bearing failures, and retain independent failures as cancellation causes when the caller actually requested cancellation. A cancellation observed after a successful physical write can coexist with committed data; it does not mean “nothing was saved.” Retrying the same fact finds its persisted ID through deduplication. Metadata is not retained through a caller-owned `JsonDocument` lifetime.

The implementation follows pinned commit `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `vended-memory-stores/test-memory-store/store.ts:117–319`, Python `vended_memory_stores/test_memory_store/store.py:97–302`, and their dedicated tests. Genuine scaffold behavior failures and later implementation regressions are retained separately in component evidence. This scope adds no S3, knowledge-base, embedding, QMD/BM25, cross-process transaction or full SDK parity claim.
