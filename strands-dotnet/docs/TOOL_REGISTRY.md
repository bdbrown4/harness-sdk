# Standalone tool registry

`ToolRegistry` is an insertion-ordered, standalone registry of borrowed `ITool` instances. It does not mutate an Agent, advertise tools to a model, execute tools, initialize providers, or load modules.

```csharp
var registry = new ToolRegistry(new[] { searchTool });
registry.Add(otherTool);
registry.Replace(updatedSearchTool); // Same exact name; retains its insertion slot.
var pythonDirect = registry.Resolve("search_tool", ToolNameResolution.UnderscoreAlias);
var exact = registry.Get("search-tool"); // Nullable, exact lookup only.
```

`Items` returns a detached read-only membership snapshot. Registry mutation cannot change an earlier snapshot; tool objects themselves remain borrowed. Replacement, removal, clear and failed batches never dispose tools. A registration captures its `ToolSpec.Name` once; custom tools must continue to expose a stable specification, as existing Agent registration requires. The registry does not alter `ToolSpec` validation: names remain 1–64 ASCII letters, digits, hyphens or underscores, and descriptions remain nonblank.

`Add` rejects exact duplicates. `AddRange` validates a complete batch before publishing it. `AddOrReplace` accepts a single tool or a batch; a replacement keeps the original slot, new names append, and repeated exact names within its batch use the last supplied tool. `Replace` requires an existing exact name. `Get` returns null when missing; `TryGet` returns false. `Remove` uses exact names and returns false when missing. `Clear` removes registrations only.

The default `ToolNameConflictPolicy.RejectNormalized` also rejects case-sensitive names differing only by hyphens versus underscores, matching both pinned upstream registries. Explicit `ExactOnly` retains native compatibility for applications that deliberately register such names. Case-only names are permitted under both policies.

Name resolution is explicit. The default `Exact` performs ordinal exact lookup. `UnderscoreAlias` first checks exact names, then, only when the request contains underscores, finds the first registration whose hyphens become underscores. This matches Python's direct caller. `TypeScriptDirect` adds a final insertion-order lowercase comparison. It does not combine case folding and alias rewriting: `MY-TOOL` can match `my-tool`, but does not match `my_tool`. Exact matches always win. Removing and readding moves a registration to the end; replacing does not. Missing `Resolve` or `Replace` throws native `KeyNotFoundException` rather than a foreign exception type. Lookup accepts arbitrary nonnull query strings; empty names simply do not match.

Mutations are serialized within an instance. Enumeration and custom specification getters run before the publication lock. Enumeration/getter exceptions or conflicts leave the calling batch unpublished, including replacements already prepared in that batch. This atomic range behavior is a native strengthening over TypeScript's sequential loops. Mutations independently performed by application callbacks are separate operations and are not rolled back. Successful reads return one consistent membership snapshot; this does not make borrowed tool implementations thread-safe.

Agent-bound mutation, filter authority, active/pending invocation guards, child inheritance and model request schema caches require a separate guarded integration. Existing Agent and model-issued tool lookup remain unchanged and exact. Generic provider leases, directory watching, managed hot reload and arbitrary language module execution are separate capabilities.

Sources at `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript `src/registry/tool-registry.ts:11–183` and its `__tests__/tool-registry.test.ts:121–217`; Python `src/strands/tools/registry.py:236–311`, `src/strands/tools/_caller.py:155–178`, and tests `test_registry.py:64/579`, `test_caller.py:164`. Exact model execution remains in TypeScript `tools/executors/executor.ts:112` and Python `tools/executors/_executor.py:509`.
