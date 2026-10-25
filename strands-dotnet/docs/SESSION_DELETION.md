# Latest-session deletion

This increment adds an optional `IDeletableSessionStore` capability while preserving the two-method `ISessionStore` contract. `FileSessionStore` deletes only the exact latest-snapshot record for an opaque identifier, using its existing key resolution. Missing snapshots are idempotent; errors remain observable. Accepted synchronous filesystem deletion cannot be rolled back by later cancellation.

Scenarios: reopened store and fresh Agent restoration; sibling and unrelated bytes; missing directory and repeated missing session; existing two-method implementations; precancellation with exact token; invalid IDs; observable filesystem failure. The first runnable test covers persisted deletion and fresh restore before implementation.

Separate catalog/session-manager orchestration is still required to delete immutable snapshot histories and externally stored context originals. This API does not clear a running Agent or promise coordination with concurrent writers. Python classic managers raise on missing sessions, whereas TS and Python snapshot-manager profiles allow missing deletion; this native capability chooses the latter profile.
