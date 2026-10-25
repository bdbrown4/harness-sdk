# Indexed repository deletion

`IDeletableSessionRepository` is an optional capability. Existing nine-method repositories remain valid.
The intended native profile removes all rows under the selected repository/session namespace, including
agent, message and multi-agent rows, without deserializing them. Missing sessions are a no-op, matching
the snapshot-manager profile; Python classic missing-session exceptions are a separate source profile.
This is independent of FileSessionStore's latest snapshot record and external durable stash/catalog data.
Backends remain borrowed. Operations serialize within this repository instance; there is no cross-writer
transaction, rollback or resurrection guarantee. Accepted partial deletions on error remain observable.

Deletion detaches and validates the entire listed key set before its first mutation. Every key must
remain within the exact canonical session prefix. Each backend deletion is awaited before the next;
caller cancellation stops later mutations and retains an independent backend error as its cause.
No record deserialization, backend disposal, live Agent clearing, or external stash clearing occurs.

The file backend retains its physical dot-directory aliases while projecting listings back into the
requested logical namespace. A final partial dot prefix still matches dotfiles. Global storage-key
normalization and literal-dot namespaces in memory or custom backends remain unchanged.

The initial persisted-delete capability failure is retained. Two file namespace cases first failed
during extended-Windows-root writes; a physical path-only correction then exposed the actual delete
listing failures, followed by the same three namespace cases passing. Thirty-four additional safety
and alias cases are first-green conformance. One test-only attempt incorrectly assumed that
InvalidDataException derives from IOException; its four assertion failures and corrected exact
exception assertions are retained separately. All39 cases now pass six selected profiles,238 executions
including architecture controls. Normal Core API/package checks cover netstandard2.0/net8.0/net10.0.
The Framework profiles run installed4.8.1; genuine4.8 and Linux/macOS verification remain separate.
No fresh package consumer or combined-source matrix is inferred. These checks do not establish
immutable catalogs or complete-session parity.
