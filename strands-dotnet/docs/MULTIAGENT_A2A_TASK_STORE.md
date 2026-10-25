# A2A task persistence

`IA2ATaskStore` provides `SaveAsync` and `LoadAsync` for detached A2A Task JSON. It follows the supplied-store capability in both pinned servers and the official [JavaScript TaskStore contract](https://raw.githubusercontent.com/a2aproject/a2a-js/v0.3.10/src/server/store.ts). A task record describes protocol progress; native agent messages, model continuation, and interrupt ledgers have a separate lifetime.

```csharp
var tasks = new StorageA2ATaskStore(new LocalFileStorage(".strands-a2a"));
await using var server = new A2AServer(executor, new()
{
    Name = "Research",
    Url = new Uri("https://research.example/rpc"),
    TaskStore = tasks
});
```

The server borrows the supplied store. The storage adapter also borrows its `IStorage`; neither layer infers disposal ownership. Without an explicit store, the server retains its existing bounded memory cache.

`InMemoryA2ATaskStore` defaults to 1,000 records, 16 MiB per task, and 64 MiB of aggregate retained UTF-8 JSON. An over-capacity save fails without evicting or replacing another record. Smaller replacements release byte capacity. These limits measure JSON bytes, rather than total process memory.

`StorageA2ATaskStore` defaults to the `a2a/tasks` namespace and a 16 MiB per-task limit. It hashes opaque task identifiers into storage keys, so identifiers containing path separators do not become filesystem paths. Recreating the adapter and a `LocalFileStorage` backend preserves saved records. Durability and atomic replacement follow the selected backend. A backend's `ReadAsync` returns a complete byte array; the adapter rejects oversized data before parsing, while the backend controls its own read allocation.

Both built-in stores validate task identifiers, context, status, history, artifacts, part shapes, and duplicate JSON fields. Extensions and metadata remain detached JSON. The adapter verifies that a loaded record matches its requested identifier and rejects malformed JSON or UTF-8. Raw store operations preserve backend exceptions and caller cancellation.

During execution, the server builds and validates a candidate snapshot, checks replay-event bounds, and awaits its save before replacing the visible task and publishing its event. A backend failure cannot expose an unpersisted completed status. The executor attempts to publish a failed status after an execution or persistence failure; if that save also fails, the last successfully published task remains available. The server does not rerun the agent to repair a task-store failure.

The save is the commit boundary. After a backend reports a successful save, the server commits that candidate even if cancellation arrives immediately afterward. A throwing backend may have its own commit ambiguity; the adapter does not claim to roll back external writes.

`tasks/get` reads the live cached task when available. Otherwise it loads the supplied store, preserving metadata and artifact fields and applying `historyLength` to the returned projection. This read does not create an agent or start an execution. Invalid stored records and backend failures produce a sanitized correlated JSON-RPC internal error (`-32603`); a missing record remains task-not-found (`-32001`).

The server links accepted store reads to shutdown and drains them during disposal. Backends must cooperate with cancellation. Store callbacks cannot await disposal of their own server or recursively reload that server's store. Caller cancellation retains the caller token.

Persisted Task JSON alone cannot authorize resuming an unrelated native pause. The subsequent [stable-pause recovery increment](MULTIAGENT_A2A_RECOVERY.md) binds an `input-required` task to a matching native session before continuing or canceling it. Active execution recovery, cross-process coordination, push delivery, and task-retention management remain separate acceptance work.

Pinned source references at `3bd98e618320f0f286acbfb2252f72388030421b`:

- `strands-ts/src/a2a/server.ts`
- `strands-py/src/strands/multiagent/a2a/server.py`

Focused cases cover JSON lifetime, record/count/byte limits, failed replacement preservation, malformed backend records, filesystem reopening, independent namespaces, backend errors, cancellation, durable completion before SSE, failed terminal saves, server disposal, callback cycles, borrowed lifetime, and rejection of task-only execution recovery. Validation and source hashes are recorded in `A2A_TASK_STORE_SCOPE.json` when this increment freezes.
