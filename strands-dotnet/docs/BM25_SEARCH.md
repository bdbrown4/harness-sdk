# SQLite FTS5 BM25 search

`Strands.Agents.Sqlite` supplies the Python-style incremental `Bm25SearchStrategy` through the existing byte-storage search API. It is an optional package, with `Microsoft.Data.Sqlite` 10.0.13 and its default SQLitePCLRaw native bundle. Core and Harness do not acquire a SQLite dependency.

```csharp
await using var strategy = new Bm25SearchStrategy();
var storage = new LocalFileStorage("./app-data", strategy);
var memory = new FileMemoryStore(new()
{
    Name = "facts",
    Storage = storage,
    UseStorageSearch = true
});
await memory.AddAsync("# Preferences\nThe user prefers concise answers");
var entries = await memory.SearchAsync("concise answer");
```

The host owns the strategy and must finish its consumers before disposing it. The strategy owns only its connection; it never disposes storage. LocalFileStorage calls `IndexAsync` after its byte write commits. Index failure is visible, while the bytes can already exist. There is no combined file/database transaction, distributed coordination, or crash replay guarantee.

## Selection and snapshots

`FileMemoryStoreOptions.UseStorageSearch` defaults to false. The default keyword scan and existing explicit `Search` override retain their behavior. An explicit override wins even when the flag is true and still hydrates backing bytes as before. The opt-in branch delegates through the store's namespaced backend search, snapshots the entire result membership and optional Data buffers before its first hydration await, then uses Data when present and reads only absent Data.

Namespace filtering and relative-key projection precede the result limit. Missing backing bytes can yield fewer than the requested count because limiting precedes hydration. Namespaces are logical key views; backend search/ranking may examine the shared root before filtering, and corpus statistics can span namespaces. This is not access isolation.

## Index and database behavior

Only entries passed through IndexAsync are indexed. Existing files are not backfilled, and deleting a file does not delete its FTS row. Direct strategy search may return stale keys; FileMemoryStore skips missing backing bytes. A final basename beginning with a dot is excluded; an ordinary file inside a dot directory remains eligible.

The default database is `.__strands_<root-name>-fts5.sqlite` beside the storage root. An explicit DatabasePath selects one caller-controlled keyspace, including when the same strategy switches roots; it does not partition a fixed database by root. Empty DatabasePath selects a temporary on-disk database and `:memory:` selects a private in-memory database. Both disappear on close. Ordinary paths are made absolute and passed as one Data Source value through the connection-string builder. They are filenames, not connection strings or URI configuration. Filename validity follows the platform.

The index stores key and UTF-8 content, with replacement decoding for invalid bytes. A raw-byte MD5 fingerprint detects unchanged content and has no security role. Replacements update FTS and hash rows in one SQLite transaction. Empty content can still match its indexed key. Queries use distinct lowercase native word tokens, the pinned Python stop-word set, sorted prefix terms joined with implicit AND. Key and content are searched; results retain SQLite rank ordering and score `abs(rank)/(1+abs(rank))`, with no Data payload. Unicode token and ordering boundaries follow the existing native tokenizer, rather than a promise of identical Python regex/Unicode-version behavior.

This strategy implements the pinned Python FTS5 family. The separate native [`QmdSearchStrategy`](QMD_SEARCH.md) supplies the TypeScript-style filesystem refresh before search and explicit `UpdateAsync`; it does not change this strategy's indexing contract.

## Cancellation and lifetime

SQL executes synchronously in the provider. The strategy serializes offloaded synchronous actions per instance. Its five-second command timeout follows the Python source; it is not a hard bound on every possible OS operation.

- Cancellation before admission or while waiting for the serial SQL slot prevents that operation's database work and returns the original caller token.
- After the synchronous SQL action starts, its physical completion is always joined. An independent SQL or cleanup failure remains primary even if the caller subsequently requests cancellation.
- Successful SQL followed by a requested caller token returns cancellation with that token. A write may already have committed; cancellation does not roll it back.
- CloseAsync uses cancellation only for admission. Once admitted, it waits for the serial slot and closes physically; later cancellation does not detach the wait or replace a cleanup failure. Close is reusable.
- DisposeAsync closes admission immediately, joins all admitted physical operations, and closes the owned connection. It is terminal and idempotent. Borrowed storage Read/List/Delete remain usable. A later storage write through the disposed attached strategy can fail after its byte commit.

No application callbacks are invoked by the SQL action. The internal test phase task observes accepted synchronous action entry after the last pre-action token check; it does not assert that a particular SQLite opcode has executed.

## Runtime proof boundaries

The dedicated test project disables xUnit assembly shadow copying only. Its default collection parallel behavior is retained. With the pinned default SQLitePCLRaw Framework dynamic loader, ordinary NuGet-only Framework executables load the architecture-matching native asset, while default xUnit shadow copying fails to find that asset despite both official output RID files being present. Those failed attempts are retained. Other shadow-copy hosting arrangements are not universally supported by this proof; no production resolver, provider reset, search-path mutation, or manual native-copy workaround is added.

Recorded component evidence must identify the actual runtime, native module hash/version, pointer, and tested architecture. Installed Framework 4.8.1 results do not establish clean 4.8 execution. Package RID presence does not establish execution on every OS. There is no AWS service, QMD, embedding, Agent/session, or new Harness configuration claim.
