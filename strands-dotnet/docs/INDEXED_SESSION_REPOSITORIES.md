# Indexed session repositories

`Strands.Agents.SessionRepositories` implements the pinned Python indexed repository family independently of native whole-conversation snapshots. `ISessionRepository` has nine session/agent/message operations; `IMultiAgentSessionRepository` adds three operations for detached JSON objects keyed by explicit multi-agent IDs. Storing these objects does not attach a live Graph, Swarm, or Agent.

```csharp
var repository = new FileSessionRepository("./application-session-records");
await repository.CreateSessionAsync(new SessionRecord("session"));
await repository.CreateAgentAsync("session", new SessionAgentRecord("agent",
    JsonSerializer.SerializeToElement(new { owner = "application" }),
    JsonSerializer.SerializeToElement(new { removed_message_count = 0 })));
await repository.CreateMessageAsync("session", "agent",
    new SessionMessageRecord(Message.User("original"), messageId: 10));
var rows = await repository.ListMessagesAsync("session", "agent", limit: 20, offset: 0);
```

`StorageSessionRepository` borrows any `IStorage`. `FileSessionRepository` delegates to the existing `LocalFileStorage` atomic file adapter under an explicit directory. The repository has no disposable host resources and never disposes borrowed storage. Session and agent identities are opaque, hashed independently beneath the selected storage prefix; they cannot become paths. Signed `Int64` message IDs remain numeric identities, including sparse values. Listing sorts them numerically, applies a row offset and optional limit once, and reads only selected payloads. A missing message collection fails; a created empty agent lists no rows.

On Windows, the file adapter selects the extended absolute path form before storing nested records, including on Framework hosts with legacy path handling. It does not change host switches or shorten record hashes. `RootPath` exposes that actual storage root and may contain the Windows extended-path prefix. The supplied base directory must be resolvable by the host's `Path.GetFullPath` implementation.

Create-session rejects an existing session namespace. Create-agent and create-message are upserts, including when parent metadata has not been created. Update-agent/message rejects a missing row, keeps its original `CreatedAt`, and uses the replacement's supplied `UpdatedAt`. Creating again replaces timestamps too. An indexed message retains both `Message` and optional `RedactMessage`; `ToMessage()` selects the latter. Native message JSON preserves rich/binary media, tool inputs/results, structured result data, metadata and tracking IDs. Runtime exception objects retain the existing native message JSON exclusion.

Records copy caller JSON and message collections. Reads return independent records. Persisted native envelopes have version one and bind record kind/session/owner/numeric identity to the requested key. Malformed envelopes, duplicate JSON properties and invalid payloads fail before an update can publish. Unknown additional record properties are ignored, as Python `from_dict` does. Native serialization retains the standard `System.Text.Json` depth limit for the complete envelope; this is not a Python byte/file format adapter.

The public immutable records also support ordinary native `JsonSerializer` roundtrips. Private exact-type JSON constructors preserve nonnullable timestamp properties while keeping the convenient optional timestamp parameters in their public constructors. Repository reads continue to use the stricter key-bound envelope decoder.

Operations serialize within one repository instance, and same-instance storage callback cycles are rejected before waiting. This does not provide transactions across records, compare-and-swap, or create-if-absent against another instance/process. Applications coordinate external writers. A generic backend must publish complete individual writes atomically if failed writes are to preserve previous records. `LocalFileStorage` does so through temporary files and atomic replacement. Cancellation before publication leaves the previous record intact; cancellation after an uncooperative backend has published can still report cancellation, without a rollback or exactly-once guarantee. Caller cancellation retains the exact caller token and a racing backend failure as its cause.

There is no default record byte cap, so valid large media is not rejected by an arbitrary repository limit. Applications can set a positive `SessionRepositoryOptions.MaxRecordBytes`; it bounds encoded writes and reads. Namespace prefixes are validated storage keys. Other explicit native validation differences are nonnegative row offset/limit, finite `Int64` identities, typed `DateTimeOffset` timestamps and valid UTF-16 IDs. Python accepts arbitrary integer IDs and Python slice behavior for negative pagination; its file manager rejects path separators while native hashes opaque IDs. Native records are immutable and do not mutate the caller's replacement `CreatedAt` when preserving the stored value.

This scope does not change `ISessionStore`, `SessionSnapshot`, `FileSessionStore`, snapshot managers, Agent session restoration, live repository-manager journaling or Agent hooks. Repository-backed incremental flush/redaction/alias integration, concrete session deletion and a specialized S3 repository remain separate work.

Sources at `3bd98e618320f0f286acbfb2252f72388030421b`: Python `session/session_repository.py:12–66`, `types/session.py:58–193`, `session/file_session_manager.py:159–321`; tests `test_file_session_manager.py:274–376` and `types/test_session.py:122`. The existing native whole-snapshot contract remains in `Sessions.cs` and `FileSessionStore.cs`.
