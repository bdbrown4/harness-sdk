# .NET feature parity

## Scope and current acceptance

This contribution provides a native C# SDK and assembled harness against the inclusive feature set of the Python and TypeScript implementations at [`3bd98e618320f0f286acbfb2252f72388030421b`](https://github.com/strands-agents/harness-sdk/tree/3bd98e618320f0f286acbfb2252f72388030421b). Parity means that a .NET application can perform the same useful operations through supported native APIs, with the required data, protocol interoperability, cancellation and resource guarantees.

The implementation uses ordinary .NET composition. Private algorithms, schedulers, class layouts, foreign runtime objects and incidental callback order are not completion criteria. Meaningful source differences already exposed as native options keep their documented defaults. The historical [export inventory](EXPORT_INVENTORY.json) and [symbol inventory](EXPORT_SYMBOL_INVENTORY.json) locate baseline sources; their old absence labels and member counts do not measure current feature completion.

The checklist covers fourteen native capability families and twenty-eight accepted feature outcomes. Current source checks pass on Windows, Linux and both macOS architectures, and the final thirteen-family feed passes fresh .NET 8/10 published consumers on all four platforms. The authored genuine Framework 4.8 pipeline passes all applicable source suites and all thirteen package families on x64 and x86, plus the example. [Validation results](VALIDATION_RESULTS.md) distinguish exact source, package and runtime profiles, retained failed attempts and the scope of the executed workflows. Maintainer adoption, official support, publication and the repository's [public API review](../../team/API_BAR_RAISING.md) remain separate decisions.

## Completed feature checklist

Each outcome applies to the supported native profiles documented below. Representative behavior tests are linked for review; the full source, runtime, package and consumer evidence is recorded separately in [validation results](VALIDATION_RESULTS.md).

### F01 — Ordinary agent conversations and rich streaming

- [x] **F01.1** Invoke/stream a text and rich-content model conversation and return detached results/usage. [Behavior test](../tests/Strands.Agents.Tests/AgentTests.cs#L93).
- [x] **F01.2** Handle malformed/truncated provider output and early stream disposal without leaking an invocation. [Behavior test](../tests/Strands.Agents.Tests/RuntimeContractTests.cs#L314).

### F02 — Tools, schemas and native extensibility

- [x] **F02.1** Register application tools/plugins and complete parallel or sequential tool/model turns with correlated results. [Behavior test](../tests/Strands.Agents.Tests/AttributedPluginTests.cs#L6).
- [x] **F02.2** Reject invalid input before side effects and preserve complete exchanges under exceptions/cancellation. [Behavior test](../tests/Strands.Bidi.Tests/BidiAgentTests.cs#L54).

### F03 — Human approval, steering, goal and stop control

- [x] **F03.1** Pause for approval, resume with an answer, apply steering/goal evaluation and request immediate or deferred stop. [Behavior test](../tests/Strands.Agents.Tests/AgentInterruptTests.cs#L23).
- [x] **F03.2** Preserve denied/pending effects and physically join owned work when cancellation or callbacks fail. [Behavior test](../tests/Strands.Agents.Tests/AgentInterruptTests.cs#L234).

### F04 — Durable sessions, storage and recovery

- [x] **F04.1** Save/reopen a native conversation, application state and pending human work through a supported store/repository. [Behavior test](../tests/Strands.Agents.Tests/SessionRepositories/RepositoryJournalFileTests.cs#L10).
- [x] **F04.2** Recover an accepted-write/save failure without replaying completed effects; reject corrupt or unsupported state clearly. [Behavior test](../tests/Strands.Agents.Tests/SessionRepositories/RepositoryJournalInterruptTests.cs#L10).

### F05 — Context, memory and skills

- [x] **F05.1** Reduce/offload long history, retrieve original content, extract/recall named memory and activate a skill. [Behavior test](../tests/Strands.Agents.Tests/Context/ContextOffloadIntegrationTests.cs#L42).
- [x] **F05.2** Preserve valid tool exchanges and child read-only memory authority through a supported restore path. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/SubagentTests.cs#L79).

### F06 — Supported model-provider adapters and credentials

- [x] **F06.1** Use each documented provider through its native model contract, including tools, structured results and advertised media/configuration. [Behavior test](../tests/Strands.Agents.Tests/Models/ProviderTests.cs#L350).
- [x] **F06.2** Preserve caller credentials/clients, classify provider failures and cancel/dispose a real protocol fixture safely. [Behavior test](../tests/Strands.Agents.Tests/Models/ProviderCredentialTests.cs#L13).

### F07 — MCP external tool integration

- [x] **F07.1** Discover and invoke real HTTP/stdio tools with documented authentication/task profiles and child-server grants. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/McpAuthenticationHarnessTests.cs#L101).
- [x] **F07.2** Handle notifications, server errors and task cancellation while joining owned connections/processes. [Behavior test](../tests/Strands.Agents.Tests/Mcp/McpTaskNotificationTests.cs#L17).

### F08 — Graph, Swarm and restricted child delegation

- [x] **F08.1** Execute a graph/team or delegated child and return correlated outputs with selected tool/context authority. [Behavior test](../tests/Strands.Agents.Tests/MultiAgent/GraphTests.cs#L337).
- [x] **F08.2** Resume paused native child/orchestration work without replaying committed effects or widening grants. [Behavior test](../tests/Strands.Agents.Tests/MultiAgent/GraphCheckpointTests.cs#L313).

### F09 — A2A invocation, hosting, durable tasks and push

- [x] **F09.1** Invoke JSON-RPC/SSE and REST-only peers; host task operations and opt-in push notifications. [Behavior test](../tests/Strands.Agents.Tests/A2A/A2ARestClientTests.cs#L14).
- [x] **F09.2** Preserve protocol data, cancellation, borrowed HTTP and bounded configuration/task lifetime through failure. [Behavior test](../tests/Strands.Agents.Tests/A2A/A2ARestClientTests.cs#L147).

### F10 — Realtime text/audio/tool model conversations

- [x] **F10.1** Use OpenAI, Gemini and Nova bidirectional models for text/PCM/tool/usage and reconnection. [Behavior test](../tests/Strands.Bidi.Tests/OpenAIRealtimeTests.cs#L32).
- [x] **F10.2** Preserve pending calls, fence superseded sessions and join transport on stop/caller cancellation. [Behavior test](../tests/Strands.Bedrock.Tests/BedrockNovaSonicFeatureTests.cs#L443).

### F11 — Ready console, asynchronous input and audio I/O

- [x] **F11.1** Run a native console/async-input conversation and optional capture/playback with echo/noise/gain processing. [Behavior test](../tests/Strands.Bidi.Tests/BidiConsoleFeatureTests.cs#L13).
- [x] **F11.2** Treat EOF normally, await enumerator/output cleanup, expose device callback/overflow errors and preserve borrowed resources. [Behavior test](../tests/Strands.Bidi.Tests/BidiConsoleFeatureTests.cs#L193).

### F12 — Harness construction, configuration and defaults

- [x] **F12.1** Create the ordinary native Agent from typed/portable configuration with prompt, default tools, todos and selected environment. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/HarnessTests.cs#L62).
- [x] **F12.2** Keep supplied-model/resource identity, resolve explicit overrides predictably and reject unsupported configuration before side effects. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/HarnessTests.cs#L102).

### F13 — Filesystem, shell, web and programmatic capabilities

- [x] **F13.1** Read/edit media/files, run shell/Python/persistent Bash, fetch/search/HTTP and call real bounded Monty tools in the selected environment. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/HarnessTests.cs#L296).
- [x] **F13.2** Respect sandbox/capability boundaries, cancellation/output budgets and declared external interpreter/native prerequisites. [Behavior test](../../harness-dotnet/tests/Strands.Harness.Tests/FileToolsTests.cs#L30).

### F14 — Logging, tracing and metrics

- [x] **F14.1** Observe agent/provider/tool/orchestration/realtime operations through standard ILogger/Activity/OpenTelemetry paths. [Behavior test](../tests/Strands.Agents.Tests/Logging/SdkLoggingTests.cs#L167).
- [x] **F14.2** Retain correct ownership, parentage, usage and terminal cleanup when work fails or is cancelled. [Behavior test](../tests/Strands.Agents.Tests/Telemetry/AutomaticTelemetryBackgroundTests.cs#L11).

## Targets and native dependencies

Managed libraries target `netstandard2.0;net8.0;net10.0`. Modern .NET targets Windows, Linux and macOS; existing .NET Framework 4.8 applications on Windows consume the .NET Standard 2.0 asset. There is no target named ".NET 4.8 Standard." Consumers need a modern C# compiler for public `required`/`init` options; Framework HTTP projects also need their ordinary `System.Net.Http` reference and binding redirects.

Native components have separate RID and deployment requirements. Monty and Cedar require their genuine advertised worker assets and notices. Device audio uses PortAudio and platform audio prerequisites. The Nova Framework transport uses static-CRT Windows x86/x64 AWS CRT assets. Its shared-model protocol and fresh-package ABI checks have run on genuine Framework 4.8 on both architectures; these bounded checks do not qualify the complete solution or a live AWS call. Modern Nova construction uses the official AWS client on supported modern operating systems. See the package-specific documentation and [build instructions](../README.md#build-from-source).

## Agent execution and control

Applications can invoke or stream typed and multimodal conversations, execute model/tool turns, obtain structured results, apply limits and cancellation, and pause/resume human decisions. Snapshots and execution checkpoints retain supported native conversation state. Tool declarations support delegates, attributes, JSON schema, rich and streamed results, registries, providers and custom batch scheduling. Awaited hooks, plugins and middleware provide native control points; background tasks, Goals, Decisions and ready-made user handoff/Stop/Sleep helpers compose with the ordinary Agent.

Contracts: [typed media](MEDIA.md), [structured output](STRUCTURED_OUTPUT_SOURCE_PROFILES.md), [interrupts](API.md), [checkpoints](CHECKPOINT_EXECUTION.md), [tools](TOOL_REGISTRY.md), [providers](TOOL_PROVIDERS.md), [hooks](HOOK_EVENT_PARITY.md), [background tasks](BACKGROUND_TASKS.md), [Goals](GOALS.md), [cancellation](AGENT_CANCELLATION.md).

## Model providers

Native adapters include OpenAI Chat Completions and Responses, Anthropic Messages, Bedrock Converse/Mantle, Google GenerateContent/Vertex, Ollama, Mistral, Writer, Llama API, llama.cpp and SageMaker. Provider packages expose supported credentials, configuration, rich content, tool calls, usage, streaming and structured results. Retry/routing and `IModel` allow native composition. The optional Google credential bridge uses the official authentication library, including application-default credentials through its supported composition.

Provider wire formats, supported content and documented error/ownership behavior are interoperability contracts. Importing Vercel or LiteLLM runtime objects is not a native .NET requirement; a missing concrete provider route must be identified by an unavailable application operation. Live-service qualification is separate from deterministic HTTP/official-SDK fixtures. See [provider APIs](API.md), [Bedrock](../src/Strands.Agents.Bedrock/README.md), [Google authentication](GOOGLE_AUTH.md), [Vertex](GOOGLE_VERTEX_AI.md), [llama.cpp options](../src/Strands.Agents/Models/LlamaCppModelOptions.cs) and [SageMaker](../src/Strands.Agents.SageMaker/DESIGN.md).

## Persistence, context and memory

Native snapshots, file sessions, snapshot catalogs, repository journals and deletion support durable Agent state. Graph, Swarm and Bidi have their own supported persistence/recovery contracts. Context reduction, offloading/retrieval, stash checkpoints, memory extraction/recall and file/test/Bedrock Knowledge Base stores are implemented. Optional S3 and SQLite/BM25/QMD packages supply storage/search capabilities.

Storage policies are explicit: the live indexed Agent journal currently rejects the first-class ContextManager and is not a live repository manager for Graph/Swarm/Bidi. Whole snapshots and the separate orchestrator stores remain supported alternatives. Cross-language snapshot files and arbitrary combinations of independent storage policies are not promised. A newly required recovery workflow must be specified and tested as that workflow.

Contracts: [sessions](API.md#sessions-and-context), [snapshot catalogs](SNAPSHOT_SESSION_CATALOG.md), [indexed repositories](INDEXED_SESSION_REPOSITORIES.md), [context](CLASSIC_CONVERSATION_MANAGERS.md), [memory stores](TEST_MEMORY_STORE.md), [S3](../src/Strands.Agents.S3/README.md), [SQLite/search](../src/Strands.Agents.Sqlite/README.md), [Knowledge Base](../src/Strands.Agents.BedrockKnowledgeBase/README.md).

## MCP, remote agents and orchestration

MCP supports official transports, authentication, discovery, tool-provider binding, resources/prompts, long-running tasks and human input, routing and tool rediscovery/publication. Task polling and notification-assisted completion preserve the native cancellation and ownership contract. The underlying supported protocol client remains available for advanced calls.

A2A provides client/server JSON-RPC, streaming, agent cards, task persistence and opt-in authorized push configuration/delivery. The REST client consumes HTTP+JSON-only cards using standard REST routes and protobuf JSON conversion, including streaming and push-configuration management. These wire/data contracts are required interoperability, independent of foreign transport class names.

Graph and Swarm compose agents with native session and interruption support. `AsTool` and the Harness delegation tools preserve supported child isolation and durable human-answer continuation. Contracts: [MCP tasks](MCP_TASKS.md), [MCP providers](MCP_TOOL_PROVIDERS.md), [A2A](MULTIAGENT_A2A.md), [Graph](MULTIAGENT_GRAPH.md) and [Swarm](MULTIAGENT_SWARM.md), [agent tools](AGENT_TOOLS.md), [subagent interrupts](../../harness-dotnet/docs/SUBAGENT_INTERRUPTS.md).

## Realtime conversations and observability

OpenAI Realtime, Google Gemini Live and Bedrock Nova Sonic supply native full-duplex text/audio/tool conversations. Bidi handles tools, interruptions, lifecycle, retained-context reconnect and supported persistence. Console channels and asynchronous inputs are included. The optional [device audio package](../src/Strands.Agents.Bidi.Audio/README.md) supplies capture/playback and managed echo, noise and gain processing. Audio hardware availability and actual device behavior require separate qualification from native loading and deterministic processing tests.

`ActivitySource`, `Meter` and the OpenTelemetry add-on expose agent/model/tool/memory/orchestration activity with privacy controls. Bidi traces session/connect/response/tool lifetimes and provider usage through native activities. These capabilities do not promise identical private tracing classes or metrics absent from the baseline implementation. See [Bidi](../src/Strands.Agents.Bidi/README.md), [telemetry](TELEMETRY.md) and [OpenTelemetry](../src/Strands.Agents.OpenTelemetry/README.md).

## Harness composition

`Strands.Harness` builds an ordinary core Agent. It combines provider resolution, instructions, built-in tools, selected sandboxes, sessions, memory/context, skills, MCP, delegation, background work, interventions and tracing. Configuration is native C#/portable JSON with explicit executable bindings. Trusted compiled C# modules and watched managed tools are supported; running Python/TypeScript modules inside the .NET process is not supplied.

The local sandbox uses host-process permissions; root confinement and remote isolation require a suitable selected sandbox and policy. Shell and programmatic execution require their actual execution capabilities and native dependencies. See the [Harness README](../../harness-dotnet/README.md), [configuration](../../harness-dotnet/README.md#portable-configuration) and [watched tools](../../harness-dotnet/docs/WATCHED_MANAGED_TOOLS.md).

<a id="interventions-and-authorization--partial"></a>

### Interventions and authorization

Native intervention presets support steering, authorization, retries and human decisions. `GuideFeedbackDelivery.Native` remains the default; explicit `DirectAppend` adds accepted feedback to requests/history without a feedback `MessageAdded` event or immediate session save. Finalization still persists accepted feedback, including provider failure. Explicit policy/configuration authority remains necessary for executable tools. See [intervention API](API.md) and [intervention contracts](API.md).

<a id="multi-agent-orchestration-and-bidirectional-models--partial"></a>

### Multi-agent and realtime compatibility

The sections above replace older blanket Partial/Absent labels. Different persistence and ownership policies remain documented native contracts. Focused Graph/Swarm/Bidi tests and historical platform checkpoints do not automatically qualify a later combined assembly or every nested workflow.

## Remaining acceptance

The agreed feature checklist is complete: twenty-eight accepted outcomes of twenty-eight (100%). Feature acceptance is independent of the seven release gates. Concrete interoperability failures and unavailable supported workflows remain actionable.

Current Windows/Linux affected complete suites and both macOS source profiles pass for their recorded scopes. All thirteen normal package/symbol pairs pass unsuppressed package/API, native/notices and exact-feed modern consumer checks on Windows, Linux, Intel Mac and Apple Silicon. The authored genuine Framework 4.8 workflow passes both architectures, all ten applicable suites and all thirteen families' consumers. The validation record distinguishes those executed workflows from the unpublished contribution's whole public merge graph. Final contribution export has its separate forward/reverse integrity check.

The bounded engineering API/binary-linkage review retains its documented target-asset and consumer evidence. No previous released .NET baseline was supplied. Maintainer public API review, maintenance ownership, official support and publication remain separate decisions. Live-provider and physical audio-device qualification use explicitly configured opt-in profiles. [Validation results](VALIDATION_RESULTS.md) preserve exact counts, source/feed identities, runtime qualifications and retained failures.
