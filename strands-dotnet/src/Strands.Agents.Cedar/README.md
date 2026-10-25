# Strands.Agents.Cedar

An optional native Cedar authorization package for Strands Agents. This package
uses the official `cedar-policy` 4.12.0 engine and
`cedar-policy-mcp-schema-generator` 0.6.1. Both are Apache-2.0 licensed.

The two native libraries have separate Cargo lockfiles because those published
crates pin incompatible versions of `cedar-policy-core` (4.12.0 and 4.11.0).
Policy parsing, evaluation, validation, and MCP schema generation execute those
upstream implementations.

The managed library targets .NET Standard 2.0, .NET 8, and .NET 10. Existing
Framework 4.8 applications consume the .NET Standard asset. Native asset targets
are Windows x86/x64, Linux x64, and macOS x64/ARM64. Windows native binaries require
Windows 10 or Windows Server 2016 or later, following
[Rust 1.97's MSVC target requirements](https://github.com/rust-lang/rust/blob/1.97.0/src/doc/rustc/src/platform-support/windows-msvc.md#os-version).
They statically link the VC runtime. Framework 4.8 applications use this same
native OS baseline. Linux builds use the pinned Ubuntu 20.04 container to
establish a glibc 2.31 baseline. The dedicated native workflow builds and exercises
each architecture, collects all assets, validates the complete package, and runs
fresh-cache consumers. Linux/macOS execution and clean Framework 4.8 verification
are recorded per source checkpoint in the SDK's validation results. No package
has been published.

```csharp
using Strands.Agents.Interventions.Cedar;

var authorization = new CedarAuthorization(new()
{
    Policies = "permit(principal, action == Action::\"search\", resource);",
    PoliciesAreInline = true,
});
// Supply authorization in AgentOptions.Interventions.
```

`Policies` and `Schema` follow the pinned SDK's suffix-based file loading by
default. Set `PoliciesAreInline` or `SchemaIsInline` when another filesystem has
already loaded the content; a final comment ending in `.cedar` must then remain
text. `Entities` accepts a detached JSON array, and `EntitiesPath` reads a JSON
file. `Tools` invokes the official MCP schema generator when `Schema` is absent.
Explicit schemas receive full Cedar policy validation; generated schemas follow
the baseline's action-error-only validation policy because application context
enrichers can add fields outside the tool schema.

The default `UseSchemaForAuthorization = false` matches TypeScript: schemas
validate policies at construction and reload, while authorization uses the
explicit JSON representation. Set it to `true` for Python's schema-aware
authorization. The real engine then parses context and entities against the
schema, including implicit entity and extension values, and validates the
request. This also applies to generated schemas; extra enricher fields must fit
the declared context when this mode is enabled. Reload replaces policies,
entities, and the authorization schema together after complete validation.

`Principal` and `PrincipalResolver` are mutually exclusive. The default identity
is `User::"anonymous"`; a missing resolved identity denies. `ContextEnricher`
receives the original caller invocation state and returns extra `context.session`
fields. Native `hour_utc` and `call_count` always take precedence. The optional
namespace applies to default identities, actions, resources, and generated
schemas. Typed entity identifiers preserve quoted IDs safely.

Accepted authorization counts persist in the agent's application state under
`cedar-authorization`, separately for each tool. A shared handler has no
cross-agent counter cache. Evaluation and count commits serialize per agent;
denials and requested cancellation do not consume allowance.
`ResetCallCounts(agent)` resets that agent's counts. `Reload()` validates the
entire candidate configuration before atomically replacing it.

An engine result reporting evaluation failure denies in both profiles. Native
operation exceptions use `EngineExceptionPolicy.UseHandlerPolicy` by default,
matching TypeScript: the original exception reaches `OnError`. Set
`EngineExceptionPolicy = CedarEngineExceptionPolicy.Deny` for Python's behavior,
which denies engine exceptions even if `OnError` permits callback failures.
Callback exceptions always use `OnError`, defaulting to `Throw`. Selecting
`UseSchemaForAuthorization = true` and `EngineExceptionPolicy.Deny` together
selects these Python semantics. Cancellation is checked before and after
synchronous native evaluation;
it cannot preempt a running Rust call. There is no model or transport ownership
transfer. Loaded native modules stay resident for the process, and every returned
UTF-8 result buffer is freed by the library that allocated it.

Source builds require .NET SDK 10 and Rust 1.97.0 with `rustfmt` plus the selected
native target. For example, on Windows:

```powershell
rustup toolchain install 1.97.0 --profile minimal --component rustfmt --target i686-pc-windows-msvc,x86_64-pc-windows-msvc --no-self-update
./native/Build-Native.ps1 -RuntimeIdentifier win-x64
./native/Build-Native.ps1 -RuntimeIdentifier win-x86
```

Native outputs are generated under `strands-dotnet/artifacts/cedar-native`.
Packing fails if any supported RID or original dependency-license notices are
missing. The package's Framework MSBuild target copies both Windows architecture
folders; the adapter also supports managed-assembly shadow copying. The
core SDK package remains usable without Rust or this optional module.
The independent package checks use separate x64 and x86 project intermediates,
verify their PE architecture flags, and execute both Framework consumers after
the second build.
