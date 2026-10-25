# Strands harness for .NET

`Strands.Harness` composes the native `Strands.Agents` runtime into an ordinary `Agent`. It supplies .NET Standard 2.0, .NET 8 and .NET 10 assets; .NET Framework 4.8 applications consume the .NET Standard 2.0 asset. Examples use a modern C# compiler. This alpha contribution implements the reviewed native harness feature families. See [feature scope](../strands-dotnet/docs/PARITY.md) for supported contracts and [recorded validation](../strands-dotnet/docs/VALIDATION_RESULTS.md) for tested profiles and open release checks. Maintainer adoption, public API approval, maintenance ownership and publication are separate decisions.

## Use

Reference the `Strands.Harness` project while developing this contribution. The harness depends on core, Bedrock, genuine native Monty execution and the OpenTelemetry add-on. Additional provider and Bidi/Cedar packages are available separately. Local packaging is subject to each package's asset and API gates; a complete harness feed needs the full Monty package, including all five native worker RIDs and notices. Publication remains a separate maintainer decision. See [the SDK build/package instructions](../strands-dotnet/README.md#build-from-source).

```csharp
using Strands.Agents;
using Strands.Harness;

static async Task<string> ReviewReadmeAsync(IModel model, CancellationToken cancellationToken)
{
    await using var agent = await Harness.CreateAsync(new HarnessOptions
    {
        Model = model,
        Instructions = "Help maintain this project's documentation.",
        BuiltinTools = ["read"],
        Session = new SessionOptions { Id = "documentation", Directory = "./.agent/sessions" },
    }, cancellationToken);

    var result = await agent.InvokeAsync("Read the project README and suggest improvements.",
        cancellationToken: cancellationToken);
    return result.ToString();
}
```

Supply a model configured by your application, as described in [the SDK README](../strands-dotnet/README.md), or omit `Model` to use `bedrock/global.anthropic.claude-opus-5`, matching the upstream harness default. The native AWS SDK client and credential chain are initialized lazily on the first request; constructing the harness does not make a provider request. The convenience Bedrock adapter uses `us-west-2` unless configured explicitly.

The agent owns a default model created by the harness and releases it through `DisposeAsync`. Supplied models remain caller-owned unless explicit core `AgentOptions.OwnsModel` transfers ownership. Children inherit the parent model without taking ownership of it. An explicit core model wins over the harness model and default resolution.

## Supported behavior

- The shared Python/TypeScript behavioral prompt, with domain instructions appended. `HarnessPrompt.Build` can append request context too.
- Built-in selection includes `shell`, `read`, `write`, `edit`, `web_fetch`, `web_search`, `programmatic_tool_caller` and `subagent`, combined with consumer tools. Omission selects the native default catalog; `[]` disables it. Actual provider-native search availability is resolved from model configuration, with explicit Exa as a separate service option.
- Portable JSON also supports mapping edits: `"*"` chooses the starting set and per-tool booleans/configuration edit it. Unavailable selections fail explicitly. Shell and curl require a command-capable selected sandbox; programmatic execution requires a genuine Monty worker. Hyphens and underscores collide across application and plugin tool names.
- A selected filesystem seam for text and media tools. A host implementation reads bounded UTF-8 text, writes files and creates parent directories. `read` numbers text lines and supports offset/limit; recognized image/document paths return typed media through the selected byte filesystem. `edit` requires a unique, non-empty exact match.
- Local conversation and application-state persistence enabled by default in `./.agent/sessions`, using a fresh eight-hex session ID. Specify the same ID to resume; IDs are trimmed, lowercased and sanitized to ASCII letters, numbers, `_` and `-`. The native snapshot format does not interoperate with Python or TypeScript snapshot files. Set `Session.Enabled = false` for an in-memory conversation.
- Default `Todos` and `EnvironmentContext` plugins. Todos provide a real `todo_write` tool and session-persisted state; reminders affect model requests without entering durable history. Environment context discovers project documents through `IEnvironmentSandbox`; a custom text-only sandbox receives date context without exposing host filesystem details. `BuiltinPlugins = []` disables both.
- Standard `subagent` delegation with a generalist role, fresh child conversations, inherited core policies, cancellation, and a default depth limit of two. Selected tools constrain both application and plugin-generated capabilities. The low-level `Subagent.Create` API supports `Fixed`, `Inherit`, `Open`, `Choice`, `Option`, `Preset`, and `AgentSpec`; context modes are `none`, `all`, and `no_tools`. Shared context removes pending calls and provider reasoning and retains complete tool exchanges. Child failures return correlated error results. Child sessions are disabled.
- Core options and extension points remain available through `HarnessOptions.AgentOptions`. An explicit core model, system prompt, auxiliary model or session store wins over the matching harness convenience setting; tools from both options are additive. Core hooks, invocation limits, tool execution strategy, conversation manager and messages pass through.

The returned value is the core `Agent`; invoke and stream it using the APIs described in [the SDK README](../strands-dotnet/README.md).

Core [tool-provider bindings](../strands-dotnet/docs/TOOL_PROVIDERS.md) pass through `HarnessOptions.AgentOptions`. [Declarative MCP providers](docs/MCP_TOOL_PROVIDERS.md) are now composed after application providers: configuration is captured through the selected sandbox once, and normal harness creation awaits agent initialization. Named server grants select original provider declarations before child initialization; excluded servers cannot return through the ordinary tools axis. Children and grandchildren rebuild their own delegation bindings, retain only admitted names, and reject changed provider names before publication. Provider objects, models, sandbox and rich/streaming tools remain borrowed; fresh connector-created sessions belong to shared generations until the final consumer leaves. Already-connected borrowed clients and router leases are not silently transferred into those owning factories. Portable identifiers and [optional live publication](../strands-dotnet/docs/LIVE_TOOL_PROVIDER_UPDATES.md) retain their explicit native binding contracts.

## Portable configuration

`HarnessAgentConfig.Parse` and `Normalize` validate the shared camelCase configuration shape, normalize defaults, and preserve module metadata for round trips. Unknown top-level keys are ignored with a sorted trace warning, matching both loaders. Nested objects retain their schema rules; duplicate raw JSON keys and duplicate selections are rejected. Duplicate-key rejection is an explicit native parser choice. `ToOptions` resolves supported native model identifiers or uses explicit `HarnessConfigBindings` functions for models, tools, plugins, sandboxes, and specialist agents. Module references do not automatically execute Python/TypeScript source or install dependencies.

Applications can opt into [trusted managed assembly exports](docs/MANAGED_MODULES.md) with `HarnessAssemblyLoader`. It supports named/default static values, real `[Tool]` methods and ordered lists, retains explicit binding precedence, and shares native component types with the host. This loads compiled application code with process permissions; it does not provide isolation, foreign-language execution, dependency installation or hot reload.

`ToOptions` supports the implemented native context, memory, skills, caching, MCP, intervention and background-task settings; unsupported values still fail before executable bindings. Default-enabled capabilities do not all need to be disabled to create a runnable configuration. The following intentionally limited example switches off selected features; those switches are application choices, not missing implementations:

```csharp
var config = HarnessAgentConfig.Parse("""
{
  "builtinTools": { "*": false, "read": true, "subagent": { "maxDepth": 1 } },
  "caching": false,
  "contextManager": false,
  "skills": false,
  "memory": false,
  "session": false
}
""");
await using var agent = await Harness.CreateAsync(config.ToOptions());
```

This example retains the default todo/environment plugins and lazy Bedrock model. Pass `new HarnessConfigBindings { ModelResolver = identifier => configuredModel }` to use an application-owned model instead. Relative session directories resolve against the `ToOptions` project directory. Explicit core options retain precedence.

## Filesystem access

`LocalFileSandbox` operates with the host process's filesystem permissions. It provides **no isolation, root confinement or authorization policy**. Absolute paths are required and lexical `..` segments are rejected; this validation is not an access boundary. Supply an `ISandbox` implementation that enforces your application's filesystem policy, or disable file tools when they are inappropriate. Edits are read/replace/write operations and do not provide concurrent-writer transactions.

Writes stage UTF-8 text in a randomly named sibling file, flush it, check cancellation, and replace the destination. A failed staging operation leaves existing destination contents intact. A terminal symbolic-link or reparse-point destination is rejected; select its ordinary target file explicitly. Parent-directory links are still followed, and link checks do not provide protection against concurrent filesystem changes. Windows replacement merges destination metadata without ignoring merge errors. Modern .NET assets copy existing Unix permission bits and create new Unix files with owner read/write permissions; the .NET Standard asset uses platform permission defaults. Ownership and extended Unix ACL preservation are not guaranteed. Atomic replacement depends on the destination filesystem and does not promise crash recovery on arbitrary filesystems.

Text reads default to a 5 MiB byte limit; use the `LocalFileSandbox` constructor to configure it. `read` returns up to 2,000 text lines by default. Typed `Read.PagingProfile` selects exact native integral, Python/Pydantic2.5 or TypeScript numeric paging; there is no separate 10,000-line paging cap. Image/document payloads preserve bytes and are not paged; explicit Python/TypeScript profiles validate paging inputs before media I/O. See [paging profiles](docs/READ_PAGING_PROFILES.md) for input, numbering and source-reference limits. Media support defaults from the resolved model, with an explicit `Read.Media` or portable `builtinTools.read.media` override. Media reads have no default byte limit, matching the pinned sources; an explicit native `Read.MaximumMediaBytes` requires a bounded reader and rejects oversized files. See [rich read behavior](docs/RICH_READ.md) for formats, placeholders, model/router policy and selected-filesystem requirements.

## Shell execution

The host sandbox implements `ICommandSandbox`. Each call starts system Windows PowerShell or modern Unix `/bin/sh`, with a two-minute maximum and combined 1 MiB UTF-8 output capture by default. Configure `LocalShellOptions` on `LocalFileSandbox` for working directory, environment, compatible shell executable and execution bounds. `shell` accepts `command` and optional positive `timeout` seconds; completed commands return `output`, `error` and `exit_code`. Nonzero exits are command results; timeouts return error status with partial output and code 124. Caller cancellation propagates.

Owned shell and Monty binary process paths use the [byte-safe process adapter](../strands-dotnet/docs/BINARY_PROCESS_PIPES.md). Modern targets select BOM-free redirected input through the BCL; the legacy Windows implementation owns raw pipes and uses suspended process creation with an explicit inherited-handle list. The 11-contract component retains the original BOM failures and gates command release/cancellation on both Framework architectures. The documented process-tree and orphan limits still apply.

Both pipes drain concurrently and excess captured bytes are marked/discarded. Modern assets use platform process-tree termination and bounded drain cleanup, with limits for inaccessible or already-orphaned descendants. Legacy Windows uses a kill-on-close Job Object and a bounded handshake that keeps user command code opaque until attachment succeeds. Unsupported legacy platforms and failed job assignment do not release the command. This is host execution with process permissions, without isolation or approval. File-only custom sandboxes do not gain host command authority; explicitly selected unsupported capabilities fail clearly.

## Web access

The curl `web_fetch` transport's 5 MiB body bound also requires room for its encoded output within the selected sandbox's capture policy. A caller-supplied `new LocalFileSandbox()` retains its 1 MiB hard capture cap. Use `new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 8 * 1024 * 1024 })` to allow the full 5 MiB curl body envelope. Harness-created default sandboxes use that 8 MiB policy. Truncated capture fails explicitly; the tool never raises a caller's hard limit.

For repeated Exa searches, reuse an application-owned `HttpClient` through `ExaWebSearchConfig.HttpClient`. Each query still owns and disposes its MCP session. The supplied HTTP client remains caller-owned and must stay alive until its calls finish.

Direct web fetching and Exa use harness-process networking; local curl uses host networking. Private and link-local addresses remain reachable when that network permits them. Applications must select their network and sandbox policies accordingly; URL validation and quoting do not provide network isolation.

## Feature scope and release status

Parity means equivalent application capabilities through supported native .NET APIs. The harness combines provider resolution, context/memory, skills, sessions, MCP, web/filesystem/shell tools, genuine Monty execution, delegation, background tasks, interventions and tracing. Cedar policy binding is optional and uses an explicit factory. The [feature contract](../strands-dotnet/docs/PARITY.md#scope-and-current-acceptance) records the source pin and native limits.

`subagent` supports independent MCP grants and the [durable child interruption bridge](docs/SUBAGENT_INTERRUPTS.md), including fresh-child restoration, partial approvals and background continuation. Trusted compiled C# modules are native extensions; executing foreign language modules is not supplied. Different algorithms and internal structures are allowed, while protocol/data interoperability, cancellation and resource safety remain required.

Current composed regression/platform checks, fresh complete Harness NuGet consumers (including Framework x64/x86), native assets/notices and public CI still require their own acceptance. Historical/component passes do not imply a current release or maintainer approval. See [validation results](../strands-dotnet/docs/VALIDATION_RESULTS.md).

## Development

```shell
dotnet test harness-dotnet/tests/Strands.Harness.Tests/Strands.Harness.Tests.csproj -f net10.0
dotnet build harness-dotnet/src/Strands.Harness/Strands.Harness.csproj
# Pack only after the complete Monty native package/feed gate has passed; see the SDK build instructions.
```

Tests use deterministic models, injected filesystem doubles and real local shell processes. They require no model credentials or external services. Run both modern targets when their runtimes are installed.

The dedicated `strands-dotnet/tests/Strands.Compatibility.Tests` project exercises the API on .NET Framework 4.8, .NET 8 and .NET 10, with the Framework tests consuming the projects' .NET Standard 2.0 assets. Running `net48` requires Windows with a compatible Framework runtime. Independent NuGet consumption is a separate script-backed check.

Validation is tied to exact source checkpoints; see [current evidence and retained failures](../strands-dotnet/docs/VALIDATION_RESULTS.md).
