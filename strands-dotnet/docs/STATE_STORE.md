# Application state keys

`StateStore.Keys()` returns a detached, read-only snapshot of the literal key names. Each call has its own read-only container, including when empty. Later Set, Delete, Clear or snapshot restoration cannot change a previously returned key list. Ordering is unspecified; callers can sort ordinally when needed.

The native store uses ordinal key identity and preserves empty names, Unicode, punctuation and names such as `__proto__` as ordinary strings. It does not apply JavaScript object-property enumeration order or coercion. This provides the pinned TypeScript StateStore `keys()` capability through .NET `IReadOnlyList<string>` without changing Get, GetAll, Set, Delete or Clear. Snapshotting keys does not serialize values or change state versions.

The implementation copies keys under the same private lock used by mutations and wraps that copy in a fresh ReadOnlyCollection. The bounded component evidence includes four missing-API rows plus two existing controls, followed by the separately retained shared-empty-wrapper identity correction. Selected modern and installed Framework profile evidence is separate from genuine Framework 4.8 or full integration execution.
