namespace Strands.Agents.MultiAgent;

/// <summary>How incoming edges make a node ready.</summary>
public enum DependencyPolicy
{
    /// <summary>Every source must be completed and every incoming condition true, matching TypeScript.</summary>
    AllSatisfied,
    /// <summary>Any satisfied edge from the just-completed node or batch is sufficient, matching Python.</summary>
    AnySatisfied
}
/// <summary>When downstream work becomes eligible.</summary>
public enum GraphScheduling
{
    /// <summary>Schedule individually as predecessors finish, matching TypeScript.</summary>
    ReadyNodes,
    /// <summary>Wait for the current batch before scheduling successors, matching Python.</summary>
    CompletedBatches
}
/// <summary>How failures affect independent branches.</summary>
public enum NodeFailurePolicy
{
    /// <summary>Record failure and continue independent branches, matching TypeScript.</summary>
    ContinueIndependent,
    /// <summary>Cancel and drain siblings, then rethrow the original failure, matching Python.</summary>
    FailFast
}
/// <summary>How configured execution limits are reported.</summary>
public enum LimitOutcome
{
    /// <summary>Throw a graph-limit exception, matching TypeScript.</summary>
    Throw,
    /// <summary>Return a failed aggregate result, matching Python.</summary>
    FailedResult
}
/// <summary>How dependency outputs are added to original input.</summary>
public enum GraphInputFormat
{
    /// <summary>Original typed content followed by [node: id] and typed predecessor content, matching TypeScript.</summary>
    Typed,
    /// <summary>Python's Original Task/Inputs from previous nodes/From labels and textual predecessor projection.</summary>
    Python
}
/// <summary>When an orchestrator persists its latest session state. Both upstream implementations default to Node.</summary>
public enum MultiAgentSaveLatestStrategy
{
    /// <summary>After each completed node and after invocation cleanup.</summary>
    Node,
    /// <summary>Only after invocation cleanup, reducing storage writes.</summary>
    Invocation
}
/// <summary>Which typed task is used when a stored non-interrupt execution still has unfinished nodes.</summary>
public enum GraphRestoredInputPolicy
{
    /// <summary>Use newly supplied content, matching TypeScript; explicit response-only resume still uses the stored task.</summary>
    Incoming,
    /// <summary>Keep the stored task, matching Python.</summary>
    Stored
}
/// <summary>An optional asynchronous routing condition with access to both graph and caller state.</summary>
public delegate ValueTask<bool> EdgeCondition(MultiAgentState state, IReadOnlyDictionary<string, object?> invocationState, CancellationToken cancellationToken);
/// <summary>A directed connection between named nodes.</summary>
/// <param name="Source">The source node identifier.</param>
/// <param name="Target">The target node identifier.</param>
/// <param name="Condition">An optional condition, evaluated during scheduling.</param>
public sealed record Edge(string Source, string Target, EdgeCondition? Condition = null);
/// <summary>Immutable graph topology and explicit upstream compatibility policies. Null budgets are unbounded.</summary>
public sealed record GraphOptions
{
    /// <summary>Creates Python routing, scheduling, failure, limit, input and retained-agent-state policies. Topology and budgets remain explicit.</summary>
    public static GraphOptions Python => new()
    {
        Dependencies = DependencyPolicy.AnySatisfied,
        Scheduling = GraphScheduling.CompletedBatches,
        NodeFailure = NodeFailurePolicy.FailFast,
        LimitOutcome = LimitOutcome.FailedResult,
        InputFormat = GraphInputFormat.Python,
        AgentContext = AgentContextPolicy.Retain,
        RestoredInput = GraphRestoredInputPolicy.Stored,
        Telemetry = new() { Profile = MultiAgentTelemetryProfile.Python }
    };
    /// <summary>The default agent state policy; a per-node override takes precedence. Defaults to TypeScript snapshot/restore.</summary>
    public AgentContextPolicy AgentContext { get; init; }
    /// <summary>The callback registry. Its registrations remain caller-owned.</summary>
    public MultiAgentHookRegistry Hooks { get; init; } = new();
    /// <summary>Ordered borrowed plugin declarations. Membership is copied; initialization and session ordering follow SessionRestoreProfile.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<MultiAgentPlugin> Plugins { get; init; } = [];
    /// <summary>Automatic orchestration activity settings. Uses the TypeScript policy by default.</summary>
    public MultiAgentTelemetryOptions Telemetry { get; init; } = new();
    /// <summary>The graph identifier, defaulting to graph.</summary>
    public string Id { get; init; } = "graph";
    /// <summary>An optional opaque durable session identifier; requires SessionStore.</summary>
    public string? SessionId { get; init; }
    /// <summary>A borrowed native orchestration repository; requires an explicit SessionId.</summary>
    public IMultiAgentSessionStore? SessionStore { get; init; }
    /// <summary>Session restore ordering. Native restores before user plugins; explicit source profiles select their callback phase.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public MultiAgentSessionRestoreProfile SessionRestoreProfile { get; init; }
    /// <summary>Session persistence frequency, defaulting to Node. Resources remain borrowed.</summary>
    public MultiAgentSaveLatestStrategy SaveLatestOn { get; init; }
    /// <summary>The restored unfinished-task policy, defaulting to TypeScript's incoming content.</summary>
    public GraphRestoredInputPolicy RestoredInput { get; init; }
    /// <summary>The ordered node declarations; all dependencies remain borrowed.</summary>
    public IReadOnlyList<Node> Nodes { get; init; } = [];
    /// <summary>The directed edges in deterministic declaration order.</summary>
    public IReadOnlyList<Edge> Edges { get; init; } = [];
    /// <summary>Explicit source identifiers, or null to infer nodes without incoming edges.</summary>
    public IReadOnlyList<string>? Sources { get; init; }
    /// <summary>Maximum simultaneous node visits, defaulting to no topology-independent cap.</summary>
    public int MaxConcurrency { get; init; } = int.MaxValue;
    /// <summary>Maximum total node visits, or null for no step budget.</summary>
    public long? MaxSteps { get; init; }
    /// <summary>An optional cooperative invocation deadline, propagated into nested orchestrators.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>An optional cooperative node deadline. Agent-node overrides take precedence.</summary>
    public TimeSpan? NodeTimeout { get; init; }
    /// <summary>TypeScript's AND dependency policy by default.</summary>
    public DependencyPolicy Dependencies { get; init; }
    /// <summary>TypeScript's individual readiness scheduling by default.</summary>
    public GraphScheduling Scheduling { get; init; }
    /// <summary>TypeScript's independent-branch failure policy by default.</summary>
    public NodeFailurePolicy NodeFailure { get; init; }
    /// <summary>TypeScript's throwing step/deadline outcome by default.</summary>
    public LimitOutcome LimitOutcome { get; init; }
    /// <summary>TypeScript's typed predecessor projection by default.</summary>
    public GraphInputFormat InputFormat { get; init; }
}
