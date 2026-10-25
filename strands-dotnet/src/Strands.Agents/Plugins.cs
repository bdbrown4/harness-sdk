namespace Strands.Agents;

/// <summary>Extends one agent through initialization, lifecycle hooks, and tools.</summary>
public interface IPlugin
{
    /// <summary>A stable unique identifier. Strands-supplied plugins use the strands: prefix.</summary>
    string Name { get; }
    /// <summary>Initializes the plugin after session restoration and before the first invocation.</summary>
    ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default);
    /// <summary>Returns tools after initialization; the agent validates and registers them atomically.</summary>
    IReadOnlyList<ITool> GetTools();
}

/// <summary>Runs once after session restoration and all plugins have initialized.</summary>
public sealed record InitializedEvent(Agent Agent) : HookEvent(Agent);

/// <summary>Runs before executing one complete batch of model-requested tools.</summary>
public sealed record BeforeToolsEvent(Agent Agent, IReadOnlyList<ToolUseBlock> Tools, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent)
{
    /// <summary>Denies every requested call in the batch without executing any body. Each call receives a correlated error result.</summary>
    public string? CancelReason { get; set; }
    private readonly string? _interruptBatchId = Agent.ActiveInterruptBatchId;
    /// <summary>Returns a supplied human response, or pauses the complete tool batch before executing its incomplete calls.</summary>
    public System.Text.Json.JsonElement Interrupt(string name, System.Text.Json.JsonElement? reason = null, System.Text.Json.JsonElement? response = null) => Agent.ResolveBatchInterrupt(name, reason, response, _interruptBatchId);
}

/// <summary>Runs after complete ordered results have been collected for a tool batch.</summary>
public sealed record AfterToolsEvent(Agent Agent, IReadOnlyList<ToolResultBlock> Results, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent)
{
    /// <summary>Ends the current cycle with this detached assistant content after committing the batch's tool results. Null continues the model loop.</summary>
    public IReadOnlyList<ContentBlock>? EndTurn { get; set; }
}
