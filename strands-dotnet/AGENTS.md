# .NET development guide

Read the [root guide](../AGENTS.md), [tenets](../team/TENETS.md), and [decision records](../team/DECISIONS.md) first. These instructions also apply to the companion `harness-dotnet/` package.

## Scope and parity

`Strands.Agents` contains the native agent primitives. `Strands.Harness` composes those primitives into an ordinary `Agent`. Keep the core independent of the harness. This is an alpha implementation proposal; [the parity ledger](docs/PARITY.md) records what remains before parity with Python and TypeScript can be claimed. The [public API proposal](docs/API.md) records current signatures/defaults and deliberate contracts. The [design RFC](../team/designs/0021-dotnet-sdk.md) is pending maintainer and public API review.

Match existing concepts and names using C# conventions: PascalCase public members and types, camelCase parameters and locals, async methods ending in `Async`, and provider wire keys preserved explicitly. Public APIs use shallow namespaces. Do not introduce abstractions solely to satisfy an acronym or to anticipate an unimplemented feature.

## Design and implementation

- Depend on narrow capabilities (`IModel`, `ITool`, `ISessionStore`, `IConversationManager`). Keep transports, storage, and orchestration separate.
- Use `Task`, `ValueTask`, `IAsyncEnumerable<T>`, `CancellationToken`, `HttpClient`, and `System.Text.Json` directly. Avoid wrappers that merely rename framework types.
- Await lifecycle hooks. Preserve hook order, cancellation, tool-call/result pairs, and single-invocation ownership when changing the agent loop.
- Priorities ascend for every hook; only registration order within equal-priority after-hooks reverses. Preserve primary errors and attach secondary cleanup failures under `Exception.Data["Strands.CleanupErrors"]`.
- `AsTool` creates fresh conversation state while sharing model/tool dependencies and the registry. Callbacks can run concurrently. A sliding window may exceed its message target to retain the complete user exchange.
- Caller invocation state is user-owned. Store SDK coordination state separately.
- Preserve the original exception and release async enumerators, HTTP responses, streams, and invocation guards on every exit path.
- Explicit user configuration wins over harness defaults. The harness returns a core `Agent` rather than a second agent hierarchy.
- Add XML summaries to meaningful public extension points. Comments describe constraints and invariants; design rationale belongs in the RFC or PR.
- Library XML documentation generation is enabled; maintain public XML documentation and keep CS1591 clean. An automated API compatibility baseline/freeze remains pending. Public API review is a maintainer process.

## Test-driven changes

Follow [Kent Beck's Canon TDD](https://newsletter.kentbeck.com/p/canon-tdd): list behavioral scenarios, implement one runnable failing test, make it pass with the existing suite, then refactor if useful. Add newly discovered edge cases to the list. Do not replace assertions with snapshots copied from actual output, or claim test-first evidence from the existence of tests alone.

Prefer deterministic scripted models, fake HTTP handlers, and temporary directories. Assert externally visible behavior: request content, exact event order, complete history, bounded execution, cancellation cleanup, restored sessions, and package consumption. Live provider tests require explicit credentials and are separate from the default gate.

## Validation

Libraries target `netstandard2.0;net8.0;net10.0`. Framework 4.8 consumers select the .NET Standard 2.0 asset; no "4.8 Standard" target exists. The dedicated compatibility suite targets `net48;net8.0;net10.0`; core/harness suites stay on modern targets. Keep modern-only BCL calls behind compatible implementations or target guards; async interfaces and JSON support on `netstandard2.0` come from package dependencies. `Microsoft.NETFramework.ReferenceAssemblies` is a private build dependency for `net48` tests/consumers, not a runtime distribution.

The public required/init options require C# 11+ consumers; current examples use C# 12. Framework samples constructing providers reference `System.Net.Http` and enable binding redirects. Preserve those settings and the explicit distinction between modern compiler requirements and Framework runtime support. Do not silently redesign the API around older C# syntax.

From `strands-dotnet/`, install the .NET 10 SDK selected by `global.json`, plus .NET 8 and 10 runtimes, then run:

```sh
dotnet restore Strands.slnx
dotnet build Strands.slnx --configuration Release --no-restore
dotnet format Strands.slnx --verify-no-changes --no-restore --exclude src/Strands.Agents.Schema.Private/Upstream src/Strands.Agents.Schema.Private/PointerUpstream src/Strands.Agents.Schema.Private/MoreUpstream
dotnet test Strands.slnx --configuration Release --framework net8.0 --no-build --no-restore
dotnet test Strands.slnx --configuration Release --framework net10.0 --no-build --no-restore
dotnet run --project examples/GettingStarted/GettingStarted.csproj --configuration Release --framework net10.0 --no-build --no-restore -- --demo
dotnet pack Strands.slnx --configuration Release --no-restore --output artifacts/packages
pwsh -File scripts/Test-Packages.ps1
```

On Windows, also run `dotnet test tests/Strands.Compatibility.Tests/Strands.Compatibility.Tests.csproj --configuration Release --framework net48 --no-build --no-restore` and the getting-started `--demo` with `--framework net48`; do not request `net48` tests on the full solution, since modern test projects do not target it. CI builds all targets on Windows/Linux/macOS using reference assemblies and runs modern tests/demo/packed-package checks; only Windows executes Framework tests/demo/consumers. The package script retains diagnostics in a short temporary or explicit new/empty scratch directory, puts caches beside the consumer, and isolates repository build properties. Update [recorded results](docs/VALIDATION_RESULTS.md) with exact observed counts/environments; do not count repeated target executions as unique tests or infer live-provider compatibility.
