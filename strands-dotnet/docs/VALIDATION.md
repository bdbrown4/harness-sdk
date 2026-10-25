# Behavioral validation

## Test-driven workflow

Use [Kent Beck's Canon TDD](https://newsletter.kentbeck.com/p/canon-tdd) as a development workflow: maintain a scenario list, make one concrete test fail, implement enough behavior for it and previous tests to pass, and refactor after green. A test suite alone cannot prove the order in which tests and implementation were written. Report red/green execution evidence only when it was actually observed.

Tests should assert customer-visible contracts and use independently chosen expected values. Use scripted models to control event sequences, fake HTTP handlers to capture requests and supply fragmented streams, and temporary directories for persistence and file tools. Keep credentials and paid provider calls out of the default test gate.

## API design

Use Scott Hanselman's [discussion of discoverable APIs and unnecessary indirection](https://www.hanselman.com/blog/im-just-a-caveman-the-hanselman-corollary-to-the-clarkewheeler-laws) as a readability check: method names should explain the operation, and parameters should make its dependencies and ownership clear. Add an abstraction when it isolates a real responsibility or substitution point. Compose existing model, tool, storage and telemetry contracts before introducing another framework layer. Keep asynchronous cancellation and resource cleanup visible at their actual execution boundaries.

The shared `netstandard2.0` API keeps Framework consumers on the same async contracts, while `net8.0`/`net10.0` assets can use modern platform APIs. This targeting choice follows the portability and multi-targeting rationale in Scott Hanselman's [open-source .NET library guidance](https://www.hanselman.com/blog/new-prescriptive-guidance-for-open-source-net-library-authors), which also points library authors to dependency, NuGet, SourceLink and versioning guidance. The internal Windows child-process adapter is one concrete boundary: legacy raw-handle startup stays behind the shared process contract, and modern targets retain their BCL path. These exact targets and adapter choices are native design decisions, with their own ABI, process-lifetime and package tests; the article is not an endorsement of this implementation.

[Recorded results](VALIDATION_RESULTS.md) identify the source, environment, outcome and limitations of completed checks. Keep historical checkpoints and failed attempts there; the commands below describe the repeatable release gate.

## Scenario inventory

This is an acceptance inventory, not an assertion that every scenario already has a passing test. Review concrete tests alongside [the parity ledger](PARITY.md) before changing a category's status.

- Agent execution: text-only turn, multiple tool turns, empty model output, tool errors, malformed input, unknown tools, stable message order, accumulated usage, and an extensible terminal stop reason.
- Streaming: interleaved indexed blocks, fragmented tool JSON, reasoning signatures, terminal metadata, provider errors, truncated streams, missing stop events, duplicate/mismatched indices, and early enumerator disposal.
- Tools: full JSON Schema validation before callbacks; bounded maximum concurrency; sequential ordering; deterministic result order; exception-to-result conversion; cancellation with every requested tool represented in history.
- Hooks: awaited callbacks, registration/priority order, after-hook ordering, denial before tool side effects, disposal/unregistration, failure propagation, and cleanup after hook exceptions.
- Lifecycle: reject overlapping invocations, release guards after all exits, cancel before/during model and tool work, respect per-invocation limits, and leave complete provider-replayable tool exchanges.
- Sessions: no snapshot, save/reload, every supported block, corrupt and unsupported schema versions, traversal/invalid identifiers, atomic replacement, cancellation, and separate sessions under one root.
- Context: empty and short history, limits, user/assistant ordering, complete tool pairs, and no mutation of the original message list. Opt-in offloading needs threshold/preview boundaries, actual provider-body reduction, unchanged structured/opaque content, retrieval-loop exclusion with repeated tool IDs, stable restored references, child isolation, shared search fixtures, regex deadlines, bounded results, and cancellation.
- HTTP providers: exact authentication/headers and body; wire casing; SSE comments, multiline data, arbitrary chunk boundaries, completion sentinels, unknown events, API errors, usage, and response/stream disposal.
- Harness: supplied-model identity, returned core `Agent`, explicit options over defaults, prompt assembly, built-in selection, missing roots, and cancellation propagation.
- Native Bedrock: real binary event-stream decoding, model/tool/model replay, reasoning/media/cache/usage, malformed union/ordering, typed AWS failures, read cancellation and deadline/resource ownership.
- MCP: official HTTP/stdio negotiation and lifecycle, real child-process transport, paged/filtered discovery, raw annotations, rich results and error/schema behavior; caller-owned HTTP clients survive disposal.
- Structured output and policy decorators: forced response-tool selection, validation feedback, ordered accepted output after hook transforms, throttling-only retry, no partial replay, invocation-local routing/fallback state and caller state preservation.
- Harness composition: portable configuration and typed bindings, sorted warn/drop behavior for unknown top-level keys, strict nested settings, unsupported options rejected before resolution side effects, owned default model versus supplied ownership, todo/session state and ephemeral environment context, role/capability/depth-limited child conversations and position-correlated tool history.
- File tools: read ranges, encoding, missing files, deterministic errors, write/create behavior, exact and ambiguous edits, lexical traversal rejection, required absolute paths, and cleanup after cancellation. Verify custom policy-enforcing sandboxes independently; do not assert root/symlink confinement for `LocalFileSandbox`, which intentionally uses host-process permissions.
- Shell: real stdout/stderr and exit status, fresh process state, deadline/cancellation with partial output, bounded UTF-8 capture, concurrent pipe draining, working directory/environment overrides, capability selection, and cleanup. Verify legacy Windows Job Object attachment before command release on actual x64/x86 hosts; document modern process-tree/orphan limitations rather than asserting universal descendant termination.
- Packaging: `netstandard2.0`, `net8.0`, and `net10.0` assets; correct core dependency from the harness package; generated XML documentation; README/license metadata; no test/reference-assembly build dependencies leaked; and external consumers using the local NuGet feed.
- Compatibility: dedicated `Strands.Compatibility.Tests` on `net48;net8.0;net10.0`, with Framework execution on Windows. Verify x64/x86 test-host pointer size rather than relying on launch labels. Confirm the Framework consumer resolves the packaged .NET Standard 2.0 asset and its async/JSON dependencies; compilation with reference assemblies does not establish runtime package loading.

## Merge-gate checks

Run from `strands-dotnet/`:

Use the SDK selected by `global.json`, both modern runtimes and PowerShell 7. Supply genuine workers/libraries through `MontyNativeAssetsPath`, `CedarNativeAssetsPath`, `PortAudioNativeAssetsPath` and `NativeNovaAssetsPath` before running native suites. The Windows PortAudio and Nova package guards require their real x64/x86 pairs. These are verification inputs; the build does not generate substitute native assets. The full harness release-package gate additionally needs the five real Monty worker RIDs described below.

The complete source suites also require existing Python and native Bash executables. Set `STRANDS_PYTHON_EXECUTABLE` to the absolute physical interpreter reported by `os.path.realpath(sys.executable)`, and prepend its directory to `PATH`. Verify that direct execution reports that same interpreter. Set `STRANDS_BASH_TEST_EXECUTABLE` to an existing absolute native Bash path: Git Bash on Windows, system Bash on Linux/macOS. The [modern workflow's interpreter selection steps](../../.github/workflows/dotnet-test-lint.yml) perform these identity checks, including macOS launcher re-execution. The tests reject missing or mismatched prerequisites; Windows shell tests use Git Bash rather than WSL.

```sh
dotnet restore Strands.slnx
dotnet build Strands.slnx --configuration Release --no-restore
dotnet format Strands.slnx --verify-no-changes --no-restore --exclude src/Strands.Agents.Schema.Private/Upstream src/Strands.Agents.Schema.Private/PointerUpstream src/Strands.Agents.Schema.Private/MoreUpstream
dotnet test Strands.slnx --configuration Release --framework net8.0 --no-build --no-restore
dotnet test Strands.slnx --configuration Release --framework net10.0 --no-build --no-restore
dotnet run --project examples/GettingStarted/GettingStarted.csproj --configuration Release --framework net10.0 --no-build --no-restore -- --demo
# Cedar is optional and needs its genuine native assets.
dotnet restore tests/Strands.Cedar.Tests/Strands.Cedar.Tests.csproj
dotnet build tests/Strands.Cedar.Tests/Strands.Cedar.Tests.csproj --configuration Release --no-restore
dotnet test tests/Strands.Cedar.Tests/Strands.Cedar.Tests.csproj --configuration Release --framework net8.0 --no-build --no-restore
dotnet test tests/Strands.Cedar.Tests/Strands.Cedar.Tests.csproj --configuration Release --framework net10.0 --no-build --no-restore

# Pack all thirteen public families after providing the complete genuine native assets.
# The private schema library is embedded in Core; it is not a fourteenth public package.
dotnet pack Strands.slnx --configuration Release --no-restore --output artifacts/packages
dotnet pack src/Strands.Agents.Cedar/Strands.Agents.Cedar.csproj --configuration Release --no-build --no-restore --output artifacts/packages

pwsh -File scripts/Test-ManagedPackages.ps1 -PackageDirectory artifacts/packages
pwsh -File scripts/Test-Packages.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Bedrock/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.BedrockKnowledgeBase/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.SageMaker/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Monty/native/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Cedar/native/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Bidi/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Bidi.Audio/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.OpenTelemetry/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.S3/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
pwsh -File src/Strands.Agents.Sqlite/validation/Test-Package.ps1 -PackageDirectory artifacts/packages
```

On Windows, additionally run:

```sh
dotnet test tests/Strands.Compatibility.Tests/Strands.Compatibility.Tests.csproj --configuration Release --framework net48 --no-build --no-restore
dotnet run --project examples/GettingStarted/GettingStarted.csproj --configuration Release --framework net48 --no-build --no-restore -- --demo
```

The library build includes `netstandard2.0;net8.0;net10.0` on each host. The dedicated compatibility project adds `net48` while core/harness test projects remain modern; request `net48` on that project rather than the full solution. Private reference assemblies permit cross-platform compilation; Framework execution requires Windows. CI runs modern tests/demo/consumers on Windows/Linux/macOS, with Framework execution on Windows. [Test-Packages.ps1](../scripts/Test-Packages.ps1) creates an independent consumer with an exact harness version, short scratch path, sibling cache, and isolated build properties. It verifies output and retains diagnostics; `-ScratchDirectory` accepts a new/empty location. [Recorded results](VALIDATION_RESULTS.md) identify completed Windows, Linux, and macOS checks and their exact environments; configured coverage alone is not execution evidence.

The complete feed requires all five genuine Monty worker RIDs, Cedar's advertised native libraries, the Windows PortAudio and Nova x64/x86 pairs, and their dependency notices. Keep package guards enabled. A partial feed cannot qualify the assembled Harness. Declare native asset paths and retain the exact source, native producer, package archive and local feed hashes used by each consumer.

[Test-ManagedPackages.ps1](../scripts/Test-ManagedPackages.ps1) creates independent Core and GoogleAuth consumers, publishes modern outputs and verifies normal target assets and exact local-package origins. Its `-TargetFramework net48 -PrepareOnly` mode prepares separate x64/x86 payloads for the isolated Framework runner; preparation or execution on Framework 4.8.1 does not establish genuine 4.8. The remaining validators exercise their package-specific operations, native loading and persistence. Check published output as well as the build tree, and launch packaged Unix workers without manually changing consumer permissions.

The [isolated Framework workflow](../../.github/workflows/dotnet-framework48.yml) runs genuine Framework 4.8 checks separately from the installed modern Windows host. [Test-Framework48.ps1](../scripts/Test-Framework48.ps1) rejects registry releases below 4.8 or at/above 4.8.1 and records both registry views, CLR and OS metadata. Each `-Platform x64`/`x86` run requires a nonempty, fully passing TRX and an executed pointer-size assertion. Core and GoogleAuth package consumers run before either architecture branch returns; native and storage consumers have their own architecture/output checks. SQLite uses separate writer/read processes to verify persistence. Preserve the script's existing parameter positions when appending optional inputs.

Run the complete Harness consumer on both architectures as part of release verification. The isolated workflow runs the project-based offline Framework example on x64; its x86 branch returns before that example. A script branch returning after compatibility tests does not count as executing a consumer. Readonly mounted package payloads, retained stdout/stderr/TRX diagnostics and local script-contract tests support the workflow; configured YAML and synthetic dispatch tests do not prove an actual container or GitHub Actions run.

## Validation script contracts

Run the scripts used by the Framework workflow before dispatching it. They exercise package target selection, interpreter prerequisite copying, runtime-runner contracts and native archive/workflow contracts without treating synthetic fixtures as genuine Framework execution:

```powershell
./scripts/tests/Test-ManagedPackages.Contract.Tests.ps1 -ProductionScript ./scripts/Test-ManagedPackages.ps1 -EvidenceDirectory ./artifacts/contracts/managed
./scripts/tests/Test-Framework48Interpreters.Contract.Tests.ps1 -ProductionScript ./scripts/Stage-Framework48Interpreters.ps1 -EvidenceDirectory ./artifacts/contracts/interpreters
./scripts/tests/Test-Framework48.Contract.Tests.ps1 -ProductionScript ./scripts/Test-Framework48.ps1 -FixtureDirectory ./scripts/tests/fixtures/framework48 -EvidenceDirectory ./artifacts/contracts/framework48
./scripts/tests/Test-DotNetCI.Contract.Tests.ps1 -RepositoryRoot .. -EvidenceDirectory ./artifacts/contracts/ci
```

Check each script's exit code before starting the next. File-symlink tests require the host's normal symlink capability; report explicit prerequisite skips instead of counting them as exercised cases. Package asset selection must validate every applicable target entry, including the ordinary framework and RID-qualified entries produced by a normal publish.

## Evidence to record for review

Record the source commit, SDK/runtime versions, OS, exact commands, outcomes, test counts, and any skipped or unavailable checks in the review handoff. Also record an end-to-end script or sample run and a separate local-package consumption result. Keep routine logs outside source comments.

Modern CI uses normal console verbosity, TRX records, and a 120-second per-test hang timeout with mini dumps. The test host is terminated on timeout and diagnostics are retained for seven days; see the [.NET CLI VSTest documentation](https://learn.microsoft.com/dotnet/core/tools/dotnet-test-vstest). A timed-out or canceled run is not passing evidence.

Live provider verification should identify API family, model configuration, tested features and date. Fake transports establish exercised protocol behavior; they do not establish successful provider credentials, live model support, rate limits or device behavior. Run paid service and microphone/speaker checks only as explicitly configured opt-in profiles. Cross-language session file exchange is not a supported contract.

## Release acceptance

Assess the finite native application outcomes in [the parity scope](PARITY.md), separately from the release gates: current platform regressions, genuine Framework execution, legitimate native assets, complete package/API validation, fresh and published consumers, actual CI execution, and engineering/API/documentation review. Count overlapping tests once within their stated profile; do not turn test counts or private source inventories into a feature-completion percentage. Preserve failed and aborted runs with their actual status. Maintainer adoption, official support and publication are separate decisions.
