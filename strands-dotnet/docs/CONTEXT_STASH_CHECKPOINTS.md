# Context stash in native sessions

The first-class `ContextManager` persists original content separately from reduced conversation history. Ordinary `ISessionStore` snapshots retain enough information to restore those originals without treating a preview as the original.

An ephemeral backend uses the existing `SessionSnapshot.ContextStash` map of reference keys to typed block JSON. A durable backend uses the additive `ContextStashStorageType` diagnostic marker and does not embed its entries. These fields are mutually exclusive. Old sessions with neither field, or with the earlier inline-only field, remain supported. The positional `SessionSnapshot(int, IReadOnlyList<Message>)` constructor and two-value deconstruction are unchanged.

The external marker records the base storage class name. Restoring it under a different class warns that originals may be inaccessible; it does not copy, migrate, clear, or read the old backend. Supplying the same class name is a diagnostic match rather than proof that it points at the same external data. Storage and session dependencies remain borrowed.

`ContextManagerOptions.StashRestoreFailurePolicy` defaults to `Throw`, matching the pinned TypeScript session manager. `Continue` selects Python's best-effort backend restoration: failures are warned about and the restored conversation remains usable with whatever originals are available. Requested caller cancellation always escapes with the caller's token, including when a backend throws cancellation with a different token or reports another failure after requesting cancellation. Diagnostic-listener exceptions cannot turn this best-effort path into an initialization failure.

All inline references and block JSON are validated and detached before any restore write. Malformed checkpoint data, including conflicting inline/external fields, is rejected before replacing native agent history or application state, even under `Continue`. This validation policy is a deliberate native strengthening. A caller cannot mutate the pending batch or dispose its JSON document during an awaited backend write to change later entries.

Restoration merges entries, replacing matching references and retaining unrelated entries. Writes to an arbitrary `IStorage` backend are nontransactional: a failure on a later write can leave earlier writes accepted. `Continue` does not guarantee that every original is available. A failure capturing ephemeral originals prevents saving a new conversation checkpoint with silently missing stash data.

These contracts follow baseline `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/session/session-manager.ts:350–382` and Python `src/strands/session/snapshot_session_manager.py:696–755`. Both distinguish inline and external stash data. TypeScript propagates stash restore errors; Python logs ordinary failures and continues. The native snapshot format remains independent of their JSON schemas.

Bare `Agent.TakeSnapshotAsync` does not embed stash data; the upstream bare snapshot helpers also leave stash augmentation to their session managers. The first-class context manager's overflow retry counter and retrieval skip set are transient. Python's classic conversation-manager removal counts, per-turn cadence and previous summary are a separate runtime/state parity increment, not a serialized state fabricated for this component.

`StashCheckpointTests` covers native file-session reopening, legacy inline data, external markers, cancellation, validation before mutation, JSON ownership, storage failures and borrowed lifetimes. This component does not establish full SDK/harness parity, external-store transactions, cross-language session exchange, live backend interoperability, or execution on an untested runtime or operating system.
