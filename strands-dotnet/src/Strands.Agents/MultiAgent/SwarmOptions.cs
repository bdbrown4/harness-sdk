namespace Strands.Agents.MultiAgent;

/// <summary>The model-facing protocol used to request a swarm handoff.</summary>
public enum SwarmRoutingMode
{
    /// <summary>A required structured response containing agentId, message and optional context, matching TypeScript.</summary>
    Structured,
    /// <summary>An invocation-local handoff_to_agent tool and an ordinary final model response, matching Python.</summary>
    Tool
}

/// <summary>How an ordinary node or invocation failure is exposed to the caller.</summary>
public enum SwarmFailurePolicy
{
    /// <summary>Rethrow the original failure after cleanup, matching TypeScript.</summary>
    Throw,
    /// <summary>Return a failed aggregate result, matching Python. Caller cancellation still propagates.</summary>
    FailedResult
}

/// <summary>Which completed visits participate in repetitive-handoff detection after a human pause.</summary>
public enum SwarmRepetitionScope
{
    /// <summary>Inspect only this invocation, matching TypeScript. Resuming begins a new detection window.</summary>
    Invocation,
    /// <summary>Inspect the retained execution history, matching Python.</summary>
    Execution
}

/// <summary>Whether human resumes restart or continue the execution-time budget.</summary>
public enum SwarmTimeoutScope
{
    /// <summary>Restart the budget for each invocation, matching TypeScript.</summary>
    Invocation,
    /// <summary>Accumulate active execution time across resumes, excluding time awaiting human input, matching Python.</summary>
    Execution
}

/// <summary>When successful tool-mode handoff contributions become visible in shared knowledge.</summary>
public enum SwarmContextCommit
{
    /// <summary>Commit when the agent finishes its visit. Contributions remain private during a human pause.</summary>
    CompletedVisit,
    /// <summary>Also commit accepted successful contributions when a visit pauses, matching Python's committed interrupted turn.</summary>
    AcceptedResults
}

/// <summary>Text presentation of JSON shared knowledge in tool-routing prompts.</summary>
public enum SwarmContextTextProfile
{
    /// <summary>Preserve the existing compact JSON representation.</summary>
    NativeJson,
    /// <summary>Use fixed CPython 3.10.2/Unicode 13 JSON value presentation, including numeric scalars. NativeJson and stored JSON retain their original representation.</summary>
    PythonRepr
}

/// <summary>Sequential agent handoff orchestration. Structured routing and borrowed-agent restoration match TypeScript by default.</summary>
public sealed record SwarmOptions
{
    /// <summary>Python's tool routing, construction baseline, 20-visit budgets, 900/300-second deadlines and failed-result policies.</summary>
    public static SwarmOptions Python => new()
    {
        Id = "default_swarm",
        Routing = SwarmRoutingMode.Tool,
        AgentContext = AgentContextPolicy.ResetOnRevisit,
        MaxSteps = 20,
        MaxHandoffs = 20,
        Timeout = TimeSpan.FromSeconds(900),
        NodeTimeout = TimeSpan.FromSeconds(300),
        LimitOutcome = LimitOutcome.FailedResult,
        FailurePolicy = SwarmFailurePolicy.FailedResult,
        RestoredInput = GraphRestoredInputPolicy.Stored,
        RepetitionScope = SwarmRepetitionScope.Execution,
        TimeoutScope = SwarmTimeoutScope.Execution,
        ContextCommit = SwarmContextCommit.AcceptedResults,
        ContextTextProfile = SwarmContextTextProfile.PythonRepr,
        Telemetry = new() { Profile = MultiAgentTelemetryProfile.Python }
    };
    /// <summary>The stable orchestrator identity, defaulting to swarm.</summary>
    public string Id { get; init; } = "swarm";
    /// <summary>The borrowed agents in declaration order, with explicit identifiers and per-agent options.</summary>
    public IReadOnlyList<AgentNode> Nodes { get; init; } = [];
    /// <summary>The initial node identifier, or null to use the first declared node.</summary>
    public string? Start { get; init; }
    /// <summary>The awaited lifecycle and progress callbacks for this orchestrator.</summary>
    public MultiAgentHookRegistry Hooks { get; init; } = new();
    /// <summary>Ordered borrowed plugin declarations. Membership is copied; initialization and session ordering follow SessionRestoreProfile.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<MultiAgentPlugin> Plugins { get; init; } = [];
    /// <summary>Automatic orchestration activity settings. Uses the TypeScript policy by default.</summary>
    public MultiAgentTelemetryOptions Telemetry { get; init; } = new();
    /// <summary>An opaque durable session identifier paired with SessionStore.</summary>
    public string? SessionId { get; init; }
    /// <summary>A borrowed orchestration repository paired with SessionId.</summary>
    public IMultiAgentSessionStore? SessionStore { get; init; }
    /// <summary>Session restore ordering. Native restores before user plugins; explicit source profiles select their callback phase.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public MultiAgentSessionRestoreProfile SessionRestoreProfile { get; init; }
    /// <summary>Latest-checkpoint write frequency, defaulting to Node.</summary>
    public MultiAgentSaveLatestStrategy SaveLatestOn { get; init; }
    /// <summary>Whether restored unfinished work uses incoming or stored task content. Explicit response-only resumes always use stored content.</summary>
    public GraphRestoredInputPolicy RestoredInput { get; init; }
    /// <summary>The default borrowed-agent state policy. Per-node overrides take precedence.</summary>
    public AgentContextPolicy AgentContext { get; init; } = AgentContextPolicy.RestoreAfterExecution;
    /// <summary>When missing reset baselines are captured relative to user plugins. BeforePlugins preserves the native default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public SwarmBaselineCapturePhase BaselineCapturePhase { get; init; }
    /// <summary>A positive logical-visit budget, or null for TypeScript's unbounded default. Continuing a paused visit consumes no new step; a terminal answer on the final allowed visit succeeds.</summary>
    public long? MaxSteps { get; init; }
    /// <summary>An optional second completed-visit budget corresponding to Python's max_handoffs counter; null is unbounded.</summary>
    public long? MaxHandoffs { get; init; }
    /// <summary>How execution budgets are reported; TypeScript throws by default.</summary>
    public LimitOutcome LimitOutcome { get; init; }
    /// <summary>How ordinary execution failures are reported; TypeScript rethrows by default.</summary>
    public SwarmFailurePolicy FailurePolicy { get; init; }
    /// <summary>The number of recent completed visits inspected for routing loops; zero disables detection.</summary>
    public int RepetitiveHandoffDetectionWindow { get; init; }
    /// <summary>The required distinct agents in that window; pair with a positive detection window, or set both to zero.</summary>
    public int RepetitiveHandoffMinUniqueAgents { get; init; }
    /// <summary>The repetition-history boundary, defaulting to TypeScript's current invocation.</summary>
    public SwarmRepetitionScope RepetitionScope { get; init; }
    /// <summary>An optional cooperative execution budget, accumulated according to TimeoutScope; null is unbounded.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>The deadline boundary after a pause, defaulting to TypeScript's current invocation.</summary>
    public SwarmTimeoutScope TimeoutScope { get; init; }
    /// <summary>An optional cooperative visit deadline. An AgentNode timeout overrides it.</summary>
    public TimeSpan? NodeTimeout { get; init; }
    /// <summary>The model-facing handoff protocol. Structured routing is the default.</summary>
    public SwarmRoutingMode Routing { get; init; }
    /// <summary>When accepted tool-mode contributions become public, defaulting to completion of the visit.</summary>
    public SwarmContextCommit ContextCommit { get; init; }
    /// <summary>How tool-routing shared knowledge is rendered, defaulting to the existing compact JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public SwarmContextTextProfile ContextTextProfile { get; init; }
}
