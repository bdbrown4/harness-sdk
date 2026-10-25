# Strands Agents for .NET

A native C# SDK and assembled harness contribution for modern .NET and existing .NET Framework 4.8 applications. `Strands.Agents` supplies agent primitives; `Strands.Harness` composes them into an ordinary Agent. Managed libraries target .NET Standard 2.0, .NET 8 and .NET 10. Modern .NET targets Windows/Linux/macOS; Framework execution is Windows-only, and native packages have their own RID requirements.

The implementation includes model providers, typed/structured streaming, tools/hooks/middleware, human interruption, context/memory/sessions, MCP, Graph/Swarm/A2A, realtime conversations and observability. [Feature scope](docs/PARITY.md) records the pinned Python/TypeScript baseline, supported native contracts and release gates. Idiomatic C# implementations may use different algorithms and private structures.

This is an alpha contribution proposal. [Recorded validation](docs/VALIDATION_RESULTS.md) identifies tested source, package and platform profiles, genuine Framework execution, retained failures and any open release checks. Maintainer adoption, public API approval, official maintenance ownership and publication are separate decisions.

## Build from source

The libraries target `netstandard2.0;net8.0;net10.0`. .NET Framework 4.8 applications consume the `netstandard2.0` asset; there is no target named ".NET 4.8 Standard." See Microsoft's [.NET Standard compatibility documentation](https://learn.microsoft.com/en-us/dotnet/standard/net-standard). Async streaming uses the compatible `Microsoft.Bcl.AsyncInterfaces` support package where required.

Source builds require the .NET 10 SDK selected by `global.json`. Install .NET 8 and 10 runtimes for modern tests. To build, test and pack the managed core from this directory:

```sh
dotnet restore src/Strands.Agents/Strands.Agents.csproj
dotnet build src/Strands.Agents/Strands.Agents.csproj --configuration Release --no-restore
dotnet test tests/Strands.Agents.Tests/Strands.Agents.Tests.csproj --configuration Release --framework net8.0
dotnet test tests/Strands.Agents.Tests/Strands.Agents.Tests.csproj --configuration Release --framework net10.0
dotnet pack src/Strands.Agents/Strands.Agents.csproj --configuration Release --no-restore --output artifacts/packages
```

The full solution also builds the harness and optional provider/add-on projects. Running Monty or harness tests requires the genuine worker for that process RID. Packing Monty, and producing a usable harness feed that depends on it, requires all five real worker assets plus generated notices; the package gate deliberately rejects incomplete sets. See [the Monty build/validation contract](src/Strands.Agents.Monty/README.md). Cedar has its own optional solution and native asset gate. Framework tests require Windows and the appropriate native workers; [the validation plan](docs/VALIDATION.md) describes actual x64/x86 assertions. Reference assemblies alone establish compilation compatibility.

The repository contains the core, Bedrock and SageMaker providers, optional S3 byte storage, Bedrock Knowledge Base retrieval/CUSTOM/S3 ingestion and SQLite FTS5/BM25/QMD keyword search, Bidi and OpenTelemetry add-ons, the native Cedar and Monty packages, and the assembled harness. Packages contain .NET Standard 2.0, .NET 8 and .NET 10 assets and XML documentation; these commands do not publish them.

After assembling a complete feed containing the harness and all of its dependencies, the external harness consumer can be checked with PowerShell 7:

```sh
pwsh -File scripts/Test-Packages.ps1 -PackageDirectory artifacts/packages
```

This script requires exactly one harness package, restores into a fresh isolated cache and checks its offline agent/tool cycle. It does not manufacture missing native assets or turn a partial feed into a complete package. S3, SageMaker, Cedar, Bidi and OpenTelemetry also have family-specific fresh-consumer validators. Keep package-consumer results separate from source-project tests. The newer optional [S3 package](src/Strands.Agents.S3/README.md) has separate selected tests, three-asset/API checks and Windows/Linux fresh-consumer evidence.

## Consumer compiler and Framework projects

Consumers need a C# 11 or newer compiler for public options containing `required`/`init` members. Repository examples use C# 12 and the configured modern SDK. Runtime compatibility with Framework 4.8 does not imply compatibility with its original C# compiler. `await foreach` requires C# 8 or newer; `InvokeAsync` provides the ordinary `Task<AgentResult>` path for callers that do not need async-stream syntax.

SDK-style `net48` applications constructing provider clients should reference `System.Net.Http` and enable binding redirect generation. The [example project](examples/GettingStarted/GettingStarted.csproj) demonstrates:

```xml
<PropertyGroup>
  <LangVersion>12.0</LangVersion>
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <Reference Include="System.Net.Http" />
</ItemGroup>
```

## Run the example

The [getting-started example](examples/GettingStarted/Program.cs) runs an agent/tool cycle without credentials or network access:

```sh
dotnet run --project examples/GettingStarted/GettingStarted.csproj --configuration Release --framework net10.0 --no-build -- --demo
```

It invokes `add` and returns `2 + 3 = 5.` in two model turns. On Windows, select `--framework net48` to exercise the Framework build. For live requests, set `STRANDS_MODEL_ID` and either `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`, then use `--openai` or `--anthropic` instead of `--demo`. Live requests use the configured provider and require separate validation. The example targets `net48;net8.0;net10.0` through project references; it is distinct from external packed-package consumption.

## Core agent

Models implement `IModel`; every invocation uses native async code and supports a `CancellationToken`:

```csharp
using Strands.Agents;

static async Task<AgentResult> AskAsync(IModel model, CancellationToken cancellationToken)
{
    var agent = new Agent(new AgentOptions
    {
        Model = model,
        SystemPrompt = "Explain your answer briefly.",
        MaxTurns = 8,
    });

    return await agent.InvokeAsync("What makes an agent tool useful?", cancellationToken: cancellationToken);
}
```

`StreamAsync` exposes model updates, committed messages, and a terminal `AgentResultEvent`. The core also exposes schema-validated tools and typed structured output, rich content, awaited typed hooks, plugins, application state, bounded tool execution, token and turn limits, session storage, sliding-window history, and agent-as-tool composition. Retry and routing models wrap ordinary model implementations. These capabilities are part of the alpha surface; their boundaries and remaining upstream differences are in the ledger.

## Provider and transport ownership

OpenAI Chat Completions and Anthropic Messages adapters use caller-owned `HttpClient` instances. Explicit options supply model ID, API key, endpoint overrides and provider settings. Both support tool choice and applicable rich content; Anthropic supports signed and redacted reasoning replay and explicit cache points. Unsupported provider content fails explicitly. Models do not retry automatically; compose `RetryModel` when appropriate.

The separate [Bedrock adapter](src/Strands.Agents.Bedrock/README.md) uses the official AWS SDK for credentials, signing and native Converse streaming. A supplied AWS client stays caller-owned; the convenience constructor owns a lazily created client. Core `Agent` construction still requires an explicit model. The assembled harness can create its default Bedrock model and transfers that model's lifetime to the returned agent.

The MCP client uses the official C# protocol SDK for HTTP/stdio discovery, tools and typed rich results. Implemented subsets also include negotiated/explicit legacy and finalized task profiles, client-credentials discovery and authenticated replay, harness server configuration and lazy router tools. Their protocol, deadline and authority limits are documented in [tasks](docs/MCP_TASKS.md), [authentication](docs/MCP_AUTHENTICATION.md) and [routing](docs/MCP_ROUTER.md). Dispose owned connections when no longer needed; supplied transports remain borrowed unless ownership is explicitly transferred. Exhaustive protocol/content behavior and cross-feature combinations remain separate work.

Native adapters include OpenAI Chat Completions and Responses, Anthropic Messages, Google GenerateContent, Ollama, Mistral, Writer, llama.cpp and the deprecated pinned Llama API, with official AWS SDK packages for Bedrock and SageMaker. Provider-specific options, supported content and lifetime contracts differ. Native `IModel` composition supports additional provider routes; feature parity is assessed through concrete application capabilities and wire interoperability. Scripted models, genuine protocol frames and loopback transports establish deterministic behavior, not live-service interoperability. See [provider scope](docs/PARITY.md#model-providers) and [the API proposal](docs/API.md).

## Operational contracts

Use a separate agent instance for each concurrent conversation. A second active invocation on the same instance raises `ConcurrentInvocationException`. Per-call `InvocationState` is caller-owned and is visible to tools and hooks. Cancelling the invocation token ordinarily returns a terminal result with `StopReasons.Cancelled`; disposing a stream repairs pending tool requests and releases the invocation. Primary errors retain secondary cleanup failures in `Exception.Data["Strands.CleanupErrors"]`.

The local session snapshot format is specific to this .NET alpha and is not interoperable with Python or TypeScript sessions. Version `1` validates tool correlation; a final pending batch is recoverable. File replacement uses last-writer-wins semantics, so applications sharing a session need their own lease. The optional sliding-window manager reduces model-visible history while retaining the complete user exchange; that exchange can exceed the configured message count. Explicit retained-history snapshots carry `SessionSnapshot.HistoryProfile`; independently owned pending batches expose `InterruptState.AssistantMessageIsIndependent`. Their Native/false defaults are omitted from JSON. See the [local history and admission contract](docs/API.md#sessions-and-context).

The optional [indexed Agent journal](docs/REPOSITORY_AGENT_SESSIONS.md) binds a borrowed `ISessionRepository` through `RepositorySessionManager`. Explicit append/redaction receipts, confirmed numeric frontiers, stable failed-write retries and validated stateless repair support its documented native restore path. Reference-preserving suffix reduction and repeated summaries are supported; unsupported pinned/non-prefix/rich rewrites fail before committing history or reducer state. Stateful models, the first-class `ContextManager`, a competing snapshot store and message-replacing public snapshot loads conflict with this profile. Multi-record writes are not transactional, and archived originals remain available after redaction.

`Strands.Agents.Context.ToolResultOffloader` retains its separate opt-in projection and retrieval contract. The first-class `ContextManager` supplies automatic/agentic reduction, ordered strategies, summaries, overflow recovery and original-content stash retrieval; the harness enables its automatic profile by default. Native sessions persist the supported stash checkpoint. Python classic manager algorithms and durable state are available separately under `Strands.Agents.Conversation`, without changing the older root-namespace projection API. These surfaces have distinct lifecycle/storage semantics; see [context scope](docs/PARITY.md), [stash checkpoints](docs/CONTEXT_STASH_CHECKPOINTS.md) and [classic managers](docs/CLASSIC_CONVERSATION_MANAGERS.md). Cross-language checkpoint interchange and exhaustive provider/context integration remain open.

Hooks run by ascending priority; equal-priority after hooks reverse registration order. Agents own their plugin registrations and compose them with application hooks. The legacy string-argument `AsTool` overload creates fresh children. `AsTool(AgentToolOptions)` instead offers explicit reset/preserve-context and delegation behavior, including its supported durable nested human-input bridge. Neither overload implicitly transfers borrowed dependency ownership. [Agent-tool documentation](docs/AGENT_TOOLS.md) describes the separate schemas, session restrictions and lifecycle contracts. Shared hooks, tools and models must be safe for their intended concurrency.

Graph and Swarm also accept ordered borrowed `MultiAgentPlugin` declarations. Their initialization is awaited once per receiving orchestrator. Native session restoration remains before plugins; explicit `SessionRestoreProfile` values select the TypeScript/Python callback phases. `SwarmBaselineCapturePhase.AfterPlugins` separately selects the Python construction-time baseline phase. These choices do not transfer plugin or agent ownership. The explicit per-node `ResetToConstructionBaselineOnRevisit = 3` graph policy captures messages, application state and model state at `GraphBuilder.AddNode(Agent, ...)`, then restores them before the node hook on completed revisits. See the [API contracts](docs/API.md#named-orchestration-plugins-and-session-phases) for admission and remaining limits.

[Generic tool providers](docs/TOOL_PROVIDERS.md) are borrowed declarations in `AgentOptions.ToolProviders`. Each agent gets an independent consumer context; admitted tool metadata/schema is detached for that binding while the actual rich/streaming tool objects remain borrowed. Initialization rollback and shutdown remove every tracked consumer without transferring provider ownership. The explicit `McpToolProvider` adapter now supplies shared owned connection generations, and the harness admits declarative MCP providers through that lifecycle. Optional [live tool publication](docs/LIVE_TOOL_PROVIDER_UPDATES.md) and [MCP rediscovery](docs/MCP_TOOL_CHANGES.md) have explicit refresh/binding contracts; they do not imply arbitrary dependency unloading.

Dispose agents with `await using` when finished. Supplied models stay caller-owned unless `AgentOptions.OwnsModel` explicitly transfers ownership; child agents do not dispose inherited models. Finish initialization and dispose active invocation streams before disposing an agent. Application `State`/`AppState` is JSON-backed, persisted with native sessions and excluded from model context. Per-invocation state remains caller-owned.

The harness's `LocalFileSandbox` reads and writes absolute paths with host-process permissions. It provides no root confinement, isolation, or authorization policy. Writes stage and flush a sibling file before replacement, and reject terminal link/reparse destinations; parent-directory links are still followed, and checks do not prevent concurrent filesystem races. It also implements the explicitly selectable `ICommandSandbox` shell capability, with deadlines, bounded capture and process cleanup. Default harness built-ins include shell; a custom file-only sandbox must disable it. See the [harness README](../harness-dotnet/README.md) for access policies, shell/platform limits, file metadata behavior, and default sessions.

## Contributing

Read the [public API proposal](docs/API.md), [development guide](AGENTS.md), [validation plan](docs/VALIDATION.md), [recorded results](docs/VALIDATION_RESULTS.md), and [draft RFC](../team/designs/0021-dotnet-sdk.md). Significant public API additions need the repository's design and [API bar-raising](../team/API_BAR_RAISING.md) process before merge. Library XML documentation generation is enabled; an automated public API compatibility baseline/freeze remains pending.

Experimental [execution checkpoints](docs/CHECKPOINT_EXECUTION.md) support explicit TypeScript regeneration or Python pending-tool replay, with Disabled as the default. Conversation/provider state remains separate from the checkpoint marker. The current composed platform/package gates remain recorded separately in [validation results](docs/VALIDATION_RESULTS.md).
