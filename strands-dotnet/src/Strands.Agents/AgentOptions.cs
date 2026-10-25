using System.Text.Json;

namespace Strands.Agents;

/// <summary>The boundary of an agent's invocation span and metrics history entry.</summary>
public enum InvocationTelemetryScope
{
    /// <summary>One boundary across all hook continuations, matching Python's public-invocation scope.</summary>
    PublicInvocation,
    /// <summary>A fresh boundary for each accepted agent pass, matching TypeScript.</summary>
    AgentPass
}

/// <summary>Whether an explicit cancellation requested while idle is retained for the next invocation.</summary>
public enum IdleCancellationBehavior
{
    /// <summary>Ignore idle requests, matching TypeScript and the native default.</summary>
    Ignore,
    /// <summary>Retain an immediate request and its last nonnull message for the next admitted invocation, matching Python's idle retention.</summary>
    CancelNextInvocation
}

/// <summary>Placement of cooperative explicit-request checkpoints. The original caller token remains a hard cancellation in every mode.</summary>
public enum ExplicitCancellationTiming
{
    /// <summary>Preserve the native early input, hook and result checkpoints.</summary>
    NativeEarly,
    /// <summary>Check before input/model attempts, then at model or tool boundaries, following TypeScript's checkpoint pattern.</summary>
    BeforeInputAndModelOrTool,
    /// <summary>Allow input/pre-model preparation before checking at model or tool boundaries, following Python's checkpoint pattern.</summary>
    ModelOrTool
}

/// <summary>How tools requested by a single model turn are scheduled.</summary>
public enum ToolExecutionStrategy
{
    /// <summary>Run independent calls concurrently, bounded by MaxParallelTools.</summary>
    Concurrent,
    /// <summary>Run calls one at a time in model-issued order.</summary>
    Sequential
}

/// <summary>Construction settings for a native Strands agent.</summary>
public sealed record AgentOptions
{
    /// <summary>Optional cycle pauses. Disabled preserves ordinary execution; enabled modes select the source language's after-model workflow.</summary>
    public CheckpointBehavior CheckpointBehavior { get; init; }
    /// <summary>The explicitly configured model used by the main agent loop.</summary>
    public required IModel Model { get; init; }
    /// <summary>Whether a model attempt observes replacement before model hooks (TypeScript/default) or after them (Python).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ModelSelectionTiming ModelSelectionTiming { get; init; }
    /// <summary>Construction-only history behavior, independent of model-selection timing. Native is the default; source profiles retain their constructor-selected collaborators and do not migrate native input receipts or indexed session modes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ModelHistoryBehavior ModelHistoryBehavior { get; init; }
    /// <summary>Idle explicit-cancellation retention. Ignore is the default. CancelNextInvocation retains immediate requests; deferred-only requests are cleared on entry. This does not change native input/model safe points.</summary>
    public IdleCancellationBehavior IdleCancellationBehavior { get; init; }
    /// <summary>Where explicit requests are observed. Defaults to NativeEarly; does not defer the original invocation cancellation token or change idle retention.</summary>
    public ExplicitCancellationTiming ExplicitCancellationTiming { get; init; }
    /// <summary>Whether disposing the agent also disposes its main model. Supplied models remain caller-owned by default.</summary>
    public bool OwnsModel { get; init; }
    /// <summary>When provider input redaction changes history: accepted model turn (TypeScript, default) or immediate provider event (Python).</summary>
    public InputRedactionTiming InputRedactionTiming { get; init; }
    /// <summary>Resources transferred to the agent on successful construction. Entries must be nonnull and unique by reference, and cannot repeat an owned main model. The list is copied; disposal runs in reverse order before the owned model. Default children borrow dependencies and inherit no owned resources.</summary>
    public IReadOnlyList<IAsyncDisposable> OwnedResources { get; init; } = [];
    /// <summary>An auxiliary model exposed to integrations; null resolves to the main model.</summary>
    public IModel? AuxModel { get; init; }
    /// <summary>A stable agent identity, persisted with sessions and regenerated for default children.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    /// <summary>Optional shared byte storage for SDK subsystems. The caller retains backend ownership.</summary>
    public Storage.IStorage? Storage { get; init; }
    /// <summary>Optional selected filesystem for SDK components. The caller retains backend ownership.</summary>
    public Files.IReadOnlyFileSystem? FileSystem { get; init; }
    /// <summary>The explicitly selected borrowed execution environment; null selects a per-agent lazy local default.</summary>
    public Sandbox.Sandbox? Sandbox { get; init; }
    /// <summary>First-class context reduction and overflow recovery, taking precedence over ConversationManager. An instance binds to one agent.</summary>
    public Context.ContextManager? ContextManager { get; init; }
    /// <summary>First-class cross-session memory. Each manager instance binds to one agent.</summary>
    public Memory.MemoryManager? MemoryManager { get; init; }
    /// <summary>Opt-in background tool execution. Null disables it; an empty configuration enables model selection with four physical execution slots.</summary>
    public Tasks.BackgroundTasksConfig? BackgroundTasks { get; init; }
    internal Func<Agent, IReadOnlyList<ITool>>? InitializationTools { get; init; }
    internal Func<IReadOnlyList<ITool>, Tasks.BackgroundTasksConfig, Func<ITool, ToolSpec>, Tasks.BackgroundTasksConfig>? BackgroundTasksPolicyNormalizer { get; init; }
    /// <summary>The agent's display name and default name when exposed as a tool.</summary>
    public string Name { get; init; } = "agent";
    /// <summary>An optional description used when exposing the agent as a tool.</summary>
    public string? Description { get; init; }
    /// <summary>Custom activity attributes, copied together with array values when constructing the agent and inherited by default children.</summary>
    public IReadOnlyDictionary<string, object?>? TraceAttributes { get; init; }
    /// <summary>Whether an error-status tool result also marks its span as an error without inventing an exception. False follows TypeScript; true selects Python's result-status policy.</summary>
    public bool UsePythonToolErrorStatus { get; init; }
    /// <summary>The span and metrics-entry boundary. Defaults to one public invocation across hook continuations; this does not change turn/token limits or select other language policies.</summary>
    public InvocationTelemetryScope InvocationTelemetryScope { get; init; }
    /// <summary>Retained invocation histories; fifty follows TypeScript, while null selects Python's unbounded retention. Lifetime totals always remain cumulative.</summary>
    public int? MaximumInvocationHistory { get; init; } = 50;
    /// <summary>How metrics select context-size estimates. Defaults to the most recently reported usage, matching TypeScript.</summary>
    public Telemetry.ContextSizePolicy MetricsContextSizePolicy { get; init; }
    /// <summary>Optional instructions sent separately from conversation messages.</summary>
    public string? SystemPrompt { get; init; }
    private IReadOnlyList<ContentBlock>? _systemPromptContent;
    /// <summary>Optional ordered text, cache-point and guard instructions, copied on assignment. Configure either this property or SystemPrompt, including empty values.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContentBlock>? SystemPromptContent { get => _systemPromptContent; init => _systemPromptContent = SessionSnapshot.CopySystemPromptContent(value, allowGuardContent: true); }
    /// <summary>The default schema/JSON conversion settings for ordinary invocation and streaming; null disables the default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StructuredOutputOptions<JsonElement>? StructuredOutput { get; init; }
    /// <summary>The tools registered for model-directed execution. Names must be unique.</summary>
    public IReadOnlyList<ITool> Tools { get; init; } = [];
    /// <summary>Borrowed providers loaded in declaration order during initialization, after application tools and before plugins. The list is copied; children reload with their own consumer context.</summary>
    public IReadOnlyList<IToolProvider> ToolProviders { get; init; } = [];
    /// <summary>Use optional IToolProviderUpdates subscriptions for initial loading and inactive-agent publication. False preserves one-time provider loading.</summary>
    public bool EnableToolProviderUpdates { get; init; }
    // Internal delegated scope checks see the same detached specification that will be published.
    internal Func<ITool, ToolSpec, bool>? ProviderSpecFilter { get; init; }
    /// <summary>Persistent and invocation-local name collision policy. Defaults to native exact-name compatibility.</summary>
    public ToolNameConflictPolicy ToolNameConflictPolicy { get; init; }
    /// <summary>Public direct-call lookup policy. Model-issued and Bidi tool names always remain exact.</summary>
    public ToolNameResolution DirectToolNameResolution { get; init; }
    /// <summary>An optional registration predicate applied to construction, plugin, and added tool batches. False omits the tool; exceptions leave that batch unregistered. Default children inherit the predicate. This composes registration and is not an isolation boundary.</summary>
    public Func<ITool, bool>? ToolFilter { get; init; }
    /// <summary>The initial conversation, copied at construction and replaced when an existing session is restored.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];
    /// <summary>The registry used for this agent's awaited lifecycle callbacks.</summary>
    public HookRegistry Hooks { get; init; } = new();
    /// <summary>Application middleware inherited by default children. Each agent adds an independent layer for registrations made by its plugins.</summary>
    public Middleware.MiddlewareRegistry Middleware { get; init; } = new();
    /// <summary>Ordered policy handlers for lifecycle events. Each agent creates an independent bridge; default children inherit the caller-owned handler instances.</summary>
    public IReadOnlyList<Interventions.InterventionHandler> Interventions { get; init; } = [];
    /// <summary>How model-boundary intervention feedback enters history. Native preserves ordinary message callbacks and persistence; DirectAppend selects source direct delivery.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public GuideFeedbackDelivery GuideFeedbackDelivery { get; init; }
    /// <summary>Plugins initialized once after restoring application state, before the first invocation.</summary>
    public IReadOnlyList<IPlugin> Plugins { get; init; } = [];
    /// <summary>Initial JSON application state, replaced by stored state when a snapshot supplies it.</summary>
    public IReadOnlyDictionary<string, JsonElement> State { get; init; } = new Dictionary<string, JsonElement>();
    /// <summary>Initial provider conversation state, copied independently from application state and replaced when a stored snapshot supplies it. Default children start empty.</summary>
    public IReadOnlyDictionary<string, JsonElement> ModelState { get; init; } = new Dictionary<string, JsonElement>();
    /// <summary>The default per-invocation model-turn limit. Must be positive; defaults to 20.</summary>
    public int MaxTurns { get; init; } = 20;
    /// <summary>The maximum concurrent tool executions in one batch. Must be positive; defaults to eight.</summary>
    public int MaxParallelTools { get; init; } = 8;
    /// <summary>The tool scheduling strategy, defaulting to concurrent execution.</summary>
    public ToolExecutionStrategy ToolExecution { get; init; } = ToolExecutionStrategy.Concurrent;
    /// <summary>An optional borrowed model-batch scheduler. Null preserves ToolExecution; custom execution uses a linked batch token.</summary>
    public IToolBatchExecutor? ToolBatchExecutor { get; init; }
    /// <summary>An optional conversation store. A nonempty SessionId is required when supplied.</summary>
    public ISessionStore? SessionStore { get; init; }
    /// <summary>The opaque key used by the session store.</summary>
    public string? SessionId { get; init; }
    /// <summary>An optional strategy that reduces model-visible history without discarding durable messages.</summary>
    public IConversationManager? ConversationManager { get; init; }
}

/// <summary>Per-invocation limits and caller-owned context.</summary>
public sealed record InvocationOptions
{
    /// <summary>Resumes an existing conversation at a model/tool cycle boundary without appending user input. The marker does not contain conversation or provider state.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AgentCheckpoint? CheckpointResume { get; init; }
    /// <summary>Schema/JSON conversion settings for this invocation; null inherits the Agent default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StructuredOutputOptions<JsonElement>? StructuredOutput { get; init; }
    /// <summary>An optional exact ordinal token that joins only an in-flight invocation on this agent. Null disables joining; empty is valid.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? IdempotencyToken { get; init; }
    /// <summary>Responses to pending human input requests. Resuming a paused batch cannot also add a user prompt.</summary>
    public IReadOnlyList<InterruptResponse> InterruptResponses { get; init; } = [];
    internal Message? InputMessage { get; init; }
    internal IReadOnlyList<Message> InputMessages { get; init; } = [];
    internal IReadOnlyList<ITool> EphemeralTools { get; init; } = [];
    internal IStructuredOutputTool? StructuredOutputTool { get; init; }
    internal StructuredOutputBehavior StructuredOutputBehavior { get; init; }
    internal bool ExposeStructuredOutput { get; init; }
    internal string? StructuredOutputForcePrompt { get; init; }
    internal int StructuredOutputValidationLimit { get; init; } = int.MaxValue;
    /// <summary>Caller-owned context passed to tools and hooks and returned on the result. The SDK adds no entries.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; init; } = new Dictionary<string, object?>();
    /// <summary>A positive turn limit overriding AgentOptions.MaxTurns for this invocation.</summary>
    public int? MaxTurns { get; init; }
    /// <summary>A positive cumulative token cap, checked after a complete model/tool turn. One response may exceed it.</summary>
    public long? MaxTotalTokens { get; init; }
    /// <summary>A positive cumulative output-token cap, checked after a complete model/tool turn. One response may exceed it.</summary>
    public long? MaxOutputTokens { get; init; }
    /// <summary>Starts new limits and result usage/turn counters for each explicit hook-resumed cycle, matching the TypeScript run boundary. False keeps cumulative limits and counters across the outer invocation. Automatic background continuation never resets limits.</summary>
    public bool ResetLimitsOnHookResume { get; init; }
}

/// <summary>The terminal result of an invocation, including usage across all model turns.</summary>
/// <param name="StopReason">The model or framework reason for stopping.</param>
/// <param name="Message">The latest completed model response, or null if none completed.</param>
/// <param name="Usage">The accumulated usage from completed model turns.</param>
/// <param name="Turns">The number of completed model turns during this invocation.</param>
/// <param name="InvocationState">The caller-owned context supplied for this invocation.</param>
public sealed record AgentResult(string StopReason, Message? Message, Usage Usage, int Turns, IReadOnlyDictionary<string, object?> InvocationState)
{
    /// <summary>The optional experimental cycle marker; it contains no resume authority or conversation state.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AgentCheckpoint? Checkpoint { get; init; }
    /// <summary>The accepted schema-validated JSON result of an ordinary structured invocation, or null when absent.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StructuredOutput { get => _structuredOutput; init => _structuredOutput = value?.Clone(); }
    private readonly JsonElement? _structuredOutput;
    [System.Text.Json.Serialization.JsonIgnore]
    internal (string ToolUseId, object? Value)? AcceptedIdempotentStructuredValue { get; init; }
    /// <summary>A detached execution-statistics snapshot for an agent-produced result, or null for manually constructed results.</summary>
    public Telemetry.AgentMetrics? Metrics { get; init; }
    /// <summary>The latest prompt context size from the collected metrics, or null when unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? ContextSize => Metrics?.LatestContextSize;
    /// <summary>The latest prompt plus output context size from the collected metrics, or null when unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? ProjectedContextSize => Metrics?.ProjectedContextSize;
    /// <summary>The collected local cycle traces associated with this result.</summary>
    public IReadOnlyList<Telemetry.AgentTrace> Traces { get; init; } = [];
    /// <summary>Unanswered human input requests when execution pauses.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; init; } = [];
    internal string? StructuredOutputToolUseId { get; init; }
    /// <summary>Returns accepted structured JSON, otherwise the final message's plain text or an empty string.</summary>
    /// <returns>The plain text representation of the result.</returns>
    public override string ToString() => StructuredOutput?.GetRawText() ?? Message?.Text ?? string.Empty;
}

/// <summary>An agent instance already has an active invocation.</summary>
public sealed class ConcurrentInvocationException() : InvalidOperationException("This agent already has an active invocation. Use a separate agent for concurrent conversations.");
