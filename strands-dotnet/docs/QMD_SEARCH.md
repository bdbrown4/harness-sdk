# QMD-style lexical file search

`Strands.Agents.Sqlite` supplies a separate `QmdSearchStrategy` for host
`LocalFileStorage`. Construction is lazy. Supply it as the local store's search
strategy, or call its `SearchAsync`/`UpdateAsync` with a direct local store.
Storage stays borrowed; the strategy owns its SQLite connection. This does not
change default keyword search or the separate Python-style `Bm25SearchStrategy`.

```csharp
await using var search = new QmdSearchStrategy();
var storage = new LocalFileStorage("./memory", search);
var rows = await storage.Namespace("notes").SearchAsync("authentication flow");
await search.UpdateAsync(storage);
```

Every search refreshes the selected physical directory before token filtering,
including empty or stopword-only queries. `UpdateAsync` refreshes without a
query. New and changed nonblank files are indexed; missing files are removed
from active full-text search. A file changed to whitespace retains its prior
indexed content, matching the characterized compatible engine. Unreadable files
retain their previous rows when present. Keys retain their original case and
path spelling. Hidden path segments and `node_modules`, `vendor`, `dist`, and
`build` directories are excluded. The scanner rejects symbolic links/reparse
points it encounters instead of following them. It uses host permissions and
provides no filesystem authorization boundary or race-free link traversal.

The native lexical index has filepath, extracted title and UTF-8 body columns,
SQLite FTS5 `porter unicode61`, weights 1.5/4/1, ANDed prefix terms and CJK
character phrases. Markdown and Org titles follow the characterized extraction
rules; other titles fall back to the filename. At most twenty results are
returned with the compatible engine's score normalization followed by the
pinned SDK wrapper's second normalization. Results carry keys and scores, with
no byte payload; a memory store must hydrate its accepted selected storage.
Arbitrary equal-score tie order is unspecified, including ties across different
SQLite versions or independently created row IDs.

When configured on a local store, its composable namespace views select the
physical directory before refresh, ranking and the top-twenty limit. A narrow
internal capability supplies this routing; public storage interfaces and the
fallback behavior of keyword, incremental BM25 and custom search backends stay
unchanged. Direct calls to `QmdSearchStrategy.SearchAsync`/`UpdateAsync` require
the concrete local store. There is no sandbox fallback or remote filesystem
parser.

`DatabasePath = null` selects `.<root>-qmd.sqlite` alongside the selected root;
`:memory:` uses an in-memory index. An explicitly empty path fails on opening.
The native database has its own version marker and rejects existing unrelated
schemas. It is not a QMD database format. Switching selected roots closes the
previous connection. Calls serialize per strategy; `CloseAsync` joins preceding
admitted work and permits reuse, while `DisposeAsync` closes admission, joins
physical work and permanently releases the connection. Cancellation checks bound
the managed phases and preserve the original caller token. A synchronous native
SQLite/filesystem operation must return before cancellation or disposal can
finish; no close timeout or abandoned background cleanup is provided.

The scalar/token/casing tables deliberately select **Unicode 17.0.0**, including
default contextual final sigma and full dotted-I lowercase. They do not use the
runtime's changing Unicode regex or invariant casing tables. The private data's
normative hashes and regeneration instructions are in
`src/Strands.Agents.Sqlite/unicode-data/provenance.json`; generation is offline
and is not a build step. The Unicode License V3 is included in the package.
This reference profile is not a universal JavaScript or future QMD promise.

Source anchors are pinned Strands TypeScript commit
`3bd98e618320f0f286acbfb2252f72388030421b`,
`src/storage/search/qmd.ts` and `keyword.ts`. That SDK declares compatible QMD
`^2.8.0`; the characterized engine was 2.8.3, commit
`facd35e01359e59d938bc9418e93fb9318addee3`, with SQLite 3.53.4. Native validation
uses the existing Microsoft.Data.Sqlite 10.0.13 package and its SQLite build.
This implements the bounded lexical strategy, with no vector/hybrid/LLM engine,
service compatibility, official QMD database interoperability, or borrowed
resource ownership claim. Platform and package evidence must be reported
separately from source behavior.
