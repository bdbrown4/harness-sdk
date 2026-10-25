using System.Diagnostics;
using Strands.Agents.Memory;

namespace Strands.Agents.Telemetry;

/// <summary>Collects local agent traces and emits native .NET activities to application-configured listeners.</summary>
/// <remarks>A tracer belongs to one agent. Explicit span handles keep concurrent tool completions correlated.</remarks>
public sealed partial class Tracer
{
    private static readonly ActivitySource Source = new(ActivitySourceName);
    private readonly object _gate = new();
    private readonly List<AgentTrace> _traces = [];
    private readonly Dictionary<string, object?> _attributes;
    private readonly bool _latestConventions;
    private readonly TraceConventions _conventions;
    private readonly string _serviceName;
    private TraceSpan? _agent;
    private TraceSpan? _cycle;

    /// <summary>The activity source applications register with their tracing provider.</summary>
    public const string ActivitySourceName = "Strands.Agents";
    internal bool UseLatestInvocationTokens => _conventions.UseLatestInvocationTokens;
    /// <summary>Creates a tracer with optional custom attributes copied at construction.</summary>
    /// <param name="traceAttributes">Attributes applied to this tracer's operations.</param>
    public Tracer(IReadOnlyDictionary<string, object?>? traceAttributes = null)
        : this(false, traceAttributes) { }

    /// <summary>Creates a tracer with an explicit Python-compatible error-status policy for tool results.</summary>
    /// <param name="usePythonToolErrorStatus">Whether an error-status result sets span status from its first text part without inventing an exception.</param>
    /// <param name="traceAttributes">Attributes applied to this tracer's operations.</param>
    public Tracer(bool usePythonToolErrorStatus, IReadOnlyDictionary<string, object?>? traceAttributes = null)
    {
        _attributes = traceAttributes?.ToDictionary(pair => pair.Key, pair => SnapshotAttribute(pair.Value), StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        _conventions = new TraceConventions(usePythonToolErrorStatus);
        _latestConventions = _conventions.Latest;
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        _serviceName = string.IsNullOrEmpty(serviceName) ? "strands-agents" : serviceName!;
    }
    /// <summary>A detached list of this invocation's local cycle traces.</summary>
    public IReadOnlyList<AgentTrace> Traces { get { lock (_gate) return Array.AsReadOnly(_traces.ToArray()); } }
    /// <summary>Starts an agent invocation and resets its local cycle collection.</summary>
    /// <param name="agentName">The agent display name.</param>
    /// <param name="agentId">The stable agent identifier.</param>
    /// <param name="modelId">The provider model identifier.</param>
    /// <returns>The invocation span handle.</returns>
    public TraceSpan StartAgentSpan(string agentName, string? agentId = null, string? modelId = null)
    {
        Guard.NotNullOrWhiteSpace(agentName, nameof(agentName));
        lock (_gate) { _traces.Clear(); _cycle = null; _agent = null; }
        var attributes = CommonAttributes("invoke_agent");
        attributes["gen_ai.agent.name"] = agentName;
        if (agentId is not null) attributes["gen_ai.agent.id"] = agentId;
        if (modelId is not null) attributes["gen_ai.request.model"] = modelId;
        var span = Create("invoke_agent " + agentName, null, null, attributes, onEnd: ended =>
        {
            lock (_gate) { if (ReferenceEquals(_agent, ended)) { _agent = null; _cycle = null; } }
        });
        lock (_gate) _agent = span;
        return span;
    }
    /// <summary>Starts a numbered local cycle and an activity parented to the agent invocation.</summary>
    /// <param name="cycleId">The cycle correlation identifier.</param>
    /// <returns>The cycle span handle.</returns>
    public TraceSpan StartAgentLoopSpan(string cycleId)
    {
        Guard.NotNullOrWhiteSpace(cycleId, nameof(cycleId));
        AgentTrace trace;
        TraceSpan? parent;
        lock (_gate)
        {
            trace = new AgentTrace("Cycle " + (_traces.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            trace.SetMetadata("cycleId", cycleId);
            _traces.Add(trace);
            parent = _agent;
        }
        var attributes = new Dictionary<string, object?> { ["agent_loop.cycle_id"] = cycleId };
        var span = Create("execute_agent_loop_cycle", parent, trace, attributes, onEnd: ended =>
        {
            lock (_gate) { if (ReferenceEquals(_cycle, ended)) _cycle = null; }
        });
        lock (_gate) _cycle = span;
        return span;
    }
    /// <summary>Starts a model operation within the current cycle.</summary>
    /// <param name="modelId">The provider model identifier.</param>
    /// <returns>The model span handle.</returns>
    public TraceSpan StartModelInvokeSpan(string? modelId = null)
    {
        TraceSpan? parent;
        lock (_gate) parent = _cycle;
        var trace = new AgentTrace("stream_messages", parent?.Trace);
        var attributes = CommonAttributes("chat");
        if (modelId is not null) attributes["gen_ai.request.model"] = modelId;
        return Create("chat", parent, trace, attributes);
    }
    /// <summary>Starts an individually correlated tool operation within the current cycle.</summary>
    /// <param name="tool">The model-issued tool request.</param>
    /// <returns>The tool span handle.</returns>
    public TraceSpan StartToolCallSpan(ToolUseBlock tool)
    {
        Guard.NotNull(tool, nameof(tool));
        TraceSpan? parent;
        lock (_gate) parent = _cycle;
        var trace = new AgentTrace("Tool: " + tool.Name, parent?.Trace);
        trace.SetMetadata("toolUseId", tool.ToolUseId);
        trace.SetMetadata("toolName", tool.Name);
        var attributes = CommonAttributes("execute_tool");
        attributes["gen_ai.tool.name"] = tool.Name;
        attributes["gen_ai.tool.call.id"] = tool.ToolUseId;
        var span = Create("execute_tool " + tool.Name, parent, trace, attributes);
        span.RecordToolInput(tool);
        return span;
    }
    /// <summary>Starts an explicitly parented operation, optionally detached from ambient context.</summary>
    /// <param name="name">The span display name.</param>
    /// <param name="parent">An explicit parent span, or null to use ambient context.</param>
    /// <param name="forceRoot">Whether to ignore both the explicit and ambient parent.</param>
    /// <param name="links">Contexts linked without a parent-child timing relationship.</param>
    /// <param name="attributes">Operation attributes overriding tracer-level attributes.</param>
    /// <returns>The operation span handle.</returns>
    public TraceSpan StartSpan(string name, TraceSpan? parent = null, bool forceRoot = false,
        IEnumerable<ActivityLink>? links = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        return Create(name, parent, null, attributes, forceRoot, links);
    }

    internal TraceSpan StartOperationSpan(string name, ActivityKind kind, TraceSpan? parent, IReadOnlyDictionary<string, object?> attributes)
        => Create(name, parent, null, attributes, kind: kind);

    private Dictionary<string, object?> CommonAttributes(string operation) => new(StringComparer.Ordinal)
    {
        ["gen_ai.operation.name"] = operation,
        [_latestConventions ? "gen_ai.provider.name" : "gen_ai.system"] = _serviceName,
    };

    private TraceSpan Create(string name, TraceSpan? parent, AgentTrace? trace, IReadOnlyDictionary<string, object?>? attributes,
        bool forceRoot = false, IEnumerable<ActivityLink>? links = null, Action<TraceSpan>? onEnd = null, ActivityKind kind = ActivityKind.Internal,
        MemoryTelemetryProfile? memoryProfile = null, bool includeConstructorAttributes = true)
    {
        Activity? activity = null;
        var ambient = Activity.Current;
        try
        {
            var tags = includeConstructorAttributes ? new Dictionary<string, object?>(_attributes, StringComparer.Ordinal) : new Dictionary<string, object?>(StringComparer.Ordinal);
            if (attributes is not null) foreach (var pair in attributes) tags[pair.Key] = SnapshotAttribute(pair.Value);
            if (memoryProfile != MemoryTelemetryProfile.Python || !tags.ContainsKey("gen_ai.event.start_time"))
                tags["gen_ai.event.start_time"] = DateTimeOffset.UtcNow.ToString("O");
            if (forceRoot) Activity.Current = null;
            activity = Source.CreateActivity(name, kind,
                forceRoot ? default : parent?.Context ?? ambient?.Context ?? default, tags, links, ActivityIdFormat.W3C);
            activity?.Start();
        }
        catch (Exception)
        {
            // ActivityStarted can throw after installing the activity as ambient. Attempt to finish
            // that abandoned operation before restoring the caller's context.
            var abandoned = Activity.Current;
            if (abandoned is not null && !ReferenceEquals(abandoned, ambient) && ReferenceEquals(abandoned.Source, Source))
            {
                try { abandoned.Stop(); }
                catch (Exception) { }
            }
        }
        finally { TraceSpan.RestoreAmbient(ambient); }
        return new TraceSpan(activity, trace, _conventions,
            attributes is not null && attributes.TryGetValue("gen_ai.operation.name", out var operation) ? operation as string : null, onEnd, memoryProfile);
    }

    private static object? SnapshotAttribute(object? value) => value is Array array ? array.Clone() : value;
}
