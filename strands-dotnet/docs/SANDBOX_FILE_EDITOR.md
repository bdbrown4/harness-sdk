# Combined sandbox file editor

`SandboxFileEditor.Create(selected, options)` borrows a sandbox;
`Create()` and `CreateForAgent(options)` resolve the invocation Agent's sandbox.
The default name is `file_editor` (TypeScript's standalone spelling can be
selected with `Name`). This is the combined `view`, `create`, `str_replace` and
`insert` protocol, rather than aliases for Harness read/write/edit tools.

Files are read as strict UTF-8 with a fixed 1 MiB limit. Views retain source line
numbering, tab expansion, range semantics and snippets. Literal replacements
must have one nonoverlapping occurrence; omitted/null replacement deletes it.
Directory views retain duplicates, sort names ordinally, exclude hidden items
and recurse to the pinned two-level depth. Complete supplied directory batches
are detached and validated before recursion. Successful accepted writes finish
without being relabeled as uncommitted by later cancellation; causal backend
failures and independent cleanup errors remain observable.

The default `SkipIoFailures` directory policy follows Python's I/O/permission
walk handling. `SkipNonCancellationFailures` selects TypeScript's broader walk
handling, while retaining native cancellation, malformed snapshots and
cleanup-bearing failures. Missing-path classification is limited to genuine
typed missing errors. `view_range` accepts absent/null/empty-array or two
integers, matching Python's optional profile; TypeScript's stricter tuple
acceptance differs. Nonempty malformed values fail schema validation when the
tool is registered with an Agent.

Paths are validated without host resolution. Native portable syntax accepts
POSIX, fully qualified drive and UNC paths, rejects raw traversal/NUL, and
preserves selected-backend authority. Python's path profile is POSIX-only and
TypeScript follows the Node host. Source trailing separators are removed, so a
bare root is rejected. .NET ordinal UTF-16 sorting and strict UTF-8/error mapping
are explicit native choices; diagnostics are not promised byte-identical across
languages. The fixed source read limit is not an invented create limit. No
unused TypeScript reader registry/maxFileSize API is exposed.

Deterministic fake tests cover registered schemas, child routing, whole-list
detachment, requested and independent cancellation, physical write joins,
literal multiline replacement/snippet boundaries, and UTF-8 limits. They perform
no real host file, process, interpreter, Docker or SSH operation. Existing Core
filesystem metadata/deletion differences and actual remote integration remain
separate capabilities.
