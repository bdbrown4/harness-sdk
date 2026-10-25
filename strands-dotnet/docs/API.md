# Public API proposal

This is the compact review surface for the thirteen public package families: `Strands.Agents`, `Strands.Agents.Bedrock`, `Strands.Agents.SageMaker`, `Strands.Agents.Bidi`, `Strands.Agents.Bidi.Audio`, `Strands.Agents.Cedar`, `Strands.Agents.OpenTelemetry`, `Strands.Agents.Monty`, `Strands.Agents.S3`, `Strands.Agents.Sqlite`, `Strands.Agents.BedrockKnowledgeBase`, `Strands.Agents.GoogleAuth` and `Strands.Harness`. Version `0.1.0-alpha.1` targets `netstandard2.0;net8.0;net10.0`. **Feature implementation and the recorded engineering validation are complete; this remains an alpha proposal pending maintainer adoption and API review.** .NET Framework 4.8 applications consume the .NET Standard 2.0 asset; `net48` is a test/consumer target. Package names/defaults/contracts await maintainer alignment and [API bar raising](../../team/API_BAR_RAISING.md). XML documentation and normal package API validation are enabled. No prior released native .NET compatibility baseline was supplied; public API freeze and publication remain maintainer decisions.

The optional [watched managed-tool factory](../../harness-dotnet/docs/WATCHED_MANAGED_TOOLS.md), `HarnessAssemblyLoader.CreateToolProvider(HarnessAgentConfig)`, returns `IToolProviderUpdates` over explicitly declared managed inputs. It retains host dependency binding and borrowed tool lifetimes.

The [feature checklist](PARITY.md#completed-feature-checklist) records fourteen native capability families and twenty-eight accepted component outcomes against the pinned Python/TypeScript union. Supported application behavior and protocol/resource contracts define parity; foreign private algorithms, runtime objects and arbitrary policy combinations do not. The [RFC](../../team/designs/0021-dotnet-sdk.md) explains the alternatives and ownership decisions.

The platform target is modern .NET on Windows/Linux/macOS and existing Framework 4.8 applications on Windows. [Recorded execution](VALIDATION_RESULTS.md) binds the current Windows/Linux and Intel/Apple Silicon source checks, all thirteen package families' fresh modern consumers and the authored genuine Framework 4.8 x64/x86 pipeline to their exact source and package identities. Framework CI builds its own normal package feed; it is not asserted byte-identical to the separately built local feed. Live provider calls and physical audio-device checks remain separate opt-in profiles.

Source compilation uses the repository's modern .NET SDK and C# language version even when consumers run .NET Framework 4.8. `Microsoft.Bcl.AsyncInterfaces` 10.0.13 supplies compatible async-interface types; `System.Text.Json` 10.0.13 supplies JSON support, and schema validation uses the [privately isolated native engine](JSON_SCHEMA_VALIDATION.md). The dedicated compatibility suite targets `net48;net8.0;net10.0`; core/harness suites target modern .NET. Framework tests execute only on Windows. Compatibility compilation, runtime test execution, and external packed-package consumption are distinct checks.

Consuming the options API requires C# 11 or newer because it exposes `required`/`init` members; examples use C# 12. Async-stream enumeration syntax requires C# 8+, while `InvokeAsync` returns a normal `Task<AgentResult>`. SDK-style Framework applications constructing HTTP provider clients should reference `System.Net.Http` and enable `AutoGenerateBindingRedirects` plus `GenerateBindingRedirectsOutputType`. The [getting-started project](../examples/GettingStarted/GettingStarted.csproj) demonstrates those settings for `net48` alongside modern targets. No C# 7 API compatibility is claimed.

Modern library assemblies forward their compatibility `IsExternalInit` marker to the runtime marker using standard [CLR type forwarding](https://learn.microsoft.com/en-us/dotnet/standard/assembly/type-forwarding). A real precompiled .NET Standard module reproduced `MissingMethodException` when constructing options against a modern asset before this fix. The unchanged fixture now exercises init setters from multiple SDK assemblies on modern hosts, alongside Framework execution. Packed API validation remains enabled; the marker is an implementation compatibility detail.

[Media contracts](MEDIA.md) document additive video/S3/HTTP-image sources, selective guard content, snapshot validation and each provider's supported projection. A guard selection does not enable a provider guardrail by itself.

[Native Bedrock token counting](BEDROCK_TOKEN_COUNTING.md) is opt-in and exposes explicit fallback/strict policies. `Strands.Agents.SageMaker` uses the official AWS Runtime client and has its own [configuration, payload and ownership contract](../src/Strands.Agents.SageMaker/DESIGN.md). Its package remains an optional consumer dependency even though the shared solution builds and tests it.

## Experimental checkpoint marker

```csharp
public enum CheckpointPosition { AfterModel, AfterTools }
public sealed record AgentCheckpoint
{
    public const string CurrentSchemaVersion = "1.0";
    public AgentCheckpoint(CheckpointPosition position, int cycleIndex = 0);
    public CheckpointPosition Position { get; }
    public int CycleIndex { get; }
    public string SchemaVersion { get; }
}
```

This immutable value represents a boundary separately from conversation state. Its native System.Text.Json representation uses `position` (`afterModel` or `afterTools`), `cycleIndex` and `schemaVersion`; incompatible versions, invalid positions and negative or non-Int32 cycles are rejected. A missing cycle defaults to zero. AgentResult.Checkpoint now carries this value as optional result data; Agent pause/resume remains separate. It does not promise foreign session interchange.

## Safe result JSON and rendering

```csharp
JsonElement json = AgentResultJson.ToJson(result, AgentResultOutputProfile.TypeScript);
AgentResult detached = AgentResultJson.FromJson(json);
string text = AgentResultRendering.Render(result, AgentResultOutputProfile.Python);
```

The native versioned envelope includes the stop reason, detached message and optional typed checkpoint marker. TypeScript projection also includes present structured JSON; Python omits it and writes an absent marker as JSON null. Rehydration creates fresh empty local metrics/state rather than restoring execution or conversation state. Explicit rendering preserves the selected source's interrupt, structured output and message-content priority. The five-argument constructor, deconstruction, default JSON with no marker, and existing ToString remain compatible. See [the full native contracts and qualifications](AGENT_RESULTS.md); this is separate from foreign result/session wire formats and Agent checkpoint execution.

## Core agent: `Strands.Agents`

```csharp
public sealed class Agent : IAsyncDisposable
{
    public Agent(AgentOptions options);
    public Task InitializeAsync(CancellationToken cancellationToken = default);
    public Task<AgentResult> InvokeAsync(string? prompt,
        InvocationOptions? options = null, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<IAgentStreamEvent> StreamAsync(string? prompt,
        InvocationOptions? options = null, CancellationToken cancellationToken = default);
    public Task<AgentResult> InvokeContentAsync(IReadOnlyList<ContentBlock> content,
        InvocationOptions? options = null, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<IAgentStreamEvent> StreamContentAsync(IReadOnlyList<ContentBlock> content,
        InvocationOptions? options = null, CancellationToken cancellationToken = default);
    public ValueTask<ToolResultBlock> InvokeToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<IToolStreamEvent> StreamToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default);
    public ValueTask<ToolResultBlock> InvokeToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions options, CancellationToken cancellationToken);
    public IAsyncEnumerable<IToolStreamEvent> StreamToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions options, CancellationToken cancellationToken);
    public ValueTask<AgentSnapshot> TakeSnapshotAsync(SnapshotOptions options,
        CancellationToken cancellationToken = default);
    public ValueTask LoadSnapshotAsync(AgentSnapshot snapshot,
        CancellationToken cancellationToken = default);
    public ITool AsTool(string? name = null, string? description = null);
    public Agent CreateChild(Func<AgentOptions, AgentOptions>? configure = null);
    public void SetModel(IModel model);
    public void SetAuxModel(IModel? model);
    public void AddTool(ITool tool);
    public void AddTools(IReadOnlyList<ITool> tools);
    public void ReplaceApplicationTool(ITool tool);
    public void AddOrReplaceApplicationTool(ITool tool);
    public void AddOrReplaceApplicationTools(IReadOnlyList<ITool> tools);
    public bool RemoveApplicationTool(string name);
    public void ClearApplicationTools();
    public CancellationToken CancelSignal { get; }
    public void Cancel(string? message = null, bool afterCurrentTools = false);
    public Task ShutdownAsync();
    public ValueTask DisposeAsync();
}
```

Read-only properties expose `Model`, `AuxModel`, `Id`, `Name`, `Description`, `SystemPrompt`, `SessionId`, `Hooks`, `Tools`, `State`/`AppState`, `ModelState`, `Storage`, `FileSystem`, `ContextManager`, `MemoryManager` and a message snapshot. `AuxModel` falls back to `Model`. A null prompt continues current/restored history. One invocation may be active; another raises `ConcurrentInvocationException`. Stream enumeration starts it; disposal releases its guard and repairs unfinished tool batches. `InitializeAsync` restores the session and initializes plugins once, caching success, failure, or cancellation after initialization admission. A token canceled before admission does not load the session, initialize plugins, or cache that attempt. Admitted initialization cancellation is terminal for that instance: dispose it and construct a fresh agent with appropriate dependencies to retry. Rollback attempts SDK-owned cleanup; accepted restored state, user initializer effects and manually registered hooks are not transactional. Dynamic registration validates a complete batch atomically and requires no active invocation or initialization.

`SetModel(model)` selects a borrowed ready primary model for subsequent model-attempt captures. An entered provider operation and its request/state handling keep their captured selection. The constructor model retains any construction-time ownership; assigning a replacement transfers no ownership and does not start or dispose it. Shutdown/disposal rejects replacement before and after evaluating native capability, with capability callbacks outside the lifecycle lock.

`AgentOptions.ModelSelectionTiming` defaults to `BeforeModelHooks` (TypeScript/native capture timing); `AfterModelHooks` observes ready-model changes made by `BeforeModelCall` hooks, matching Python. `ModelHistoryBehavior` separately defaults to `Native`, which retains construction-time capability and native acknowledgement cursors/input receipts. Explicit `TypeScript`/`Python` profiles permit cross-capability ready/scoped selections and send retained local history. Their after-invocation cleanup reads the constructor model reference's current capability (TypeScript) or the then-current selected model (Python). Provider state and entered scopes remain independent. The profiles reject native cursor/input-receipt imports and indexed-journal construction; native durable pauses retain their owned history. An automatically applied initial session snapshot clears local messages only for an explicit `TypeScript`/`Python` history profile whose current model is stateful, after plugin and lifecycle-observer initialization and before `Initialized`. Imported unmarked Native pending tool batches retain their paired history; a separately supplied `PythonReplay` marker also retains restored history when its invocation wins first initialization. Provider state is preserved. Both default option values are omitted from JSON.

At equal priority, TypeScript registers source clearing after plugins so it unwinds before earlier completion hooks. Python registers it before plugins so classic conversation management runs first. When source clearing applies, the pending execution ledger survives independently of committed messages. Internal durable source ownership also survives a paired save made before clearing.

`SetAuxModel(model)` selects a borrowed ready auxiliary model. `SetAuxModel(null)` resets auxiliary calls to the primary model. The selection does not start or dispose either model. Default children capture the current explicit auxiliary selection; a reset child follows its own primary model. Memory extraction keeps the default captured when its extractor initialized, and an explicit extractor model keeps precedence. Reassignment is rejected after shutdown or disposal begins; ownership remains defined by construction and `OwnedResources`.

`InvokeContentAsync` and `StreamContentAsync` accept ordered text/media/JSON blocks as one user message. The input list and JSON values are detached when the method is called, including before stream enumeration. Empty content adds an empty user message; a null string prompt remains the continuation API. Tool-use and tool-result blocks are rejected before initialization or session reads because the loop owns their correlation. `InvokeStructuredContentAsync<T>` combines the same typed input with structured response validation. Invocation context remains caller-owned and is forwarded by reference.

`AgentOptions` has required `IModel Model`; `OwnsModel = false`; empty `OwnedResources`; nullable `AuxModel`; generated `Id`; `Name = "agent"`; nullable `Description`/`SystemPrompt`; empty `Tools`, `Messages`, `Plugins`, application/model JSON state; a new application `HookRegistry`; `MaxTurns = 20`; `MaxParallelTools = 8`; concurrent `ToolExecution`; and optional sessions, conversation/context managers, memory manager, byte storage and selected filesystem. A session store requires a nonempty ID. Collections/state are copied; caller-owned dependencies remain shared. Each agent owns a hook overlay. Default children receive fresh identity/model state and separate built-in context/memory manager bindings.

`ShutdownAsync` is an awaited, cached terminal lifecycle: initialization/invocations must finish first, subsequent work is barred, and all shutdown callbacks run even when one fails. Direct/nested callback cycles are rejected instead of self-awaiting. `DisposeAsync` runs shutdown, then attempts transferred resources in reverse order and the explicitly owned main model before clearing hooks. Primary errors retain secondary failures in `Data["Strands.CleanupErrors"]`. Borrowed stores, filesystems, transports and models remain caller-owned.

`InvocationOptions` has caller-owned `IReadOnlyDictionary<string, object?> InvocationState` (a new empty dictionary by default), nullable `MaxTurns`, `MaxTotalTokens`, and `MaxOutputTokens`. Supplied limits must be positive. Per-call turns override the construction default; null token budgets impose no additional SDK budget. Limits are checked after a completed tool batch before requesting another model turn. They use reported usage and do not estimate or cap every in-flight provider response.

`AgentResult` carries `StopReason`, nullable last assistant `Message`, accumulated `Usage`, completed model `Turns`, and the caller's `InvocationState`. `ToString()` returns accepted structured JSON when present, otherwise that message's text. Stop reasons are extensible strings; provided constants include `end_turn`, `tool_use`, `max_tokens`, `stop_sequence`, `content_filtered`, `max_turns`, `cancelled`, `interrupt`, `checkpoint`, `max_total_tokens`, and `max_output_tokens`.

Only `tool_use` requests execute tools. When a provider ends a tool-containing turn with `max_tokens`, the agent returns that terminal reason, appends correlated skipped error results, and invokes no tool bodies.

### Model-boundary intervention feedback

`AgentOptions.GuideFeedbackDelivery` selects how accumulated `Guide` feedback enters local history:

```csharp
public enum GuideFeedbackDelivery { Native = 0, DirectAppend = 1 }
```

The default `Native` value is omitted from options JSON and keeps ordinary `MessageAddedEvent` callbacks and the immediate session-save path. `DirectAppend` appends a tracked user message without those feedback callbacks or an immediate save, matching the Python/TypeScript model-boundary feedback path. The feedback enters subsequent model requests and native snapshots; normal invocation-finalization persistence still applies. After-model guidance retries before the rejected response, proposed provider state or requested tool effects are committed. Undefined enum values are rejected during `Agent` construction.

Select `DirectAppend` when a review policy should guide another model attempt without treating its feedback as an ordinary message callback:

```csharp
using System.Threading.Tasks;
using Strands.Agents;
using Strands.Agents.Interventions;

static async Task<AgentResult> AskWithReviewAsync(
    IModel model, InterventionHandler review, string prompt)
{
    await using var agent = new Agent(new AgentOptions
    {
        Model = model,
        Interventions = [review],
        GuideFeedbackDelivery = GuideFeedbackDelivery.DirectAppend
    });
    return await agent.InvokeAsync(prompt);
}
```

The model and review handler remain caller-owned. This option governs before/after-model feedback; indexed-journal, Goal, catalog, background and broader intervention composition retain their separate [parity qualifications](PARITY.md#interventions-and-authorization--partial).

Component verification passes the same ten Guide checks on .NET 10 and .NET 8, including provider-failure persistence, plus a 467-check compatibility selection on installed Framework 4.8.1 x64 using the normal .NET Standard 2.0 Core asset. All three builds report zero warnings/errors. That same 467-check bin also passes on genuine Framework 4.8 x64 and x86, with pointers 8/4 and release 528049, without a guest build or restore. Current whole-suite Framework, Linux/macOS, fresh consumers, package/ABI and whole-family acceptance remain separate [verification gates](PARITY.md#interventions-and-authorization--partial).

### Cancellation and failure

Cancellation through ordinary `InvokeAsync`/`StreamAsync` produces a terminal cancelled result, including an already-cancelled token. Direct model calls and typed `InvokeStructuredAsync<T>` retain cancellation exceptions. Cleanup failures can still escape.

Cancellation is cooperative. Before-tool hooks are awaited and cancellation is checked again before tool execution. Committed tool requests receive correlated error results when execution is cancelled or the stream is disposed. A completed tool result is retained if a peer or after-tool hook fails. Tool-body exceptions become model-visible error results; hook/schema pipeline failures commit the complete batch and then fail the invocation. Sequential execution skips remaining calls after a pipeline failure; concurrent peers may finish.

Primary invocation/model errors are preserved when after-model or invocation cleanup also fails. Secondary failures are attached as a `List<Exception>` at `exception.Data["Strands.CleanupErrors"]`. When no primary error exists, a cleanup failure itself propagates. Applications should inspect the original exception and that attached diagnostic list.

If custom HTTP content ignores cancellation while opening a provider response body, cancellation stops waiting and the owned late stream is disposed when it becomes available. The cancellation exception exposes `Data["Strands.CleanupTask"]` as a no-fault `Task<IReadOnlyList<Exception>>`; awaiting it observes completion and any deferred cleanup errors. Already completed errors also appear in `Strands.CleanupErrors`. This runtime-only diagnostic is limited to cancelled body opening and is not a binary exception-serialization contract. Pre-cancelled calls start no body opening or deferred work.

`AsTool` derives its default name from the first 64 agent-name characters, replacing unsupported characters with `_`; explicit names receive ordinary strict tool-name validation. It derives its description from the agent, accepts `{ "task": "..." }`, creates/disposes a fresh child without a session, and forwards caller invocation state. `CreateChild` copies application state and construction policies while sharing models/tools/plugins and construction-time application callbacks. Plugin registrations remain child-owned. A default child never owns its parent's model. Shared dependencies must tolerate concurrent children. Harness authority/depth delegation is described separately below.

## Structured responses and model policies

```csharp
public Task<StructuredOutputResult<T>> Agent.InvokeStructuredAsync<T>(string? prompt,
    StructuredOutputOptions<T> options, InvocationOptions? invocationOptions = null,
    CancellationToken cancellationToken = default);
public sealed record StructuredOutputResult<T>(T Output, AgentResult Result);

public interface IInvocationModel : IModel
{
    IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState);
}
// Strands.Agents.Models
public RetryModel(IModel model, RetryModelOptions? options = null);
public RoutingModel(IEnumerable<RoutingCandidate> candidates,
    RoutingModelOptions? options = null);
public ClassifierRoutingStrategy(IModel model, ClassifierRoutingOptions? options = null);
```

Ordinary InvokeAsync/StreamAsync accept StructuredOutputOptions<JsonElement> through AgentOptions.StructuredOutput (default) and InvocationOptions.StructuredOutput (per-call override). AgentResult.StructuredOutput exposes the accepted detached JSON value; null means no accepted output, while a present JsonElement of Null kind represents explicit JSON null. The result text uses structured JSON when present. Default settings are copied at construction, per-call settings at enumeration, and validation state is fresh for every invocation/enumeration. Explicit InvokeStructuredAsync<T> retains its own typed settings. These entry points currently retain the existing native forcing/history behavior; selectable TS/Python forcing/history and accepted-result behavior are available through StructuredOutputOptions<T>.Behavior. See [source profiles and stateful receipt rules](STRUCTURED_OUTPUT_SOURCE_PROFILES.md). Safe complete-result JSON projection is available through the explicit APIs below.

`StructuredOutputOptions<T>` requires JSON `Schema`; optional `Description`, `SerializerOptions`, and `Func<JsonElement,T> Deserializer` customize conversion. `ForcePrompt` defaults to `"You must format the previous response as structured output."`; `MaxValidationAttempts = int.MaxValue` leaves ordinary invocation limits governing retries unless a smaller positive budget is supplied. A temporary `strands_structured_output` tool validates schema and deserialization, returns correlated retry feedback, and terminates after the first successful normalized paired result in assistant order. Ordinary text triggers a forced named selection; refusal after forcing fails. `Output` is typed, while `Result` retains usage, state, and `tool_use`. Cancellation throws `OperationCanceledException`; missing/invalid/limited output throws `StructuredOutputException`. This is a response loop, not ordinary tool-input validation or a provider-native `response_format` API. [Contracts/tests](../src/Strands.Agents/StructuredOutput.cs), [Framework integration](../tests/Strands.Compatibility.Tests/StructuredOutputCompatibilityTests.cs).

`RetryModelOptions` defaults to six total attempts, throttling-only classification, and exponential full jitter with a four-second base and 240-second cap. `IsRetryable` and `IBackoffStrategy` customize it; cancellation is never retried. `ConstantBackoff` defaults to one second; `LinearBackoff`/`ExponentialBackoff` default to a one-second base, thirty-second cap, full jitter, and exponential multiplier two. Delay calculations are bounded and cancellation interrupts waits. Different contract: no retry/failover occurs after any event escapes, preventing duplicate exposed stream content/usage. These are explicit decorators; they do not install upstream default retry hooks or assume ownership of transport dependencies.

`RoutingCandidate` holds an `IModel`, optional unique name/description, and cloned JSON-object metadata. `RoutingModelOptions` defaults to `FallbackRoutingStrategy` with no extra `MaxSwitches` cap. Agent integration creates one invocation-local model for all turns. Fallback prefers the least-failed available candidate with declaration-order ties; success resets that candidate's failure count. Each failure round tries a candidate once; nested routers resolve as opaque candidates. Invalid/erroring selection after failure preserves the pending model error. `IRoutingStrategy.SelectAsync(RoutingContext, CancellationToken)` can select a configured candidate or decline. Classification defaults to thirty seconds and 4000 characters each for request, parent instructions, and aggregate candidate evidence; evidence exceeding its budget fails rather than truncating. Classifier calls are additional provider operations, not complete main-result usage/telemetry attribution. [Retry source](../src/Strands.Agents/Models/RetryModel.cs), [retry cases](../tests/Strands.Agents.Tests/Models/RetryModelTests.cs), [routing source](../src/Strands.Agents/Models/RoutingModel.cs), [routing cases](../tests/Strands.Agents.Tests/Models/RoutingModelTests.cs).

## Messages and model extension

```csharp
public interface IModel
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ModelRequest(IReadOnlyList<Message> Messages,
    string? SystemPrompt = null, IReadOnlyList<ToolSpec>? Tools = null)
{
    public IReadOnlyList<ContentBlock>? SystemPromptContent { get; init; }
    public ToolChoice? ToolChoice { get; init; }
    public string? SessionId { get; init; }
}
public enum ToolChoiceMode { Auto, Any, None, Tool }
public sealed record ToolChoice(ToolChoiceMode Mode, string? Name = null);

public Message(Role role, IReadOnlyList<ContentBlock> content, string? trackingId = null);
public static Message Message.User(string text);
public static Message Message.Assistant(string text);
```

`Role` contains `User`/`Assistant`. Messages expose copied read-only content, tracking ID, and concatenated text. Blocks include text, tool use/results, signed reasoning, redacted reasoning, JSON, base64 image/audio/document, resource links, and explicit cache points. `ToolResultBlock` retains success/error status, `ContentBlocks`, and optional structured JSON; its rich constructor clones nested content. `CitationsBlock` and native Anthropic replay blocks preserve cited generated text. `VideoBlock` and `GuardContentBlock` represent supported video and selective guard content; [media contracts](MEDIA.md) identify each provider's projection and explicit restrictions. A named `ToolChoice` must identify a supplied tool; only that mode accepts a name, and `Any` requires tools.

`ModelRedactionEvent` replaces blocked input/output while preserving usage and tool-result correlation. `AgentOptions.InputRedactionTiming` selects `AcceptedTurn` (TypeScript default) or `ProviderEvent` (Python inline); `InvokeModelResult.InputRedaction` carries an accepted middleware result's input policy. [Guardrail redaction](GUARDRAIL_REDACTION.md) describes history/session commits, retries and cancellation boundaries.

ModelRequest.SystemPromptContent optionally supplies detached ordered text/cache-point system blocks, with null preserving the legacy string representation/default JSON shape. Typed membership takes precedence and SystemPrompt exposes its newline-joined text projection. Switching representation with a with-expression requires SystemPromptContent = null. The [request/provider and Agent/session stages](BEDROCK_SYSTEM_PROMPTS.md) preserve text, cache and supported selective guard content without flattening its authority. AgentOptions.SystemPromptContent and the read-only Agent.SystemPromptContent view retain detached ordered membership. Agent configuration rejects simultaneous nonnull string and typed prompts. InvokeModelContext replacement clears the other representation, with the last explicit initializer winning. Children, Skills and durable session/snapshot operations preserve the accepted representation and its presence semantics.

Model events include one assistant start, indexed block starts/deltas/stops, an explicit message stop, and additive usage metadata. `ModelContentBlockStartEvent(Index, ToolUse = null)` also has optional `ContentBlock` initial content; two simultaneous representations are invalid. Deltas append text/tool JSON/reasoning/signatures and copied `Citations` lists. Optional `ModelContentBlockDeltaEvent.RedactedReasoningBytes` carries detached opaque bytes; an explicit empty fragment still identifies opaque content. Opaque reasoning cannot share a block with text, tool input, signed reasoning, citations, or fixed typed content. `ModelContentBlockStopEvent.ContentBlock` may supply the matching completed block without rewriting emitted content. Starts/messages/results clone JSON lifetimes. Blocks/IDs/stop reasons are validated, including terminal token-limited tool responses. Malformed/incomplete turns raise `ModelException`. [Typed-content cases](../tests/Strands.Agents.Tests/TypedContentTests.cs), [opaque reasoning cases](../tests/Strands.Bedrock.Tests/OpaqueReasoningPayloadTests.cs).

`Usage` defaults all counts to zero and includes input/output tokens, cache-read/cache-write input tokens, and optional `ReportedTotalTokens`. `TotalTokens` uses the reported total when supplied and otherwise input plus output. Usage accumulates across completed turns with checked arithmetic; invalid negative/overflowing provider usage is rejected.

`CitationsBlock` copies its `Citations` and generated `Content` lists. Each `Citation` has a typed `CitationLocation`, `Source`, `Title` and source excerpts (`CitationText`). Location variants are `WebCitationLocation`, `DocumentCharCitationLocation`, `DocumentPageCitationLocation`, `DocumentChunkCitationLocation` and `SearchResultCitationLocation`. Model citation deltas accumulate with text into one block; reasoning/tool blocks reject citation deltas. Completed blocks cannot rewrite previously streamed text or normalized citations. `Message.Text`, extraction, summaries and token estimates retain generated text. Citation estimates exclude source excerpts and protocol overhead.

Anthropic citations retain detached native replay fields under `CitationsBlock.ProviderMetadata.anthropic`; server-executed blocks use `AnthropicContentBlock` and never become local tool calls. Unknown citation formats remain available in the native block. Bedrock converts normalized locations to its native citation union and preserves source identifiers. Other HTTP adapters can forward provider-neutral cited assistant text. These contracts do not establish complete foreign-provider replay.

`DocumentBlock` has nullable `CitationsEnabled` and `Context`. Bedrock forwards both per-document fields. Anthropic forwards them on PDF documents and, when explicitly configured, wraps UTF-8 text documents in its native document source; this text-document option is a native extension beyond the pinned adapters' plain-text fallback. Provider behavior is described by the official [Anthropic citation API](https://platform.claude.com/docs/en/build-with-claude/citations) and [Bedrock citation contract](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_CitationsDelta.html). Live citation calls remain unverified. [Shared contract cases](../tests/Strands.Agents.Tests/Models/CitationTests.cs), [Anthropic cases](../tests/Strands.Agents.Tests/Models/AnthropicServerToolsTests.cs), [Bedrock cases](../tests/Strands.Bedrock.Tests/BedrockCitationTests.cs) run against modern and Framework assets.

Agent streams include raw `ModelStreamUpdateEvent`, completed `ContentBlockEvent`/`ModelMessageEvent`, committed `MessageAddedEvent`, and final `AgentResultEvent`. [Observation hooks](HOOK_EVENT_PARITY.md) specify ordering, detached payloads, context binding, write-only completed-event JSON and preserved old positional constructors/equality. Provider deltas are provisional; committed messages arrive separately. Model/provider errors are `ModelException`, specialized by `ModelThrottledException` and `ContextWindowOverflowException` where classification is available.

The [explicit cancellation API](AGENT_CANCELLATION.md) supplies a per-pass read-only token and optional/deferred cancellation text while retaining caller token ownership and durable-save/accepted-turn behavior. Idle requests are ignored by default; set `AgentOptions.IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation` to retain an immediate idle request and its last nonnull message for the next invocation. Deferred-only idle requests are cleared on admission.

## Tools and hooks

The standalone [tool registry](TOOL_REGISTRY.md) exposes atomic ordered membership and explicit name-resolution policies. The [Agent application registry](AGENT_TOOL_REGISTRY.md) publishes schema/body/advertisement updates only while inactive and without pending interrupts. Its replacement/removal APIs cannot overwrite plugin/provider registrations. `AgentOptions.ToolNameConflictPolicy` and `DirectToolNameResolution` preserve exact-name compatibility by default; explicit alternate direct lookup never changes model-issued or Bidi exact lookup. Generic provider refresh/loading authority remains separate.

[Direct execution](AGENT_TOOLS.md#direct-execution) exposes tool progress and an accepted rich result without a model call. Original overloads retain native no-history behavior; explicit `DirectToolCallOptions` select TypeScript or Python history and management profiles. The detailed contract describes schema authority, partial history publication and admission limits.

`IAgentStreamEvent` is the common streaming-event interface. `HookEvent` is an extensible abstract record implementing it; concrete lifecycle records share this base. Keeping the stream category as an interface avoids incompatible compiler-generated covariant clone members in a nested record hierarchy across .NET Standard and modern assets. A separately compiled .NET Standard consumer fixture derives and copies custom hooks, then runs against each runtime asset in the compatibility suite.

```csharp
public ToolSpec(string name, string description, JsonElement inputSchema);
public interface ITool
{
    ToolSpec Spec { get; }
    ValueTask<string> InvokeAsync(JsonElement input, ToolContext context,
        CancellationToken cancellationToken = default);
}
public interface IRichTool : ITool
{
    ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context,
        CancellationToken cancellationToken = default);
}

public FunctionTool(ToolSpec spec,
    Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback);
public static FunctionTool FunctionTool.Create<T>(string name, string description,
    JsonElement schema, Func<T, ToolContext, CancellationToken, ValueTask<string>> callback,
    JsonSerializerOptions? serializerOptions = null);

public IDisposable HookRegistry.Register<T>(Func<T, CancellationToken, ValueTask> callback,
    int order = 0) where T : class, IAgentStreamEvent;
public ValueTask HookRegistry.InvokeAsync<T>(T value,
    CancellationToken cancellationToken = default) where T : class, IAgentStreamEvent;
```

Tool names contain 1–64 ASCII letters/digits/underscores/hyphens; descriptions must be nonempty and schemas JSON objects. `ToolSpec` also carries optional output schema and advisory annotations. The agent validates input before ordinary callbacks; `Create<T>` then deserializes using supplied options or web defaults. Direct tool calls bypass agent validation. Rich tools return correlated typed results; invalid IDs fail instead of corrupting history. `ToolContext` carries the current agent/request and user-owned invocation state.

Hooks include `InitializedEvent`, before/after invocation/model/tool calls, and `BeforeToolsEvent`/`AfterToolsEvent`. A before-model callback can replace ephemeral `Request`. Before-tool callbacks can replace `ToolUse`, select an `ITool`, or set `CancelReason`; after-tool callbacks can replace `Result`. The original model-issued ID remains authoritative. Batch lists are read-only. Priorities ascend; equal-priority after-hooks reverse registration order. Subscriptions are disposable; synchronization does not make callbacks thread-safe. Cleanup hooks receive `CancellationToken.None`. `IPlugin` exposes stable `Name`, `InitializeAsync(Agent, CancellationToken)`, and `GetTools()`; names are unique per agent. [Hook/plugin contracts](../src/Strands.Agents/Plugins.cs), [transform tests](../tests/Strands.Agents.Tests/ToolTransformTests.cs).

`StateStore` offers detached `Get(string)`, typed `Get<T>(string)`, `Set<T>(string,T)`, `Delete(string)`, `Clear()`, and read-only `GetAll()`, and detached read-only `Keys()`. It serializes JSON before changing state, so failed serialization retains the old value. State is distinct from invocation context and model-visible messages. Snapshots capture values and version together; dirty state is persisted at invocation cleanup.

PluginFactory.FromObject(object instance, string name, Func<Agent, CancellationToken, ValueTask>? initialize = null) discovers public instance methods marked with HookAttribute or ToolAttribute. HookAttribute.Order uses existing hook priorities. The adapter borrows its target; hooks accept one typed event and an optional CancellationToken, returning void, Task or ValueTask. See [attributed plugin contracts](ATTRIBUTED_PLUGINS.md) for declaration ordering, validation and per-Agent ownership.

### Borrowed tool providers and live updates

`AgentOptions.ToolProviders` defaults to an empty ordered list. Providers remain borrowed: an Agent admits its stable `ToolProviderContext`, loads and filters a complete snapshot, and removes every tracked consumer on failed initialization or shutdown. Children receive distinct consumer contexts. Application tools precede providers; a consumer identifier is unrelated to a persisted session identifier.

```csharp
public interface IToolProviderUpdates : IToolProvider
{
    ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context,
        IObserver<IReadOnlyList<ITool>> observer,
        CancellationToken cancellationToken = default);
}
```

`AgentOptions.EnableToolProviderUpdates` defaults to `false`; the existing provider ABI stays unchanged. When enabled, this optional capability delivers its initial full snapshot before returning a nonnull owned subscription lease. Its token governs establishment. Lease disposal stops and physically joins delivery; provider and tools remain borrowed. Each declaration replaces only its own complete, validated table. Updates during active admission or a pending foreground interrupt coalesce and publish at the next valid boundary. Terminal notification drops unpublished updates and retains the last published table. Original tool/specification/validator bindings remain attached to already admitted background tasks until their physical bodies finish.

`McpToolProvider` implements the capability over its existing shared RPC pump and versioned cache. MCP `ToolChanges` and Agent publication are separate opt-ins. Each consumer has an independent serialized delivery worker; consumer removal joins all matching subscriptions. See [live-update ownership and limits](LIVE_TOOL_PROVIDER_UPDATES.md). Captured Harness delegation catalogs, Bidi advertisements, managed watching, automatic reconnect and unloading need separate contracts.

### Method-derived C# tools

`ToolFactory.Create(Delegate, ToolFactoryOptions?)` derives a JSON-object input schema from the callback's declared parameters. `[Tool("description", Name = "registered_name")]` supplies method metadata; `[ToolParameter("description", Name = "argument_name")]` supplies parameter metadata. Explicit options override method metadata. Compiler-generated local/lambda names need an explicit valid tool name. `ToolFactory.FromObject(instance)` discovers only attributed public instance methods; `FromType(type)` discovers attributed public static methods. Discovery invokes no methods and rejects duplicate names atomically. Objects and captured dependencies remain caller-owned.

`ToolContext` and `CancellationToken` parameters are injected rather than advertised to the model. Other parameters use their declared JSON types, top-level nullable annotations and optional defaults; the serializer supplies DTO property contracts. Schema validation and duplicate/unknown property checks run before direct calls as well as agent calls. Sync values, `Task<T>`, `ValueTask<T>`, non-generic tasks and rich results are supported; streaming callbacks return `IAsyncEnumerable<IToolStreamEvent>` and use the existing validated streaming runner. Ref/out/pointer parameters, open generic methods, multicast delegates and async-void callbacks are rejected. Original callback exceptions are unwrapped from reflection; no background work is inferred.

Schemas use the [System.Text.Json schema exporter](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema) from the existing JSON package, including recursive DTO references and serializer naming/converters. Default options use camel-case DTO properties, strict numeric handling and required constructor parameters; supplied serializer options are copied. The implementation uses reflection and makes no trimming or Native AOT guarantee. [Factory](../src/Strands.Agents/ToolFactory.cs), [portable cases](../tests/Strands.Agents.Tests/ToolFactoryTests.cs).

### Streaming tools and middleware

`IStreamingTool.StreamAsync` yields zero or more ephemeral `ToolStreamEvent` updates followed by exactly one `ToolStreamResultEvent`. `StreamingFunctionTool` adapts an async-sequence callback; its direct invocation also validates terminal count, status and correlation. Agent streams expose progress through `ToolStreamUpdateEvent` and emit one `ToolResultEvent` after after-tool transformations. Progress is acknowledged before later hooks proceed; bounded buffering, cancellation and early disposal drain the producer. Concurrent batch history remains in model-issued order. JSON progress values are detached; arbitrary CLR progress objects remain caller-owned.

`MiddlewareRegistry` has typed input/wrap/output registrations for `MiddlewareStages.InvokeModel` and `ExecuteTool`, each returning an independent removal handle. Inputs run forward, the first wrapper is outermost and output transformations run in reverse registration order. Registrations and owned JSON/messages are snapshotted for an execution; caller invocation context and supplied model/tool references remain shared. A fully consumed chain must return exactly one typed terminal result. Wrappers may forward, filter or synthesize stream events and explicitly retry downstream work; the SDK adds no implicit replay. Model middleware receives an optional input-token estimate. Model results are authoritative for the committed message and usage; synthetic cached responses never acknowledge server-managed provider history. Agent hooks surround the stage once, and plugin registrations use an agent-owned layer. [Middleware contracts](../src/Strands.Agents/Middleware), [streaming contracts](../src/Strands.Agents/ToolStreaming.cs).

`MiddlewareStages.Create<TContext, TResult>(string name, Func<TContext, TContext> snapshotContext, Func<TResult, TResult> snapshotResult)` constructs an independent native stage token for the public `MiddlewareRegistry`. Pass that token to the existing typed registration and `InvokeAsync` methods. The supplied delegates define input and completed-result snapshots; callers choose how their mutable data is copied. All three arguments must be non-null. A name describes the token; two tokens with the same name retain independent registrations. The existing input/wrap/output ordering, progress streaming and exactly-one-terminal rules apply to custom stages. Ordinary Agent execution uses the built-in model/tool tokens.

This factory completes the native public registry's construction surface. The pinned TypeScript factory and registry are not supported package-root or subpath exports; their executor override is explicitly internal. Reusable `Compose` remains an optional native API proposal, and the whole-Agent middleware stage is an internal mechanism without an established additional public capability requirement.


The ready-made delay tool is exposed through Strands.Agents.VendedTools:

```csharp
ITool sleep = Sleep.Create(new SleepOptions
{
    Name = "sleep",
    MaximumDuration = TimeSpan.FromSeconds(60)
});
```

The input object requires numeric duration seconds between zero and the configured cap. The helper uses captured call cancellation and returns invariant text after its awaited delay. See [Sleep](SLEEP.md) for validation, lifetime and source-profile details.

The optional human handoff tool uses the native durable interrupt/resume mechanism:

```csharp
IRichTool handoff = HandoffToUser.Create(new HandoffToUserOptions
{
    Name = "handoff_to_user",
    Behavior = HandoffToUserBehavior.TypeScript
});
```

The input requires a nonblank question. TypeScript is the default reply profile; Python selects its JSON-text/result-envelope policy. Resumed answers retain the current tool-use identity. No console reader is installed. See [HandoffToUser](HANDOFF_TO_USER.md) for accepted rich media, metadata, durable resume and cancellation boundaries. ToolResultBlock.HasExplicitContent explicitly preserves an empty rich list; false keeps the earlier string fallback and default JSON omission.

The optional Stop helper uses `ITool stop = Stop.Create()` with Python deferred cancellation by default. `StopOptions.Behavior = StopBehavior.TypeScriptEndTurn` selects the experimental TypeScript ordinary end-turn profile. Options also expose `Name = "stop"`, optional `Description`, and positive `MaximumMessageLength = 4096`. See [Stop](STOP.md) for default-message, Unicode, sibling-completion and cancellation contracts. Creating this optional helper does not change the Harness default tool list.

## Sessions and context

```csharp
public sealed record SessionSnapshot(int SchemaVersion, IReadOnlyList<Message> Messages)
{
    public ModelHistoryBehavior HistoryProfile { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? State { get; init; }
}
public interface ISessionStore
{
    ValueTask<SessionSnapshot?> LoadAsync(string sessionId,
        CancellationToken cancellationToken = default);
    ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
public FileSessionStore(string directory);
public interface IConversationManager
{
    IReadOnlyList<Message> Reduce(IReadOnlyList<Message> messages);
}
public SlidingWindowConversationManager(int maxMessages = 40);
```

Restoration runs once before plugins/invocation, replacing initial messages and stored application state where present. Failed restoration cannot overwrite unread data. Message commits save complete snapshots; cleanup saves newly dirty state. `FileSessionStore` hashes IDs, writes a temporary file, and replaces the target. Concurrent writers remain last-writer-wins; shared sessions need an application lease. Schema `1` accepts optional state for backward compatibility, validates tool correlation, and permits a recoverable final pending batch. Missing files load as null; malformed data fails. Repair does not repeat side effects. There is no Python/TypeScript snapshot interoperability claim.

`SessionSnapshot.HistoryProfile` records the committed-message validation domain; its Native default is omitted from JSON. `InterruptState.AssistantMessageIsIndependent` records separate pending-assistant ownership; its false default is also omitted. Source-produced pauses retain additional internal serialized ownership through paired pre-clear saves. Local Native admission rejects retained-history domains and independent/source-owned pending batches before applying imported state or receiver initialization. The stored profile does not reconfigure the receiver or supply provider acknowledgement. Legacy unmarked paired pauses retain their paired domain.

For an automatically restored source profile, clearing uses the initialized current model after plugins/observers and before `Initialized`. Pending execution and provider state remain available; imported unmarked Native pairs and explicit checkpoint replay keep their history. Explicit `InitializeAsync` performs the conversion in memory without an immediate store write; invocation cleanup saves newly dirty history, including after a denied invocation.

New session writes also carry `HasSystemPrompt = true` and nullable `SystemPrompt`, preserving a deliberately cleared prompt across restoration. Older snapshots without the marker retain the configured prompt. This distinction is separate from an explicit in-memory checkpoint's selected fields.

`SlidingWindowConversationManager` reduces only model-visible history; durable history remains intact. Its positive `maxMessages` default is 40. The window expands backward to a user prompt to retain the complete exchange, so a long exchange can exceed the configured message count. It does not provide a hard token/context-window ceiling, summarization, or offload/retrieval.

The optional [SnapshotSessionManager](SNAPSHOT_SESSION_CATALOG.md) plugin supplies latest and immutable UUIDv7 records over borrowed IStorage. It supports SaveSnapshotAsync, ListSnapshotIdsAsync, RestoreSnapshotAsync and DeleteSessionAsync, plus Invocation/Message/Trigger automatic strategies. Existing ISessionStore implementations and the original two-argument manager constructor retain their ABI. Native ordered writes/deletes are not multi-key transactions.

### Explicit in-memory checkpoints

`TakeSnapshotAsync(new SnapshotOptions { Preset = SnapshotPreset.Session })` initializes an inactive agent and captures detached messages, application state, system prompt and provider state. `Include` adds selected `SnapshotFields`; `Exclude` is applied last. At least one field must remain selected. `AppData` is detached opaque application metadata and is never applied to agent state.

`AgentSnapshot` uses native scope `agent` and schema `1.0`, or `2.0` when capturing a complete consumed-input receipt/provider-state pair. Active receipts require messages and provider state together; restoration rejects partial or mismatched pairs before publication. `LoadSnapshotAsync` validates the complete supplied envelope before initialization, then replaces only the supplied fields while inactive. Absent fields remain unchanged; an explicit null system prompt clears it. `SnapshotFields.InterruptState` captures pending approvals, accepted responses and completed tool outputs; the session preset includes it. A restored batch must match its effective assistant history, and completed effects are skipped during resume. An absent interrupt field preserves an existing pause; explicit null clears it. Loading performs no immediate session write. Active invocations, initialization already in progress and terminal shutdown reject checkpoint operations. Server-managed history retains its acknowledgment cursor with corresponding provider state; invalid or mismatched replacements fail atomically. These native checkpoints do not establish TS/Python serialized-format interchange. Context-manager internals and durable multi-agent checkpoints still need integration. [Checkpoint implementation](../src/Strands.Agents/Agent.Snapshots.cs), [portable contract cases](../tests/Strands.Agents.Tests/AgentSnapshotTests.cs), [pause restoration cases](../tests/Strands.Agents.Tests/AgentSnapshotInterruptTests.cs).

### Opt-in tool-result offloading

`Strands.Agents.Context.ToolResultOffloader : IPlugin` can be supplied through `AgentOptions.Plugins`. `ToolResultOffloadOptions` defaults to `ThresholdTokens = 1500`, `PreviewTokens = 750`, `MaxRetrievalTokens = 10000` and `RegexTimeout = 100ms` per match. Text estimates use four characters per token; JSON estimates use two. Successful oversized text/JSON results receive a head/tail preview only after passing a UTF-8 payload shrink guard, including a conservative bound for providers that transmit typed JSON directly. This guard can leave a result unchanged even when its token estimate exceeds the threshold. Opaque blocks survive; results with `StructuredContent` are skipped unchanged. Application state and complete durable messages are retained.

The plugin registers rich `retrieve_context` with `reference`, optional `pattern`, one-based inclusive `line_range = { start, end }`, and `context_lines` defaulting to five. Stable native references identify message position/tracking ID and result/block positions. Retrieval uses the invoking agent's history, including restored sessions, and cannot resolve a parent's history through a fresh child. Search implements shared line headers, match markers, gap separators, 200-character patterns and invalid/nested-regex literal fallback. Native additions are a regex deadline and a final textual budget including headers/markers. Invalid ranges return error status with TypeScript error text; empty search follows Python's behavior, while the TypeScript retrieval wrapper treats empty text as non-searchable.

This is a native projection contract: full originals remain in memory and snapshots. It does not enable `auto`/`agentic`, summarization, overflow recovery, or upstream stash reclamation. [Source](../src/Strands.Agents/Context), [behavioral cases](../tests/Strands.Agents.Tests/Context), [shared-fixture provenance](../tests/Strands.Agents.Tests/Context/Fixtures/README.md).

### First-class context and byte storage

`ContextManager(ContextManagerOptions? options = null)` binds to one agent. `ContextManager.From("auto"|"agentic")` resolves modes; null disables the factory result and unknown names fail. Null `Strategies` uses mode defaults; an explicit empty list retains only emergency overflow recovery. `ContextPresets` exposes proactive summarization, large-tool offloading, overflow protection and stale-tool cleanup. Immutable `Offload.Truncate`/`Drop`/`Summarize` strategies select typed targets and `.When(...)` conditions. Policies preserve tool pairs, pins and recent history and refuse non-shrinking transformations. Three bounded context-overflow retries require actual progress.

Default stashing keeps detached originals outside reduced history and exposes `retrieve_context`. `StashEnabled`, `StashStorage`, `RetrievalToolEnabled` and model metadata are configurable. Ephemeral stash data is included in native snapshots; durable backends retain their own values. Agentic mode adds context tools and ephemeral token status. Legacy `ToolResultOffloader` remains a separate projection plugin; registering both retrieval tools under the same name is invalid.

`IStorage` provides `WriteAsync(key, byte[])`, nullable `ReadAsync`, `DeleteAsync` and sorted prefix `ListAsync`. Values are byte-exact. `InMemoryStorage`, `LocalFileStorage`, `Namespace(...)`, optional `ISearchableStorage` and `StorageSearchStrategy` supply built-in storage/composition. Plain views receive subsystem namespaces; explicit namespaced storage can supply an exact root. Local host storage has no remote execution policy by itself. `IModelMetadataProvider`, `IModelTokenCounter` and model extensions expose limits, counting and utilization. Heuristic token counts exclude binary cost and are not billing counts.

### Optional S3 byte storage: `Strands.Agents.S3`

`S3Storage(string bucket, S3StorageOptions? options = null)` lazily owns an official AWS S3 client. `S3Storage(IAmazonS3 client, string bucket, S3StorageOptions? options = null)` borrows the supplied client and rejects a region override. The package targets .NET Standard 2.0, .NET 8 and .NET 10. Options include `Prefix=""`, optional owned-client `Region`, borrowed `SearchStrategy` and `MissingKeyPolicy=NoSuchKeyOrNotFound`; `NoSuchKeyOnly` selects Python's explicit error-code profile. An unclassified HTTP404 or missing bucket is not silently treated as a missing object.

The store implements `ISearchableStorage`, `IStorageCharacteristics`, `IDisposable` and `IAsyncDisposable`. Byte writes and response reads detach buffers. Recursive paginated listings accept only canonical relative keys and require finite continuation progress. A physical S3 prefix does not claim `IsNamespaced`; existing namespace views supply logical composition. Default search scans keywords; indexing follows Put as a separate commit and can fail after the remote write. Disposal closes admission, cancels the owned lifetime and physically joins accepted SDK/body/strategy work before disposing a created owned client. Supplied clients, strategies and caller tokens remain borrowed. Original independent or caller-matching cancellation identity/token/stack and primary-cleanup ordering are preserved. See [storage profiles and limits](S3_STORAGE.md); source/localhost checks do not establish live AWS durability or IAM policy.

### Optional SQLite FTS5/BM25 search: `Strands.Agents.Sqlite`

`Bm25SearchStrategy(Bm25SearchStrategyOptions? options = null)` derives from `StorageSearchStrategy` and implements `IAsyncDisposable`. The strategy requires a direct host `LocalFileStorage`. `DatabasePath` selects an explicit SQLite index; by default the index is a sibling of that storage root. An empty path selects a temporary on-disk index; `:memory:` selects a private in-memory index. The strategy owns its lazy SQLite connection and physically joins admitted SQL work before close/disposal. `CloseAsync` permits later reopening; disposal is terminal. Existing storage ownership remains borrowed. [BM25 contracts](BM25_SEARCH.md) define exact tokenization, copied-byte MD5 indexing, score normalization, namespaces and cancellation/commit boundaries.

`FileMemoryStoreOptions.UseStorageSearch` defaults false and explicitly enables a searchable backend when no explicit memory search strategy wins. Backend search membership and values are captured before asynchronous hydration. Default keyword memory search is unchanged. This optional package implements the pinned Python FTS5/BM25 family; TypeScript QMD filesystem refresh semantics remain separate.

### Cross-session memory: `Strands.Agents.Memory`

```csharp
var memory = new MemoryManager(new MemoryManagerOptions
{
    Stores = [new FileMemoryStore(new FileMemoryStoreOptions
    {
        Name = "preferences",
        Storage = new Strands.Agents.Storage.InMemoryStorage(),
        Extraction = new FileMemoryExtractionOptions
        {
            Triggers = [new InvocationTrigger()]
        }
    })],
    AddTool = new MemoryToolOptions()
});
await using var agent = new Agent(new AgentOptions { Model = model, MemoryManager = memory });
await agent.InvokeAsync("Remember that this project uses native C# and NuGet.");
await memory.FlushAsync();
```

`MemoryStore` is a named retrieval backend; `IWritableMemoryStore` and `IMessageMemoryStore` add optional fact/raw-message writes. Managers require distinct nonempty store names and valid extraction/write capabilities. `SearchAsync(query, MemorySearchOptions?)` preserves selected store order, attributes provenance and isolates backend failures. Limits resolve call override > store default > three. `AddAsync(content, MemoryAddOptions?)` validates every target before I/O, attempts all writes and flattens failures. Omitted store selection means all eligible stores; empty programmatic selection searches none or rejects a write. Caller cancellation propagates.

The default `search_memory` tool returns JSON entries. Optional `add_memory` accepts a nonempty `entries` string array and reports `{ stored }` or `{ accepted }`; its allowlist cannot be widened by tool input. Empty/omitted tool selection uses its complete allowed scope. Read-only manager views remove write capabilities, extraction and backend tools. Default memory injection retrieves at most five entries on fresh user turns, XML-escapes them and appends ephemeral text to the latest user message. Every-turn/predicate/query/format overrides are available; injection failures skip enrichment while caller cancellation propagates. Appended final blocks contribute to `ModelRequest.DynamicTrailingBlocks` for cache placement.

Extraction is opt-in per store. Defaults schedule every five invocation attempts and omit tool traffic. Raw-message stores receive aligned stable sequence numbers; entry-only stores use `ModelExtractor`, resolving explicit model > auxiliary > main. Model extraction renders a role-labeled visible-text transcript and tolerates fenced/prose-wrapped arrays; malformed facts are skipped. Per-store serialized saves keep confirmed progress, retain failed batches and probe every third request after ten failures. `FlushAsync` bypasses scheduling/backoff and awaits accepted work. Failed extraction retains its pending messages for retry; detached `add_memory` failures are logged and do not become extraction retries. Completion does not certify persistence. Canceling a flush stops the wait without aborting accepted writes. Shutdown detaches trigger producers and drains writes before owned model/resource disposal.

`FileMemoryStore` defaults writable, namespaces plain storage under `memory/<name>`, honors explicitly scoped roots, groups facts by heading-derived `.md` keys and serializes same-key appends. Direct searches default to ten keyword matches; custom search affects only that store. File extraction includes existing topic headings and appends its owned JSON/heading contract after configured guidance; an explicit extractor supersedes model/prompt overrides. Stores remain caller-owned, and partial backend commits can produce repeated facts when a failed batch retries. The [memory contracts](../tests/Strands.Agents.Tests/Memory) also run on the Framework compatibility target.

## HTTP providers: `Strands.Agents.Models`

```csharp
public OpenAIModel(HttpClient client, OpenAIModelOptions options);
public OpenAIModelOptions(string modelId, string apiKey);
public OpenAIModelOptions(string modelId,
    Func<CancellationToken, ValueTask<string>> apiKeyProvider);
public AnthropicModel(HttpClient client, AnthropicModelOptions options);
public AnthropicModelOptions(string modelId, string apiKey);
public OpenAIResponsesModel(HttpClient client, OpenAIResponsesModelOptions options);
public OpenAIResponsesModelOptions(string modelId,
    Func<CancellationToken, ValueTask<string>> apiKeyProvider);
public GoogleModel(HttpClient client, GoogleModelOptions options);
```

These adapters implement `IModel`; the caller owns `HttpClient`. Model IDs and fixed API keys are explicit nonempty strings. OpenAI Chat and Responses also accept a caller-owned asynchronous credential delegate in place of a fixed key. It runs once per HTTP request, receives the operation token, and must return a nonempty bearer credential. Cancellation stops waiting even if the delegate ignores its token; eventual failures are observed. Credential failures retain their identity and start no HTTP request. Client default headers remain untouched. Adapters dispose their requests/responses/streams and perform no automatic retries. `Endpoint` is an optional complete HTTP(S) URI; otherwise the client's base address resolves the provider path, falling back to the official API URI when no base address exists. Applications own client timeout, transport handlers, credentials sourcing, and network policy.

`OpenAIModelOptions` has nullable `Endpoint`, positive `MaxTokens` (`max_completion_tokens`), finite `Temperature` in `[0,2]`, `TopP` in `[0,1]`, frequency/presence penalties in `[-2,2]`, JSON-object `Params`, and `CacheConfig`. Native parameters are cloned; managed model/messages/stream/usage fields and dedicated options take precedence. Chat supports text/parallel function streams, image/audio/document user content, rich tool text/JSON with hoisted media, and reported usage. Error tool text receives `[ERROR] `. Upstream Chat reasoning conversion sends reasoning text as assistant text, excluding signatures. Unsupported content fails before HTTP. The Chat adapter exposes ordinary function tools; output audio/video and other provider-specific projections are outside this adapter's documented media contract.

`OpenAIResponsesModelOptions(modelId, apiKey)` supports an explicit endpoint, output-token and sampling limits, cloned native parameters, text format, reasoning configuration, prompt caching, optional response storage and opt-in server-managed history. Stateful agent use stores provider IDs separately from application state, sends only pending history and rejects incompatible local context reducers. Responses-specific text/refusal/reasoning/output blocks retain detached replay fields. Managed request fields remain authoritative. Configured provider-native tools stay provider-executed; opaque output items are retained for replay rather than invoked as local functions. The standalone schema-validated `InvokeStructuredAsync<T>` helper is described below. [Options](../src/Strands.Agents/Models/OpenAIResponsesModelOptions.cs), [stateful cases](../tests/Strands.Agents.Tests/Models/ResponsesStatefulTests.cs).

`GoogleModelOptions(modelId, apiKey)` configures Gemini Developer REST streaming, optional streaming/counting endpoints, cloned `GenerationConfig`/native request parameters and built-in tool definitions. `UseNativeTokenCount` defaults false; native counting failures fall back to heuristic counting while cancellation propagates. Native thought signatures, rich parts, function calls and metadata are retained. Supplied `Params.toolConfig` takes precedence over per-request selection; registered tool declarations and content remain managed. Vertex credential support is available through the optional official-SDK bridge below. The native contracts cover the documented request/content, tool, counting and ownership policies; live-service qualification is separate from deterministic protocol tests. [Options](../src/Strands.Agents/Models/GoogleModelOptions.cs), [wire cases](../tests/Strands.Agents.Tests/Models/GoogleModelTests.cs).

The optional `Strands.Agents.GoogleAuth` package exposes `GoogleCredentialHttpClient.CreateVertexClient(GoogleCredential)` returning `ConfigurableHttpClient`, and `CreateApplicationDefaultVertexClientAsync(CancellationToken = default)` returning `Task<ConfigurableHttpClient>`. Credentials are borrowed and returned clients are caller-owned. Application-default discovery and official refresh use wait-only cancellation. Configure the Vertex endpoint, project and location explicitly; see [Google authentication](GOOGLE_AUTH.md).

`AnthropicModelOptions` has nullable `Endpoint`, `MaxTokens = 64000` (TypeScript baseline; Python requires an explicit bound), `Temperature`/`TopP` in `[0,1]`, optional `ThinkingBudgetTokens`, `StopSequences`, JSON `Params`, `CacheConfig`, empty `Betas`, and `ApiVersion = "2023-06-01"`. Thinking budgets are at least 1024 and below `MaxTokens`. The adapter supports typed tool results, images/PDF/text documents, signed/opaque-redacted thinking, four-mode tool choice, and cache accounting. Provider/model-specific constraints remain authoritative. `AnthropicTools` is a copied list of native server-tool JSON objects; function definitions carrying `input_schema` are rejected there. Native tools follow ordinary functions and precede `Params.tools`; forced function turns omit server tools. Provider `pause_turn` responses continue at most ten times within one stream/model turn, with cumulative usage and no automatic failed-request retry. Thinking replay requires its provider signature; the adapter does not rebind a signature after application changes to its thinking content. `AnthropicModel` implements `IModelTokenCounter`; `UseNativeTokenCount` defaults to `false`. See [Anthropic token counting](ANTHROPIC_TOKEN_COUNTING.md) for `CountTokensAsync(ModelRequest, CancellationToken = default)`, request projection, fallback and ownership.

Both parse SSE, require terminal protocol events, normalize supported reasons/errors, and honor cancellation. Named choice requires a supplied tool. `CacheConfig` is opt-in: `Tools`, `SystemPrompt`, and `Messages` default true; nullable section TTLs override `Ttl`. Anthropic accepts `5m`/`1h`, places managed points on tools/system/latest user prefix, and preserves explicit points when automatic message caching is disabled. OpenAI uses `CacheKey`, or `strands-<SessionId>` when `UseSessionKey = true` (default), and maps exact `in_memory`/`24h` retention literals; placement settings have no OpenAI wire equivalent. Explicit native cache fields win. Fixtures do not establish live-service compatibility. [Options](../src/Strands.Agents/Models/ProviderOptions.cs), [wire cases](../tests/Strands.Agents.Tests/Models/ProviderParityTests.cs).

### Typed provider configuration

Native HTTP adapters expose `IConfigurableModel<TOptions>` with `GetConfig()` and `UpdateConfig(Func<TOptions,TOptions>)`. The options are immutable records, so `model.UpdateConfig(options => options with { Temperature = .25 })` changes selected settings while preserving the rest. JSON and collection fields are detached from caller-owned documents/lists. Each transformation runs once; validation or callback failures leave the prior configuration unchanged. Successful concurrent updates publish in completion order. Entered streams retain their captured configuration; subsequent requests, token counters and metadata use the replacement. The client and asynchronous credential callbacks remain borrowed.

This capability covers Chat/Responses, Anthropic, Google, Ollama, Mistral, Writer, llama.cpp and Bedrock. `GetConfig` returns actual configuration, including explicitly supplied credentials; HTTP options' `ToString` representations omit credentials and native parameter contents. Changing Responses' server-history ownership currently requires a new adapter and agent. This is an explicit native history-ownership policy. Bedrock updates inference options while preserving the original AWS client, lifetime and construction-time region; changing its region requires a new model, matching the source distinction between model configuration and transport construction. [Contract](../src/Strands.Agents/ModelConfiguration.cs), [portable HTTP update cases](../tests/Strands.Agents.Tests/Models/ModelConfigurationTests.cs), [portable Bedrock update cases](../tests/Strands.Bedrock.Tests/BedrockModelConfigurationTests.cs).

### Native structured provider helpers

Responses, Google and Ollama expose `InvokeStructuredAsync<T>(ModelRequest, StructuredOutputOptions<T>, CancellationToken)` in addition to the Mistral/Writer/llama.cpp helpers. Each makes one bounded non-streaming request, ignores ordinary per-request tool selection, validates the resulting JSON schema and invokes the common typed/custom decoder. They execute no local tools and add no retry. `MaxResponseBytes` defaults to 16 MiB. A reported incomplete finish cannot become success merely because its partial JSON validates.

`OpenAIResponsesModel.InvokeStructuredAsync<T>` sends native `text.format` JSON schema, preserves configured provider-side tools and sibling text settings, and validates/decodes returned JSON with the common typed/custom decoder. It does not advance managed response history and rejects a request carrying existing `ModelState` or a conflicting text format. Native function arguments may be parsed but are never executed locally. See the [helper](../src/Strands.Agents/Models/OpenAIResponsesStructuredOutput.cs) and [structured-output contracts](../tests/Strands.Agents.Tests/Models/OpenAIResponsesStructuredOutputTests.cs).

Google selects `:generateContent`, removes only the streaming `alt` query parameter and preserves other route parameters. A custom streaming route that cannot be derived requires `StructuredOutputEndpoint`; explicit endpoints are validated before configuration publication. `generationConfig.responseMimeType` and `responseJsonSchema` override conflicting configured output formats, while configured provider-side tools remain available. Response thoughts are excluded from the JSON text. Ollama sends its schema in `format` with `stream = false`, preserving native generation settings. See the [Google protocol](https://ai.google.dev/gemini-api/docs/generate-content/structured-output), [Ollama protocol](https://docs.ollama.com/capabilities/structured-outputs), [Google cases](../tests/Strands.Agents.Tests/Models/GoogleStructuredOutputTests.cs) and [Ollama cases](../tests/Strands.Agents.Tests/Models/OllamaStructuredOutputTests.cs). Native helpers do not establish live-service or exhaustive provider parity.

### OpenAI-compatible Amazon Bedrock

`Strands.Agents.Bedrock.BedrockMantleConfig` derives AWS endpoints and a dynamic bearer credential delegate for the native Chat/Responses adapters. `Region` defaults through `AWS_REGION`, `AWS_DEFAULT_REGION`, then the AWS SDK region chain. `Credentials` optionally borrows an `AWSCredentials` instance; otherwise the AWS SDK's standard credential chain resolves it. Credentials are refreshed for each token and never disposed by the helper. `ExpiresInSeconds` defaults to 43200 and accepts 1–43200, capped by known refreshable-credential expiration. `Endpoint` selects Mantle by default or Bedrock Runtime for the Python endpoint-family capability.

`ResolveEndpoint(modelId, apiPath = "responses")` accepts only `responses` or `chat/completions`. Mantle routing follows the pinned per-model-family prefixes; Runtime always uses `/openai/v1/`. Region validation precedes credential resolution. `CreateTokenProvider()` returns the delegate used by the new options overloads. It signs the official version-1 Bedrock bearer-token envelope using the AWS SDK's SigV4 signer, with no subprocess or Python dependency. Configuration diagnostics exclude the credential object. Fixed signing vectors and deterministic credential tests verify routing/signing/cancellation, without claiming live AWS authorization. [AWS token protocol](https://github.com/aws/aws-bedrock-token-generator-js/blob/main/src/token.ts), [native helper](../src/Strands.Agents.Bedrock/BedrockMantleConfig.cs), [tests](../tests/Strands.Bedrock.Tests/BedrockMantleTests.cs).

### Mistral chat

`MistralModel(HttpClient, MistralModelOptions)` borrows its transport. Options require a model ID; a null key resolves `MISTRAL_API_KEY` on each request. Sampling is bounded to `[0,1]`, `MaxTokens` maps to `max_tokens`, and `Stream = false` converts a complete JSON response into the common event sequence. `CacheConfig` maps its routing key without retention/placement fields. Complete JSON responses default to a configurable 16 MiB `MaxResponseBytes` bound; streaming uses the shared bounded SSE reader.

The adapter retains pinned Python formatting, ignored ordinary tool-choice policy and first-appearance tool ordering. `InvokeStructuredAsync<T>(ModelRequest, StructuredOutputOptions<T>, CancellationToken)` forces one extraction tool with `tool_choice = "any"`, disables parallel tools/streaming, then applies the same JSON schema and typed decoder as agent structured output. It rejects missing/unrequested/multiple calls and performs no retry or tool execution. Native HTTP/stream overflow errors and cancellation preserve the common typed contracts and cleanup diagnostics. Typed configuration updates are supported. These documented native policies and deterministic protocol tests do not establish live-service execution. [Adapter](../src/Strands.Agents/Models/MistralModel.cs), [tests](../tests/Strands.Agents.Tests/Models/MistralModelTests.cs).

### Writer chat

`WriterModel(HttpClient, WriterModelOptions)` borrows its client and uses Writer's `/v1/chat` protocol. A null key resolves `WRITER_API_KEY` per request. `palmyra-x5` accepts ordered text/image blocks; other model IDs retain the pinned single-nonempty-text behavior. Stop sequences are copied at construction, empty tool lists are omitted, ordinary tool choice/cache controls are ignored with diagnostics, and optional `IncludeUsage` controls `stream_options`. Tool output preserves first-appearance order, closes the text block before tool blocks and reports late usage after the stop event. Unlike the pinned Python bug, native decoding retains arguments contained in the first tool fragment.

`InvokeStructuredAsync<T>` uses one non-streaming `response_format: json_schema` request without tools or `stream_options`, then validates and decodes the returned JSON text through the common structured-output parser. Complete responses are bounded by `MaxResponseBytes` (16 MiB by default). Writer overflow phrases map to the shared typed exception. Invalid/malformed responses are not retried; cleanup errors retain the primary error. Typed configuration updates are supported. Live-service execution requires separate qualification. [Writer adapter](../src/Strands.Agents/Models/WriterModel.cs), [portable cases](../tests/Strands.Agents.Tests/Models/WriterModelTests.cs), [official endpoint implementation](https://github.com/writer/writer-python/blob/main/src/writerai/resources/chat.py).

### Ollama chat

`OllamaModel(HttpClient, OllamaModelOptions)` borrows the client's transport and authentication settings. `OllamaModelOptions(modelId)` supports an explicit endpoint, copied native generation options/additional arguments, sampling and output limits, stop sequences, keep-alive values and native JSON format. The endpoint defaults to `api/chat` relative to the client base address, or `http://localhost:11434/api/chat`. Dedicated generation options override native options; managed model, conversation, tool and streaming fields override additional arguments. The pinned Python adapter instead applies additional arguments last.

Requests flatten each text/image/tool block into native messages. NDJSON decoding preserves fragmented UTF-8, validates terminal/tool/usage fields and keeps native tool-call/result correlation. Text/tool/length completion, provider metadata, context-overflow classification, cancellation and owned response cleanup are covered by deterministic portable tests. The model does not acquire client ownership or retry requests. Typed configuration updates, the native structured-output helper and harness identifier resolution are supported. The adapter validates tool choice but does not emit native `tool_choice` or automatic cache-placement fields. [Options](../src/Strands.Agents/Models/OllamaModelOptions.cs), [wire cases](../tests/Strands.Agents.Tests/Models/OllamaModelTests.cs).

## Bedrock provider: `Strands.Agents.Bedrock`

```csharp
public BedrockModel(BedrockModelOptions? options = null);
public BedrockModel(IAmazonBedrockRuntime client, BedrockModelOptions options);
// BedrockModel also implements IConfigurableModel<BedrockModelOptions>, IModelTokenCounter and IDisposable.
```

The separate provider project uses `AWSSDK.BedrockRuntime` 4.0.101.12. Core consumers need not reference it; the harness references it for its native default. Convenience construction lazily creates an owned AWS client using the credential chain; supplied AWS clients remain caller-owned. Disposing the model interrupts owned operations and disposes only an owned client. The convenience client disables automatic AWS request retries; supplied-client retry policy belongs to the caller.

`Streaming` defaults to true; false uses native `ConverseAsync` and projects the completed response into the same model-event contract. `Guardrail`, `PerformanceLatency`, `ServiceTier`, `PromptVariables`, `RequestMetadata`, `AdditionalModelResponseFieldPaths` and nullable `IncludeToolResultStatus` expose additional native request settings. [Request-option behavior](BEDROCK_REQUEST_OPTIONS.md), [Converse response ownership](BEDROCK_NON_STREAMING.md) and [native token counting](BEDROCK_TOKEN_COUNTING.md) describe their supported scopes and explicit policies.

BedrockModelOptions.Cache optionally selects independent tools/system/message sections and shared/per-section open TTLs through BedrockCacheOptions. Null preserves legacy flags; an explicit profile takes authority. Auto uses exact case-sensitive family matching, AutoIgnoreCase folds ASCII, and Anthropic forces the profile for an opaque identifier. See [cache profiles](BEDROCK_CACHE_PROFILE.md).

Guardrail trace assessments emit `ModelRedactionEvent` for blocked/detected policies. Input redaction defaults on and output redaction defaults off; both have configurable replacement text. Masking and undetected assessments do not trigger replacement. [Trace authority and fallback](GUARDRAIL_REDACTION.md) apply to streaming and native Converse. `StrictTools` enables non-mutating native strict tool schemas; [closure and reference behavior](BEDROCK_STRICT_TOOLS.md) describes preserved constraints and transformation bounds.

`BedrockModelOptions` defaults to `ModelId = "global.anthropic.claude-sonnet-4-6"`, `Region = "us-west-2"`; nullable `MaxTokens`, `Temperature`, `TopP`, `StopSequences`, and JSON-object `AdditionalModelRequestFields`; cache-system/tools/last-message flags false; nullable `CacheTtl`; `RequestTimeout = 2 minutes`; `MaximumBufferedContentBytes = 32 MiB`; and `MaximumContentBlocks = 1024`. These native timeout/buffering defaults require review and are not asserted upstream defaults. `RequestTimeout = null` disables the local deadline. Converse mapping supports tools/choice, rich content, reasoning, cache points, usage, and classified errors; unsupported native content fails explicitly. [Source](../src/Strands.Agents.Bedrock/BedrockModel.cs), [protocol cases](../tests/Strands.Bedrock.Tests/BedrockProtocolTests.cs), [lifecycle cases](../tests/Strands.Bedrock.Tests/BedrockLifecycleTests.cs).

## Native execution packages

`Strands.Agents.Monty` runs the genuine pinned Monty 0.0.23 interpreter in a bounded worker process and supplies the harness's default `programmatic_tool_caller`. Registered tools are async guest functions; inner calls use the agent's direct tool hook path, preserve invocation state and refuse unanswered approval. The interpreter cannot acquire host filesystem/process/network access outside the exposed tools. Guest limits, cancellation, worker reaping and deferred uncooperative-tool cleanup are documented in the [package README](../src/Strands.Agents.Monty/README.md).

Optional `Strands.Agents.Cedar` supplies `CedarAuthorization` using the official Cedar 4.12.0 policy engine and 0.6.1 MCP schema generator. Explicit options select the TypeScript/Python schema-aware evaluation and engine-error policies. See its [package contract](../src/Strands.Agents.Cedar/README.md).

Both native packages require genuine Windows x86/x64, Linux x64 and macOS x64/ARM64 assets with their notices; the final normal feed contains those advertised assets. Their Rust MSVC targets require Windows 10+/Server 2016+, including when hosted by Framework 4.8; the managed TFM does not widen the native OS floor. The intended Linux distribution floor is glibc 2.31, verified by a separate build image. Supplemental Ubuntu 22.04 builds have a different ABI floor and remain quarantined. Source-specific tests, executable permissions, fresh NuGet consumers and genuine Framework 4.8 runtime execution retain separate evidence; consult [validation results](VALIDATION_RESULTS.md). The core SDK package itself does not require either native package.

## Google Gemini Live authentication

`GoogleGeminiLiveModelOptions.UseVertexAi` defaults to `false`. The existing
Developer API key, model-prefix and endpoint defaults remain unchanged. Vertex
mode requires an explicit absolute WebSocket `Endpoint`, a full
`projects/{project}/locations/{location}/publishers/google/models/{model}`
`ModelId`, and `[JsonIgnore] Func<CancellationToken, Task<string>>? AccessTokenProvider`.
The callback defaults to null and is borrowed. It resolves a fresh bearer token
for each physical attempt, including service resumption and fresh fallback.
Vertex ignores API keys and sends no `key` query field. `GetConfig` retains the
callback reference, omits resolved tokens, and clears the ignored Vertex API key.

Credential resolution precedes old-connection replacement and runs outside the
model state lock. A credential failure does not enter service fallback. Caller,
disposal and setup-deadline cancellation physically await the callback and are
rechecked before socket connection. A noncooperative callback can exceed the
cooperative deadline. Same-model lifecycle reentry from that callback fails
before mutation. Vertex retains acknowledged text history through `clientContent`
and rejects the unsupported `Params.historyConfig` field before I/O. See
[credential composition](GOOGLE_AUTH.md#vertex-ai-live-websockets) and the
[Live adapter](../src/Strands.Agents.Bidi/README.md#google-gemini-live).

## Bidirectional deferred cancellation

Optional `Strands.Agents.Bidi` adds the following request members and output record:

```csharp
namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent : IAsyncDisposable
{
    public CancellationToken CancelSignal { get; }
    public void Cancel(string? message = null, bool afterCurrentTools = false);
}

public sealed record BidiConnectionStopEvent(
    string ConnectionId, string Reason) : IBidiOutputEvent;
```

`Cancel` requests an end at the completed-tool-group boundary. It does not cancel provider or tool tokens. The group settles its admitted siblings, result callbacks and completed-history notification, retains that history, suppresses its provider result batch and publishes one `user_request` event for the conversation. The connection ID comes from an accepted current connection-start event, otherwise `"unknown"`. `ReceiveAsync` yields this terminal and exits; its caller still owns actual Stop/disposal. `RunAsync` awaits every terminal sink before its ordinary physical cleanup.

`CancelSignal` is a read-only BCL token independent of the conversation lifetime; a captured current token observes a subsequent request. Actual `StopAsync` keeps immediate cancellation and joins tools/readers/Run input before returning, then replaces the request state even when idle or teardown fails. A caller-cancelled Stop that never enters lifecycle cleanup retains the request. A callback on the captured signal can call Stop; retirement waits for in-flight cancellation callbacks before disposing that signal's owned handle. After Stop, callers obtain the fresh current signal. `message` is retained privately for future use, the last nonnull value wins, and no request text is appended to history. `afterCurrentTools` is accepted for Core signature consistency; Bidi requests are always deferred.

The bounded BG01 selection passes 11/11 on each modern target and 12/12 on each installed Framework 4.8.1 architecture, including the architecture witness. The same normal AnyCPU net48 Bidi bin also passes 12/12 on genuine Framework 4.8 x64 and 12/12 on x86, with pointers 8/4, release 528049 and runtime 4.8.4110.0; neither guest profile builds or restores. These are bounded BG01 cases. Whole Bidi provider/telemetry behavior, current Linux/macOS, full suites and final package/API/ABI checks remain separate. See [parity scope](PARITY.md#multi-agent-orchestration-and-bidirectional-models--partial) and [execution evidence](VALIDATION_RESULTS.md#bidirectional-deferred-stop--bounded-bg01).

## Bidirectional identity and telemetry

`BidiAgentOptions.Name` and `Description` configure the agent identity. `BidiAgent.Name` uses `"Strands Agents"` for null or empty names and preserves other configured names, including whitespace and Unicode. `Description` preserves its optional value.

Bidirectional sessions export session, connection, response, restart and real-tool spans through SDK tracing. They include configured agent/model identity, additive token/cache usage, first-audio latency and barge-in events. OpenAI Realtime and Google Gemini Live implement `IModelMetadataProvider`; custom model metadata remains optional. Completion follows owned reader/tool cleanup and retains caller activity and borrowed resources.

## MCP: `Strands.Agents.Mcp`

Harness `McpServerOptions` adds optional `ApplicationName`, `ApplicationVersion` and `StartupTimeout`; MCP JSON uses `applicationName`, `applicationVersion` and `startupTimeoutMs`. Omitted values preserve the server-name identity, version `1.0.0` and the native official 60-second initialization default. An explicit startup budget covers connector admission and protocol initialization cooperatively and waits for owned cleanup before settling. See [Harness MCP declarations](../../harness-dotnet/docs/MCP_TOOL_PROVIDERS.md#application-identity-and-startup-budget).

```csharp
public static Task<McpClient> McpClient.CreateAsync(IClientTransport transport,
    McpClientOptions? options = null, CancellationToken cancellationToken = default);
public static Task<McpClient> McpClient.CreateHttpAsync(HttpClient httpClient,
    HttpClientTransportOptions transportOptions, McpClientOptions? clientOptions = null,
    CancellationToken cancellationToken = default);
public static Task<McpClient> McpClient.CreateStdioAsync(StdioClientTransportOptions transportOptions,
    McpClientOptions? clientOptions = null, CancellationToken cancellationToken = default);
public Task<IReadOnlyList<McpTool>> McpClient.ListToolsAsync(
    McpToolListOptions? options = null, CancellationToken cancellationToken = default);
```

This adapter uses official `ModelContextProtocol.Core` 2.2.0 types and transport/session logic. `McpClient` owns its protocol session, implements `IAsyncDisposable`, exposes `IsConnected` and the official `Protocol` client, and retains caller-owned HTTP clients. HTTP supports the official Streamable HTTP/SSE/auto modes; stdio owns the launched server session. Discovery defaults to 64 pages/10,000 received tools, rejecting loops/duplicates/malformed definitions. Optional prefix/filter/progress settings retain original server names. `McpTool : IRichTool` preserves definition/output-schema/annotations, validates successful structured results, and maps supported text/JSON/media/resources. Unsupported adapted content fails explicitly; `CallToolAsync` retains the complete official `CallToolResult`, optional request options, and progress observer. Annotations do not grant permission. Harness portable MCP settings own acquired connections and enforce delegated server grants independently of ordinary tool grants. Stdio uses the selected sandbox workdir and configured environment; portable tests launch a staged native Framework executable. [Client](../src/Strands.Agents/Mcp/McpClient.cs), [harness bindings](../../harness-dotnet/src/Strands.Harness/Mcp), [stdio/SSE cases](../tests/Strands.Agents.Tests/Mcp/McpTransportTests.cs), [Framework/real-HTTP cases](../tests/Strands.Compatibility.Tests/McpCompatibilityTests.cs).

`McpTasksConfig.ObservationMode` defaults to `McpTaskObservationMode.Polling`. Opt-in `NotificationsAndPolling` lets finalized automatic tool tasks consume validated same-task notifications with polling as a fallback. Malformed and unrelated notifications cannot settle the invocation. The default enum value is omitted from JSON; default polling and legacy protocol selection retain their existing behavior. Notification registration and owned observation work retire during operation cleanup.

## Harness: `Strands.Harness`

```csharp
public static Task<Agent> Harness.CreateAsync(HarnessOptions? options = null,
    CancellationToken cancellationToken = default);
public static string HarnessPrompt.Build(string? instructions = null,
    IReadOnlyList<string>? contextParts = null);
public interface ISandbox
{
    ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    ValueTask WriteTextAsync(string path, string content,
        CancellationToken cancellationToken = default);
}
public LocalFileSandbox(int maxReadBytes = 5 * 1024 * 1024, LocalShellOptions? shellOptions = null);
public static ITool FileTools.CreateRead(ISandbox sandbox);
public static ITool FileTools.CreateWrite(ISandbox sandbox);
public static ITool FileTools.CreateEdit(ISandbox sandbox);
```

`HarnessOptions` accepts explicit models or `ModelId`, mapped effort/caching/search settings, `ModelFactory`/`ModelResolver`, auxiliary models, instructions, application tools/plugins, built-in selections, interventions, a selected sandbox and session/context/memory/skills settings. Core model options take precedence; supplied models remain caller-owned unless core ownership explicitly says otherwise. Missing model settings choose lazy owned Bedrock `Harness.DefaultModel = "bedrock/global.anthropic.claude-opus-5"`. Native factory identifiers support Bedrock, Bedrock Mantle, OpenAI, Anthropic, Google, Ollama and a LiteLLM proxy; the separate `Harness.ResolveModel` convenience method still accepts only `bedrock/model`. `ModelFactory` receives mapped native settings and `ModelResolver` supplies preconfigured models.

Built-in names are `shell`, `read`, `write`, `edit`, `web_fetch`, `web_search`, `programmatic_tool_caller` and `subagent`. Omitted selection requests all eight; provider-native search may be disabled for unsupported prebuilt models unless explicitly requested. Exa is an explicit alternative. Shell and curl-backed web fetching require an `ICommandSandbox`; a file-only custom sandbox must disable those command operations. Native plugins default to `todos`/`environment`. Empty selections disable built-ins/plugins; exact names and normalized collision checks apply. The Monty package supplies actual programmatic execution.

Explicit core model, prompt, auxiliary model, session store, and session ID take precedence where supplied. The remaining core limits/hooks/context/history options pass through. Without core options the agent name is `"strands_harness"`. `HarnessPrompt.Contract` is the shared behavioral prompt; domain instructions and optional context parts are joined with blank lines. The returned object is a core `Agent`.

`HarnessAgentConfig.Parse(string)`/`Normalize(JsonElement)` validate the portable camelCase schema and normalize defaults/module metadata. Unknown top-level keys are dropped with a sorted trace warning; recognized nested settings remain strict. Duplicate raw JSON properties are rejected as an explicit native parser choice. Its portable display-name default is `"Strands harness"`, distinct from the direct factory's `"strands_harness"`. `ToJsonElement`/`ToJson(bool indented = false)` return owned representations. `ToOptions(HarnessConfigBindings? bindings = null, string? baseDirectory = null)` binds model policies, memory, skills, context, sessions, interventions and programmatic calling. List/`*`-mapping edits and relative paths are supported. Typed resolvers supply model/tool/subagent/plugin/sandbox/intervention modules and ordered agent-configuration patches. Top-level model and normalized settings retain precedence over patches; changing already-bound model policy requires rebinding. Use the optional [managed assembly loader](../../harness-dotnet/docs/MANAGED_MODULES.md) for declared trusted C# exports; these delegate bindings do not execute Python/TypeScript modules. Unsupported enabled capabilities fail explicitly. [Config source](../../harness-dotnet/src/Strands.Harness/Configuration/HarnessAgentConfig.cs), [configuration cases](../../harness-dotnet/tests/Strands.Harness.Tests/ConfigurationTests.cs), [module cases](../../harness-dotnet/tests/Strands.Harness.Tests/HarnessConfigurationModuleTests.cs).

`Todos` adds `todo_write`, per-agent durable task state, and ephemeral reminders. `EnvironmentContext` adds date/platform/workdir/project instructions through optional `IEnvironmentSandbox` discovery; absent discovery falls back to date only. Discovery is memoized per agent and root `AGENTS.md` injection is capped at 16,000 characters. `AgentSkills` adds progressive metadata discovery and activation from typed, YAML and HTTPS sources; harness discovery defaults to selected sandbox `./.agent/skills`, and an explicit empty selection disables it. [Plugins](../../harness-dotnet/src/Strands.Harness/Plugins), [skills](../src/Strands.Agents/Skills), [plugin cases](../../harness-dotnet/tests/Strands.Harness.Tests/PluginTests.cs), [skill bindings](../../harness-dotnet/tests/Strands.Harness.Tests/SkillsHarnessTests.cs).

`HarnessMemoryOptions` defaults enabled with directory `./.agent/memory`. Unless an explicit core manager, supplied manager or store list wins, the harness creates file memory over the selected filesystem's byte operations, enables extraction and every-turn recall, registers search and leaves add-memory opt-in. Text-only custom sandboxes require explicit storage rather than implicit host access. Harness-built memory becomes read-only for delegated agents; caller-supplied managers are not automatically forwarded. Explicit auxiliary models win; ready main-model instances are reused, while known string/default provider identifiers derive a separate inexpensive model. Acquired auxiliary resources follow agent ownership and cleanup. [Memory bindings](../../harness-dotnet/tests/Strands.Harness.Tests/MemoryHarnessTests.cs), [byte storage](../../harness-dotnet/src/Strands.Harness/Storage).

`HarnessContextOptions` defaults enabled in auto mode and supports agentic mode or a supplied manager. Explicit core context/manager plugins and an explicit legacy conversation manager take precedence over the convenience default. Disabling convenience context leaves explicit core settings in force. Delegates get fresh manager bindings: auto/agentic/off selections are preserved, while a supplied manager resolves to a fresh auto manager rather than sharing its bound instance. Server-managed history requires disabling incompatible local context management.

`WebFetch.Create` and the default `web_fetch` built-in use curl through the selected `ICommandSandbox`. `WebFetchConfig.Transport = Direct` explicitly selects HTTP in the harness process and may borrow a supplied client. The selected sandbox's environment capability or an explicit shell setting determines command language; host execution is never substituted for an unavailable sandbox capability. Fetches default to thirty seconds, five MiB of consumed body and 50,000 returned UTF-16 characters. Redirects remain HTTP(S), encoded capture is validated and caller sandbox output hard limits prevail. Native curl consumption is bounded before full spooling, a stricter contract than the pinned source. Raw pages are cached by original URL for fifteen minutes; each nonempty summarization prompt uses a fresh conversation. Default children get separate caches, while explicitly supplied summarizer models and clients remain borrowed. [Web options](../../harness-dotnet/src/Strands.Harness/Tools/WebFetch.cs), [transport cases](../../harness-dotnet/tests/Strands.Harness.Tests/WebFetchTests.cs). Checkpoint ff35aac0 includes real POSIX curl and clean Framework execution of this transport.

`Strands.Harness.Tools.Subagent.Create(SubagentOptions)` returns an `IRichTool`. Its required builder receives resolved `AgentSpec`, `ToolContext`, and cancellation. `Fixed`/`Inherit`/`Open`/`Choice` authorities, named `Option`/`Preset` values, and the shared generalist role produce a closed input schema. Defaults: name `subagent`, depth two, open instructions, inherited model, fixed `none` context, and tool choices limited by parent grants. Context modes are `none`, `all`, and `no_tools`; shared history preserves complete exchanges and omits unsafe pending/reasoning content. Standard harness children are fresh, preserve core limits, isolate state/hooks, and cannot reacquire excluded plugin tools or widen MCP-server grants. Failures/limits produce correlated error results; cancellation propagates. The tool implements `IStreamingTool`, forwards nonfinal child events and preserves supported human-answer pause/resume through native parent sessions, including fresh-parent restoration without replaying completed child effects. Its [stream and interruption contracts](../../harness-dotnet/docs/SUBAGENT_INTERRUPTS.md) retain explicit context, authority and ownership limits. [Contracts](../../harness-dotnet/src/Strands.Harness/Tools/SubagentTypes.cs), [delegation cases](../../harness-dotnet/tests/Strands.Harness.Tests/SubagentTests.cs).

`SessionOptions` defaults to `Enabled = true`, `Id = null`, and `Directory = "./.agent/sessions"`. When no explicit core store exists, enabled sessions create a file store. An omitted ID generates eight hex characters; convenience IDs are trimmed/lowercased and non-ASCII-alphanumeric characters other than `_`/`-` become `-`. An explicit core ID is preserved. Disable convenience persistence with `Enabled = false`; that does not disable an explicit core store.

The default filesystem uses host-process permissions, requires absolute host paths, and rejects lexical `..` segments. It has no root confinement, isolation, or approval policy. Reads are strict UTF-8 and bounded to 5 MiB by default. The `read` tool defaults to one-based `offset = 1` and `limit = 2000`, allowing at most 10,000 returned lines. Writes create parents, stage/flush a sibling file, check cancellation, and replace the destination. Terminal symlink/reparse destinations are rejected; parent-directory links and filesystem races remain outside this validation. Modern .NET assets copy existing Unix mode bits and create new Unix files with owner read/write permissions; the .NET Standard asset uses platform permission defaults. Windows metadata merge errors are not ignored. Ownership/extended Unix ACL preservation and crash recovery on arbitrary filesystems are not guaranteed. Edits require one nonempty exact match and use read/replace/write without a concurrent-writer transaction. Supply an enforcing `ISandbox` or disable file tools for other access policies.

### Native shell execution

`Strands.Harness.Tools.Shell.Create(ICommandSandbox, ShellConfig?, string name = "shell")` returns an `IRichTool`. Inputs are `command` and optional positive `timeout` seconds (default 120). A completed command returns JSON `output`, `error` and `exit_code`; a nonzero exit remains a completed tool execution. Timeouts produce error status, exit code 124 and captured partial output. Caller cancellation propagates. `HarnessOptions.Shell.Description` can replace the shared description; process limits belong to the command sandbox.

`ICommandSandbox : ISandbox` adds `ExecuteAsync(command, ShellExecutionOptions?, CancellationToken)`. `LocalFileSandbox` implements it using fresh system Windows PowerShell or modern Unix `/bin/sh` processes. `LocalShellOptions` defaults to a two-minute maximum and combined 1 MiB UTF-8 capture, with optional working directory, environment and compatible executable. Both pipes drain concurrently; excess captured bytes are discarded and marked. File-only custom sandboxes do not acquire host command execution.

Modern assets use the platform's process-tree termination API, which cannot guarantee termination of already-orphaned descendants; output drain/cleanup is bounded. The .NET Standard path supports legacy Windows through a kill-on-close Job Object and a bounded release handshake; user command code stays opaque until attachment succeeds. Unsupported legacy platforms and failed job attachment fail before command release. This remains host-process authority, without isolation or an approval policy. [Execution contracts](../../harness-dotnet/src/Strands.Harness/ProcessSandbox.cs), [tool](../../harness-dotnet/src/Strands.Harness/Tools/Shell.cs), [process tests](../../harness-dotnet/tests/Strands.Harness.Tests/ProcessSandboxTests.cs).

## End-to-end composition

```csharp
using Strands.Agents.Models;
using Strands.Harness;

static async Task<string> AskAsync(HttpClient client, string modelId, string apiKey,
    CancellationToken cancellationToken)
{
    var model = new OpenAIModel(client, new OpenAIModelOptions(modelId, apiKey));
    await using var agent = await Harness.CreateAsync(new HarnessOptions
    {
        Model = model,
        Instructions = "Answer briefly.",
        BuiltinTools = [],
        Session = new SessionOptions { Enabled = false },
    }, cancellationToken);

    return (await agent.InvokeAsync("Explain the role of a tool schema.",
        cancellationToken: cancellationToken)).ToString();
}
```

Reviewers should challenge the cancellation result contract, cleanup diagnostic mechanism, hook ordering, shared child dependencies, message-based window semantics, session ownership, provider package placement, and harness/core precedence. These are explicit alpha decisions, not established cross-language parity or a completed compatibility freeze.

## Optional Harness delegation helpers

`SwarmTool.Create(SwarmToolOptions? options = null)` returns `IRichTool` with streaming support. It accepts a model-defined task and team under developer authority and returns named participant text or a correlated error with partial output. Human-answer continuation retains the owned team in memory under its parent; it does not survive a process restart. See [Swarm tool](../../harness-dotnet/docs/SWARM_TOOL.md).

`Option.Null(string name, string description = "")` creates an authority choice whose `Value` is null. The existing `Option(string name, object? value = null, string description = "")` constructor keeps its null-to-name fallback. This factory does not change legacy constructor-based JSON deserialization. See [explicit null choices](../../harness-dotnet/docs/OPTION_NULL.md).

## Native graph result data

`NodeResult` and `NodeResultUpdateEvent` add read-only nullable `AgentResult` and
`MultiAgentResult` properties for the original accepted result references. The
old eight-argument and six-argument constructors and their optional defaults
remain available; additional all-required overloads supply the two exclusive
arms. The new properties have `JsonIgnore`, retaining ordinary result JSON and
avoiding implicit serialization of live caller state or telemetry.

Graph snapshots emit native schema `1.1` only when typed result data is present,
and continue accepting legacy `1.0` snapshots without those arms. The explicit
safe envelope preserves the inner message, checkpoint and structured JSON
presence, with fresh local metrics/state on restoration. Typed capture has a
64-result-level budget; JSON depth limits apply independently. See
[typed graph result and snapshot contracts](MULTIAGENT_GRAPH.md#typed-result-references-and-snapshot-data).
These data properties do not change graph text rendering or Swarm policies.

## Named orchestration plugins and session phases

`MultiAgentPlugin(string name, Func<IMultiAgent, MultiAgentHookRegistry, CancellationToken, ValueTask> initialize)` declares a borrowed named initializer. `GraphOptions.Plugins` and `SwarmOptions.Plugins` copy ordered membership; each receiving orchestrator awaits initialization once and memoizes its outcome. The supplied local registry composes with caller hooks using ascending priorities and the existing reverse registration order for equal-priority after callbacks. Plugin objects and captured resources remain caller-owned. Names use ordinal identity; empty and whitespace names are permitted, while duplicate names are rejected. Plugin declarations are omitted from options JSON.

`MultiAgentSessionRestoreProfile.Native` is the default and restores configured sessions before user plugin initialization. `TypeScriptSnapshot` wires lazy restoration after plugins and before `Initialized`; `PythonRepository` restores at the Python manager position during `Initialized`; `PythonSnapshot` wires the first-`BeforeInvocation` read at that position. The Python profiles discard terminal run state on automatic restore, while paused work remains resumable and manually loaded snapshots retain their explicit semantics. Nondefault profile values are serialized, and default Native values are omitted. These options select native snapshot-store lifecycle policies, not foreign serialized-session interchange.

`SwarmOptions.BaselineCapturePhase` defaults to `BeforePlugins`; select `AfterPlugins` to capture missing reset baselines after plugin initialization and before `Initialized`. This setting is independent of routing and `SessionRestoreProfile`; the existing Python presets do not silently change those two phase selectors.

`AgentContextPolicy.ResetToConstructionBaselineOnRevisit = 3` is an explicit `AgentNodeOptions.ContextPolicy` for `GraphBuilder.AddNode(Agent, ..., options)`. AddNode captures messages, application `State` and `ModelState`. First entry keeps current post-plugin state; after a completed visit, Graph restores the captured fields before `BeforeNodeCall`, preserving changes made by that hook. Existing policy values/defaults remain unchanged. Graph rejects this value as its global `AgentContext` and rejects direct nodes using it without builder capture; Swarm and standalone AgentNode execution reject it. Focused evidence covers inactive stateless agents without sessions. Broader stateful, persistent and nested combinations need their own workflow qualification; this option does not promise arbitrary orchestration/storage-policy combinations.

## Experimental execution checkpoints

`AgentOptions.CheckpointBehavior` selects Disabled (default), TypeScriptRegenerate or PythonReplay. `InvocationOptions.CheckpointResume` accepts an `AgentCheckpoint` when invoking the same or separately restored conversation with a null prompt and no additional user input. The marker carries position and cycle metadata, not conversation/provider state or resume authority. Typed preemption can expose its actual `AgentResult` in `StructuredOutputException.Result`; the existing `(string, Exception?)` constructor remains available. See [the execution contract](CHECKPOINT_EXECUTION.md) for ordering, cancellation, persistence and source-specific behavior.

SDK-owned fresh default subagent/Swarm builders use Disabled rather than inheriting the parent's checkpoint policy. This does not change manual CreateChild, borrowed AsTool or caller-supplied builders.

## Python orchestration text projection

`NodeResult.GetAgentResults()` returns read-only membership containing original typed single-agent result references, in depth-first order. Repeated node IDs select the latest value in their first insertion position at each nested boundary; failure status does not discard a selected typed result. Untyped custom/error nodes do not invent agent results.

`NodeResult.RenderPython()` and `MultiAgentResult.RenderPython()` expose the explicit Python presentation. A node delegates to its selected typed result, otherwise using its native error message or concatenated text. An aggregate presents its own pending interrupts first; otherwise it labels latest-per-node text, removes one trailing LF at each aggregate boundary and omits only empty rows. These methods preserve original result references, every stored visit, accumulated usage, constructors, ordinary JSON and `ToString()`. The supported presentation is a native convenience contract; complete arbitrary Python object/repr equivalence is not promised.

`GraphOptions.Python` projects dependency input from preserved typed `AgentResult` and nested `MultiAgentResult` values. It includes ordered nested leaves, including nonterminus leaves, and uses the existing Python leaf renderer and ordinary `Agent` label. A repeated node ID selects the latest value in its first insertion position within each nested boundary. Native `Results` still retains every visit and its usage; typed-input routing and custom function-node text fallback retain their existing behavior.

`SwarmOptions.ContextTextProfile` selects `SwarmContextTextProfile.NativeJson` (default) or `PythonRepr` for model-facing shared knowledge. `SwarmOptions.Python` selects `PythonRepr`, independently of routing. Unknown enum values are rejected. The default property is omitted from options JSON even when the serializer otherwise retains defaults; a nondefault selection is serialized. This shares the fixed CPython 3.10.2/Unicode 13 JSON-domain presentation with Python pending-interrupt reasons. Integer-versus-floating token form, binary64 rounding and Python-style float spelling, pinned printability, lone-surrogate escaping and duplicate-member last-value/first-position behavior apply only to this text projection. Stored JSON, constructors, result `ToString()`, default result JSON and ownership remain unchanged. Arbitrary Python object conversion, other Python/Unicode versions and universal presentation capacity are outside this explicit projection.

## Console conversations

`BidiConsole.Input()` supplies an exclusive interactive keyboard, with Enter, backspace and Ctrl+D/Ctrl+Z EOF. Redirected input uses `BidiConsole.Input(Func<CancellationToken, ValueTask<string?>>)`; returning null ends the input normally. The callback remains borrowed and must cooperate with cancellation. A pending read is always awaited.

```csharp
using Strands.Agents.Bidi;

await agent.RunAsync(
    BidiConsole.Input(),
    new[] { BidiConsole.Output() },
    cancellationToken: cancellationToken);
```

The `RunAsync` extension accepts any `IAsyncEnumerable<IBidiInput>` and shares the agent's existing run lifecycle. Normal EOF awaits iterator disposal before teardown. Existing list-based instance calls retain their bindings. `BidiConsole.Output` borrows a `TextWriter` and awaits writes and flushes for labelled text, transcripts, reasoning and tool calls. `BidiConsoleOutputOptions` selects those categories. Recent final-block duplicates are suppressed with bounded memory. Hosts coordinate any other readers of the same keyboard and any other writers using the same writer. These helpers do not provide microphone capture or speaker playback.

## REST-only remote agents

Card-selected `HTTP+JSON` endpoints use A2A 0.3 REST routes and protobuf JSON. Native calls still expose the SDK's canonical message/task JSON. The client supports send, stream, task get/cancel, GET resubscription, authenticated cards, and all four push-configuration operations. JSON-RPC remains supported; an explicit endpoint override retains the selected transport binding. Standard protobuf defaults and task/security/file/data conversions are covered by focused interoperability tests. JSON-RPC-only extension fields are not a promise of protobuf wire interchange.

The native server continues to host JSON-RPC/SSE. Client REST support does not add REST server routes or gRPC. Authentication and advertised endpoint trust remain application responsibilities.


## Authorized push notifications

Set `A2AServerOptions.PushNotifications` to opt in. `A2APushNotificationOptions` requires a borrowed `SendAsync` callback and an `AuthorizeConfiguration` callback. The latter receives the current HTTP request and detached configuration before a message or set operation can store it or construct an agent. Use it to authorize the destination and credentials. The embedding host still authenticates RPC requests and authorizes task access.

The sender receives a detached task snapshot and authorized configuration. It can use an existing webhook service or a caller-owned HTTP client. For an HTTP sender, send the task as JSON, pass the configured token in `X-A2A-Notification-Token`, and configure the HTTP pipeline's authentication, TLS and redirect policy explicitly. Keep borrowed dependencies alive until server disposal finishes.

Push configuration operations are `tasks/pushNotificationConfig/set`, `/get`, `/list` and `/delete`. Set accepts `{ taskId, pushNotificationConfig }`; the others accept the task `id`, and get/delete can select `pushNotificationConfigId` (required for delete). Get returns one `{ taskId, pushNotificationConfig }`, list returns an array of those records, and successful delete returns null. A missing configuration id in an incoming message defaults to its allocated task id.

The default `InMemoryA2APushNotificationConfigStore` bounds total configurations to 1000 and retained JSON to 1 MiB; both limits are configurable. Supply a borrowed `IA2APushNotificationConfigStore` for another backend. Delivery follows task publication and any configured durable task save. Nonpersistent task-cache eviction and failure before the first publication retire unused configurations before the next execution. Persistent, addressable tasks keep their configurations.

Delivery is awaited. `DeliveryFailed` observes sender/store failures without changing an already published task outcome; the default trace records the failure type without credentials. Shutdown cancels and joins admitted configuration operations and sender callbacks. Callbacks that ignore cancellation must finish before disposal returns. An unconfigured server continues to reject push requests and advertises no push capability.

## Nova Sonic bidirectional model

`BedrockNovaSonicModel(IAmazonBedrockRuntime, BedrockNovaSonicModelOptions)` supplies native bidirectional text, PCM16, transcripts, tool exchange, usage and barge-in events through a borrowed official AWS client on .NET 8 and later. `BedrockNovaSonicModel(AWSCredentials, RegionEndpoint, BedrockNovaSonicModelOptions)` borrows credentials/region, obtains fresh credentials for each connection and owns its created transport: the official AWS transport on modern .NET, or the static-CRT Windows x86/x64 native transport for the .NET Standard 2.0 asset consumed by Framework 4.8. The borrowed-client constructor still rejects duplex execution on that legacy target. Typed configuration updates apply to the next connection; active wire settings remain captured. The default reconnect interval is seven minutes. [Recorded results](VALIDATION_RESULTS.md#native-protocol-and-audio-evidence) distinguish the genuine Framework localhost TLS/model and package-ABI checks from the final full pipeline and live AWS qualification. See [Nova Sonic conversations](BEDROCK_NOVA_SONIC.md) for defaults, limits and retained-history semantics.

## Optional device audio

The separate `Strands.Agents.Bidi.Audio` package adds `BidiAudioIO(IAudioBidiModel, BidiAudioOptions?)` in the existing `Strands.Agents.Bidi` namespace. `Input` and `Output` implement the native run stream contracts. Construction leaves devices closed. A run owns capture/playback lifetime and borrows the model and optional console sink. Stop/disposal awaits physical device closure.

`BidiAudioOptions` selects device indices, callback frames, bounded capture/playback and optional `BidiAudioProcessingOptions`. The latter independently selects managed echo cancellation, noise suppression and gain control, with a delay hint. Supported device formats are mono little-endian PCM16. Different capture/playback rates are supported. Playback underflow emits silence, barge-in clears pending speech/reference, and overflow rejects atomically without silently discarding queued speech. Capture overflow retains recent input. See [device audio](../src/Strands.Agents.Bidi.Audio/README.md) for defaults, native prerequisites and build instructions.
