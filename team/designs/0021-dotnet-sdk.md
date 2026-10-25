# Native .NET SDK and harness

**Date**: 2026-10-20

**Status**: Draft proposal; maintainer alignment and API bar raising pending

**Baseline**: `3bd98e618320f0f286acbfb2252f72388030421b`

## Overview

Introduce a native C# core in `Strands.Agents` with a separate `Strands.Harness` composition package. The thirteen public package families are Core, Bedrock, SageMaker, Bidi, Audio, Cedar, OpenTelemetry, Monty, S3, SQLite, Knowledge Base, Google authentication and Harness. The proposed version remains `0.1.0-alpha.1`, targeting `netstandard2.0;net8.0;net10.0`; Framework 4.8 applications select the .NET Standard asset. The accepted feature scope is the inclusive Python/TypeScript union expressed through fourteen native capability families and twenty-eight component outcomes. Their implementation and component acceptance follow the documented native contracts, rather than foreign private algorithms or runtime objects. The complete thirteen-family release feed includes genuine native workers for its advertised RIDs, and fresh modern consumers have passed on Windows, Linux and both macOS architectures. **Feature implementation and recorded engineering validation are complete; maintainer adoption and publication remain open.** Maintenance ownership, design/API approval and publication remain maintainer decisions. [The feature scope](../../strands-dotnet/docs/PARITY.md#scope-and-current-acceptance) records supported capabilities and explicit policy limits.

The platform scope is modern .NET on Windows, Linux, and macOS, plus existing .NET Framework 4.8 applications on Windows.

Current Windows/Linux affected complete suites, full Intel/Apple Silicon source profiles and fresh .NET 8/10 published consumers for all thirteen families pass. The authored genuine Framework 4.8 workflow also passes all applicable x64/x86 source suites, all thirteen families' consumers and the example. [Recorded results](../../strands-dotnet/docs/VALIDATION_RESULTS.md) identify exact source/package bindings, runtime qualifications, executed workflow scope and retained failed attempts.

## Problem

.NET applications cannot currently consume a native Strands agent loop through this repository's supported SDKs. Calling a Python or Node.js process adds a deployment boundary and requires translating tool callbacks, streaming, cancellation, and application state across that boundary. Developers also need familiar integration with C# async code, existing `HttpClient` configuration, and NuGet dependency management.

### Current State

The Python and TypeScript harnesses are composition layers over their respective SDKs. They resolve models and configure tools, prompts, context, persistence, and other features before returning a regular `Agent`. A C# factory alone cannot reproduce that behavior without native model, tool, hook, session, and loop primitives.

The Python and TypeScript implementations contain significantly more than a text/tool cycle. The reviewed native scope covers conversation execution, tools and schema, control/interventions, persistence, context/memory/skills, model providers, MCP, Graph/Swarm/delegation, A2A, realtime conversations, ready console/audio I/O, Harness composition, filesystem/shell/web/Monty and observability. The fourteen-family/twenty-eight-outcome checklist describes useful application behavior and interoperability. The native Vertex-authenticated Gemini Live route and Bash lock-order correction are integrated and pass their complete component profiles. All twenty-eight feature outcomes, current modern source/package checks and the authored genuine Framework x64/x86 workflow are accepted for their recorded scopes. Native-asset and package-consumer results retain their recorded source/feed bindings; maintainer adoption is a separate decision. A newly identified unavailable supported workflow remains actionable; foreign class/module shape or incidental internal ordering does not create a missing native feature.

## Proposal

### Recommended: Native primitives with a separate harness

Place the core in `strands-dotnet/` and harness composition in `harness-dotnet/`, built by `strands-dotnet/Strands.slnx`. Export shallow `Strands.Agents` and `Strands.Harness` namespaces. Core extension points include `IModel`, `ITool`/`IRichTool`, `IPlugin`, `ISessionStore`, and `IConversationManager`; models yield indexed stream events, and an `Agent` owns the turn/tool lifecycle. Domain records represent messages, tool requests/results, usage, and terminal outcomes. `InvokeAsync` and `StreamAsync` share execution behavior. Async hooks participate in the lifecycle rather than running as detached callbacks. Plugins use per-agent JSON application state and initialize after session restoration.

Use .NET's async streams, cancellation, JSON, HTTP, and filesystem primitives directly. JSON Schema validation uses an existing library. This applies the composition lesson from [Scott Hanselman's abstraction essay](https://www.hanselman.com/blog/we-are-abstracting-on-the-shoulders-of-giants): build on established components. That source informs this proposal; it is not an endorsement of these API choices. Interfaces exist where callers need substitution, rather than around every concrete class.

Two concrete reuse points follow that rule: OpenAI Chat and Mistral share the validated Chat event decoder while retaining separate wire formatting and provider policy; agent and Mistral structured output share schema/typed-value parsing while keeping tool execution and provider extraction separate. These are internal implementations serving existing consumers, without a new public abstraction over `HttpClient`, JSON or async streams. Mantle likewise uses the official AWS signer and credential chain rather than introducing a separate cryptographic implementation.

Keep the public async contracts consistent across library assets. `Microsoft.Bcl.AsyncInterfaces` and compatible JSON/async dependencies support the .NET Standard path. Source still builds with the modern SDK and C# language version; consuming from Framework 4.8 does not require building the repository with an old compiler. There is no ".NET 4.8 Standard" target. `net48` test/consumer projects use private reference-assembly packages for compilation and execute on Windows.

Scott Hanselman's [open-source .NET library guidance](https://www.hanselman.com/blog/new-prescriptive-guidance-for-open-source-net-library-authors) explains portable reuse and multi-targeting for platform-specific APIs, alongside dependency, NuGet, SourceLink and versioning concerns. That rationale informs the shared `netstandard2.0` surface and modern `net8.0`/`net10.0` assets. The internal Windows child-process adapter is a concrete boundary: legacy raw-handle startup stays behind the existing process contract, while modern assets use their BCL path. The exact targets and adapter implementation are native design choices, validated through API, lifetime, pipe and consumer tests; the article does not endorse this code or establish its SOLID properties.

Public required/init options set a C# 11+ consumer compiler floor; current examples use C# 12. `InvokeAsync` exposes a normal task-based path; async-stream syntax needs C# 8+. Framework HTTP consumers reference `System.Net.Http` and enable SDK-style binding redirects. Framework runtime compatibility is an explicit goal; compatibility with its original C# compiler is not proposed.

Native adapters cover OpenAI Chat Completions/Responses, Anthropic Messages, Google GenerateContent/Vertex, Ollama, Mistral, Writer, llama.cpp and the deprecated pinned Llama API; optional Bedrock and SageMaker packages use official AWS components. Bidi supplies OpenAI Realtime, Gemini Live and Nova Sonic, with the documented Windows Framework transport where required. Borrowed HTTP/AWS clients remain caller-owned, and convenience constructors own only their created clients. Stateful Responses history uses accepted provider response receipts. Provider wire/content policies remain in adapters and the agent loop remains provider-neutral. Protocol fixtures and localhost/native checks do not establish live-service interoperability. Python LiteLLM and TypeScript Vercel runtime objects are not imported into .NET; equivalent application operations use native adapters and `IModel`. Any additional provider requirement must identify a concrete unavailable operation, not an in-process foreign wrapper name.

The harness returns the core `Agent` after composing the shared prompt, instructions, native sessions, selected sandbox shell/file tools, automatic context, file memory, progressive skills, todos/environment and bounded delegation. It accepts an explicit model or constructs its lazy owned Bedrock default in `us-west-2`. Portable normalization and trusted managed bindings support native configuration without executing Python/TypeScript modules. Native capabilities include streaming tools, middleware, Graph/Swarm/A2A orchestration, realtime text/audio/tools, interventions, Monty worker execution, background work, telemetry and remote sandbox adapters. Selected filesystems, owned process pipes, the optional indexed Agent journal, borrowed tool-provider lifetimes, MCP delegation and durable subagent human-answer continuation retain their documented ownership and storage policies. [Recorded evidence](../../strands-dotnet/docs/VALIDATION_RESULTS.md) separates component behavior from platform/package/release acceptance.

**Pros:** Native deployment and cancellation; independently testable capabilities; preserves upstream core/harness composition; documented package boundaries.

**Cons:** A third implementation adds ongoing maintenance; provider dependencies and protocol changes need continuing qualification; native worker distribution and the broad platform matrix add release cost; public names, lifecycle contracts and target support need maintainer approval.

### Alternative: Bridge to Python or TypeScript

Host an existing SDK behind a process or service boundary and expose C# calls to it.

**Pros:** Reuses the mature agent implementation and provider coverage; reduces duplicated loop work.

**Cons:** Requires a second runtime and deployment model; remote callbacks complicate tools, hooks, cancellation, and exception ownership; a bridge protocol becomes a separate compatibility contract. This remains viable for service deployments, but does not satisfy in-process native .NET use cases as directly.

### Alternative: Adapt an existing .NET agent framework

Expose Strands concepts over another framework's loop and model contracts.

**Pros:** Reuses established .NET provider integrations and ecosystem extensions.

**Cons:** Couples Strands behavior and lifecycle guarantees to another framework; tool ordering, hook events, history repair, and session semantics may require translation or replacement. An optional adapter can be proposed later without making that framework the core contract.

## Developer Experience

Core construction requires an explicit model; the harness can supply its lazy Bedrock default. Custom models implement `IModel.StreamAsync(ModelRequest, CancellationToken)`; tools supply a `ToolSpec` and async invocation. A minimal core call is:

```csharp
using Strands.Agents;

static async Task<string> AskAsync(IModel model, CancellationToken cancellationToken)
{
    await using var agent = new Agent(new AgentOptions
    {
        Model = model,
        SystemPrompt = "Answer concisely.",
        MaxTurns = 8,
    });

    var result = await agent.InvokeAsync("Explain this repository.", cancellationToken: cancellationToken);
    return result.ToString();
}
```

The native core includes bounded tool execution, turn/token limits, cancellation cleanup, a single active invocation per instance, file-backed stateful sessions, conversation reduction, opt-in tool-result offloading/retrieval, retries/routing, structured output, and native MCP extension points. Their documented component acceptance does not close the separate release gates. Reusing an instance concurrently raises a specific exception. Default local file tools operate with host permissions and accept absolute paths; the `ISandbox` seam does not itself provide confinement or isolation. The [API proposal](../../strands-dotnet/docs/API.md) provides signatures, defaults, provider/harness examples, and behavioral contracts for the [API bar-raising process](../API_BAR_RAISING.md).

`Agent.DisposeAsync` releases explicitly owned models and agent-local hooks. Harness-created default models are owned; supplied models remain caller-owned unless `AgentOptions.OwnsModel` explicitly transfers ownership. Fresh children inherit construction policies and copied application state, disable sessions, and share the parent model without owning it. Application dependencies must support their intended concurrency.

`HarnessAgentConfig` validates portable camelCase settings, strict nested shapes, duplicate keys, built-in mappings and normalized module kinds. Unknown top-level keys are dropped with a sorted SDK warning; the default logging bridge preserves trace behavior. Native Bedrock identifiers resolve directly, and `HarnessConfigBindings` supplies explicit resolvers for other models and components. Trusted compiled managed exports and [watched managed tool reload](../../harness-dotnet/docs/WATCHED_MANAGED_TOOLS.md) are supported. Executing Python/TypeScript modules inside the .NET process and installing their dependencies are not supplied. Implemented context, memory, skills, caching, MCP, interventions and background settings can stay enabled in runnable native definitions. Unsupported values fail before executable bindings; feature-disable examples are application choices. Relative session paths use the selected project directory, and explicit core options retain precedence.

Todos and environment context are actual default plugins with ephemeral model-input reminders. Todo state persists with sessions; environment discovery uses the selected sandbox capability and a text-only sandbox can receive date context. Standard delegation has a generalist role, depth bounds, fresh child conversations and explicit context modes. Tool authority restricts application, plugin and provider-generated capabilities, and independent MCP-server grants are supported. Harness delegation forwards nonfinal child events and supports durable human-answer continuation while retaining granted authority and owned-child cleanup. Its supported stream, pause/resume and disposal boundaries are documented in [subagent streams and human input](../../harness-dotnet/docs/SUBAGENT_INTERRUPTS.md).

The native cancellation contract returns a cancelled terminal result for the supported invocation-token and explicit/deferred cancellation paths. Hooks have documented priority, token and cleanup scopes; primary exceptions retain secondary cleanup failures. Native whole-snapshot sessions validate tool correlation and use last-writer-wins replacement. Typed agent-as-tool options expose reset/preserve context and a bounded durable nested-input bridge; legacy string wrappers still use fresh children. The optional indexed journal instead uses accepted row receipts and a confirmed frontier, validates before restore, and fails closed on unsupported projection/stateful/first-class-context combinations. Its multi-record backend writes are nontransactional. These are explicit native contracts, not cross-language checkpoint compatibility.

`ToolResultOffloader` is an opt-in projection component: it shortens successful oversized text/JSON results and provides `retrieve_context` against unchanged durable history, preserving opaque blocks and skipping structured payloads. Its 1,500/750-token defaults and shared search fixtures are source-backed; the native per-match regex deadline and strict final text budget are explicit extensions. It does not implement automatic summarization, context-overflow recovery or separate stash reclamation. Full originals remain in memory and sessions.

A separate first-class `ContextManager` implements its documented automatic/agentic strategies, auxiliary summaries, stash retrieval and overflow handling. Python-style classic conversation managers live in `Strands.Agents.Conversation` and have their own durable state/lifecycle policy. These distinct surfaces preserve their own defaults; none supplies general foreign snapshot compatibility or complete indexed projection persistence.

Native shell is an explicit `ICommandSandbox` capability. The host sandbox uses fresh Windows PowerShell or modern Unix `/bin/sh` processes, deadlines, concurrent pipe draining and bounded UTF-8 capture. The .NET Standard legacy Windows path attaches a kill-on-close Job Object before a bounded handshake releases command code; modern process-tree cleanup retains platform/orphan limits. This provides host execution without process/filesystem isolation or authorization. Default built-ins include shell; file-only custom sandboxes must disable it.

## Additional Details

<details>
<summary>Validation, parity milestones, and review decisions</summary>

### Behavioral development

Follow [Kent Beck's Canon TDD](https://newsletter.kentbeck.com/p/canon-tdd): maintain a behavioral scenario list, turn one scenario into a runnable failing test, make it pass with the existing tests, and then refactor. The committed suite demonstrates behavior; test-first claims additionally require execution evidence. Scripted models and in-memory HTTP responses verify agent/tool cycles and wire behavior without credentials. Filesystem tests use temporary roots. Cancellation, partial stream disposal, hook failures, malformed streams, bounded concurrency, and session reloads need explicit cases.

The validation plan restores and builds every managed asset with warnings as errors, verifies formatting, runs modern tests/demo on the selected operating systems, and executes Framework tests on actual Windows architectures. All thirteen public package families are present in the native/NuGet feed, with genuine advertised assets and notices. Fresh isolated-cache published consumers have passed on Windows/Linux .NET 8/10 and both macOS architectures. Genuine Framework source-suite and package-consumer results retain their individual runtime and package bindings; the current authored Framework workflow has its own original runtime and consumer artifacts. The bounded engineering API/ABI review has accepted the documented target-asset and consumer evidence without claiming compatibility with an unavailable prior released .NET baseline.

The full Arm64 source gate passes alongside Intel, Windows and Linux. The integrated Bash and direct-streaming corrections and deterministic timing-fixture corrections retain their original failed evidence and focused checks. The authored genuine Framework workflow now passes on x64/x86. [Recorded results](../../strands-dotnet/docs/VALIDATION_RESULTS.md#test-first-corrections-and-retained-failed-attempts) retain earlier failures without transferring historical passes to current source. The unpublished contribution's whole public merge graph is not claimed as executed.

[The validation plan](../../strands-dotnet/docs/VALIDATION.md) defines the commands and [recorded results](../../strands-dotnet/docs/VALIDATION_RESULTS.md) bind their observed outcomes. Packing and local publish checks are not public package publication; protocol fixtures do not imply live-service validation.

Frozen checkpoints `7dc9caa`, `f161079` and `16339e1` retain source-specific platform/package results. The earlier `be391b6` Windows checkpoint retains its unresolved genuine Framework x64 failure. These are historical checkpoints, not the current source or feed. [Recorded evidence](../../strands-dotnet/docs/VALIDATION_RESULTS.md#retained-historical-checkpoints) preserves their exact outcomes and failed attempts separately from later corrections. The current modern source and exact-feed modern consumer gates pass; current genuine Framework CI and final engineering acceptance have their own recorded scopes.

Immutable historical checkpoint `ff35aac068affaf0672217488597948ca1203766` has retained [Linux/macOS verification](https://github.com/bdbrown4/strands-dotnet-verification-20261020/actions/runs/39503931306) and [genuine Framework 4.8 verification](https://github.com/bdbrown4/strands-dotnet-verification-20261020/actions/runs/39503930591). Its build, formatting, package and consumer outcomes apply only to that frozen source and the recorded host architectures. They do not validate later provider, Graph/Swarm, Harness, Cedar or native transport changes. Current live-service qualification, API review, maintenance ownership and release acceptance remain separate decisions.

The schema dependency is selected as `JsonSchema.Net` 7.3.4 (MIT). The metadata audit of 9.4.1 reported license acceptance requirements; this proposal uses the explicitly selected dependency version rather than treating package metadata as an implementation detail.

Experimental cycle-boundary execution now exposes explicit TypeScript regeneration and Python pending-tool replay, with Disabled as the default. Markers carry no conversation/provider state; SDK-owned fresh default subagent/Swarm builders disable inherited checkpoint policy. Bounded six-profile, Core package/API and unchanged-client checks are recorded separately from genuine4.8 and final combined platform/release acceptance. See [checkpoint execution](../../strands-dotnet/docs/CHECKPOINT_EXECUTION.md).

### Roadmap

1. Maintain the recorded platform, package and genuine Framework gates as the implementation evolves; retain earlier results with their exact source/package bindings.
2. Submit the reviewed contribution for maintainer design and API feedback, with its validation record and complete source export.
3. Keep live-service and device-hardware qualification explicit and separate from deterministic fixtures.
4. Record concrete newly required application workflows or intentional baseline changes as bounded feature work. Preserve documented native defaults and unsupported policy combinations rather than promising arbitrary cross-language object/session compatibility.
5. Seek maintainer decisions on ownership, package names, support and merge/release scope before publication.

These are maintenance and adoption decisions, not a claim that the implemented fourteen-family feature scope is absent or a delivery commitment. The [feature scope](../../strands-dotnet/docs/PARITY.md) and [validation results](../../strands-dotnet/docs/VALIDATION_RESULTS.md) remain the central references for current capability and execution evidence.

### Decisions requiring maintainers

- Whether to adopt a third native language implementation and its maintenance/release ownership.
- NuGet package ownership, public naming, alpha versioning, and long-term target-framework support.
- Acceptance of the separate optional Bedrock package for core consumers and its dependency from the harness; ownership of future provider packages and standard .NET model adapters.
- Accepted casing and serialization contracts; cross-language compatibility of sessions and configuration.
- Required parity threshold for initial merge and subsequent public release.
- API reviewer assignment; substantial changes call for early review and ideally at least two reviewers.
- Cancellation results, cleanup diagnostics, hook priority, child dependency sharing, and message-window guarantees documented in the API proposal.
- Generated XML documentation is enabled; an automated public API compatibility baseline/freeze remains pending.

</details>
