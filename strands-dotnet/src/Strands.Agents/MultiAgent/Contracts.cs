using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.MultiAgent;

/// <summary>The lifecycle of a node or orchestrator.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Status>))]
public enum Status
{
    /// <summary>Execution has not started.</summary>
    [JsonStringEnumMemberName("PENDING")] Pending,
    /// <summary>Execution is active.</summary>
    [JsonStringEnumMemberName("EXECUTING")] Executing,
    /// <summary>Execution completed.</summary>
    [JsonStringEnumMemberName("COMPLETED")] Completed,
    /// <summary>Execution failed.</summary>
    [JsonStringEnumMemberName("FAILED")] Failed,
    /// <summary>Execution was cancelled.</summary>
    [JsonStringEnumMemberName("CANCELLED")] Cancelled,
    /// <summary>Execution is paused awaiting human responses.</summary>
    [JsonStringEnumMemberName("INTERRUPTED")] Interrupted
}

/// <summary>Detached typed input routed between orchestration nodes.</summary>
public sealed class MultiAgentInput
{
    /// <summary>Copies typed content without converting media or JSON to text.</summary>
    public MultiAgentInput(IReadOnlyList<ContentBlock> content) => Content = new Message(Role.User, content).Content;
    /// <summary>Creates text input.</summary>
    public MultiAgentInput(string text) : this([new TextBlock(text ?? throw new ArgumentNullException(nameof(text)))]) => IsTextInput = true;
    /// <summary>Creates response-only input for a pending orchestration. An empty list retries an already accepted answer or rechecks an unanswered pause.</summary>
    public static MultiAgentInput Resume(IReadOnlyList<InterruptResponse> responses)
    {
        Guard.NotNull(responses, nameof(responses));
        var copy = responses.Select(response => response is null ? throw new ArgumentException("Responses cannot contain null.", nameof(responses)) : new InterruptResponse(response.InterruptId, response.Response)).ToArray();
        if (copy.Select(response => response.InterruptId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Resume requires distinct interrupt identifiers.", nameof(responses));
        return new MultiAgentInput(Array.Empty<ContentBlock>()) { InterruptResponses = Array.AsReadOnly(copy), IsResume = true };
    }
    /// <summary>The detached ordered content.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
    /// <summary>Whether the caller supplied the string constructor, retained for Python input formatting.</summary>
    public bool IsTextInput { get; }
    /// <summary>The detached human responses, or an empty list for ordinary typed input.</summary>
    public IReadOnlyList<InterruptResponse> InterruptResponses { get; private init; } = [];
    /// <summary>Whether this input resumes outstanding human requests rather than supplying new content.</summary>
    public bool IsResume { get; private init; }
    /// <summary>The text blocks concatenated for display.</summary>
    public string Text => string.Concat(Content.OfType<TextBlock>().Select(block => block.Text));
}

/// <summary>Options shared by all nodes in one invocation. The caller owns invocation state.</summary>
public sealed record MultiAgentInvokeOptions
{
    /// <summary>The caller-owned object forwarded by reference without SDK-owned keys.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; init; } = new Dictionary<string, object?>();
}

/// <summary>A composable multi-agent orchestrator. Dependencies remain caller-owned.</summary>
public interface IMultiAgent
{
    /// <summary>The orchestrator identifier.</summary>
    string Id { get; }
    /// <summary>Consumes the complete stream and returns its terminal result.</summary>
    Task<MultiAgentResult> InvokeAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Streams lifecycle and node events followed by one terminal result event.</summary>
    IAsyncEnumerable<IMultiAgentStreamEvent> StreamAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>An optional capability describing a recoverable paused execution, including accepted answers awaiting retry after a failure.</summary>
public interface IInterruptibleMultiAgent : IMultiAgent
{
    /// <summary>Whether response-only input can continue the retained execution.</summary>
    bool CanResume { get; }
    /// <summary>Detached unanswered requests. This can be empty when every answer was accepted before a later failure.</summary>
    IReadOnlyList<Interrupt> PendingInterrupts { get; }
}

/// <summary>An immutable execution result for one node visit.</summary>
public sealed class NodeResult
{
    /// <summary>Creates a detached terminal result.</summary>
    [JsonConstructor]
    public NodeResult(string nodeId, Status status, IReadOnlyList<ContentBlock>? content = null, Usage? usage = null, TimeSpan duration = default, Exception? error = null, JsonElement? structuredOutput = null, IReadOnlyList<Interrupt>? interrupts = null)
        : this(nodeId, status, content, usage, duration, error, structuredOutput, interrupts, null, null) { }
    /// <summary>Creates a detached terminal result with one optional live typed result. Typed result references remain locally owned.</summary>
    public NodeResult(string nodeId, Status status, IReadOnlyList<ContentBlock>? content, Usage? usage, TimeSpan duration, Exception? error,
        JsonElement? structuredOutput, IReadOnlyList<Interrupt>? interrupts, AgentResult? agentResult, MultiAgentResult? multiAgentResult)
    {
        if (agentResult is not null && multiAgentResult is not null) throw new ArgumentException("A node result cannot contain both typed result arms.", nameof(multiAgentResult));
        AgentResult = agentResult; MultiAgentResult = multiAgentResult;
        Guard.NotNullOrWhiteSpace(nodeId, nameof(nodeId));
        if (status is not (Status.Completed or Status.Failed or Status.Cancelled or Status.Interrupted)) throw new ArgumentOutOfRangeException(nameof(status));
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        NodeId = nodeId; Status = status; Content = new Message(Role.Assistant, content ?? []).Content;
        Usage = usage ?? new(); ValidateUsage(Usage); Duration = duration; Error = error; StructuredOutput = structuredOutput?.Clone();
        Interrupts = CopyInterrupts(interrupts ?? []);
        if ((status == Status.Interrupted) != (Interrupts.Count > 0)) throw new ArgumentException("Only interrupted node results carry nonempty human requests.", nameof(interrupts));
    }
    /// <summary>The node identifier.</summary>
    public string NodeId { get; }
    /// <summary>The terminal lifecycle status.</summary>
    public Status Status { get; }
    /// <summary>Detached ordered node output.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
    /// <summary>Reported token usage for this visit.</summary>
    public Usage Usage { get; }
    /// <summary>The elapsed node execution time.</summary>
    public TimeSpan Duration { get; }
    /// <summary>The original local failure, excluded from serialization.</summary>
    [JsonIgnore] public Exception? Error { get; }
    /// <summary>Detached structured output when supplied by the node.</summary>
    public JsonElement? StructuredOutput { get; }
    /// <summary>Detached unanswered human requests for an interrupted node visit.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; }
    /// <summary>The original live single-agent result, excluded from ordinary JSON. Snapshot restoration supplies a safe result with fresh local telemetry and state.</summary>
    [JsonIgnore] public AgentResult? AgentResult { get; }
    /// <summary>The original live nested orchestration result, excluded from ordinary JSON.</summary>
    [JsonIgnore] public MultiAgentResult? MultiAgentResult { get; }
    /// <summary>Returns original single-agent result references through the selected typed result arms.</summary>
    /// <returns>Read-only membership in depth-first, latest-per-node order at each nested boundary.</returns>
    public IReadOnlyList<AgentResult> GetAgentResults()
    {
        var leaves = new List<AgentResult>();
        var pending = new Stack<NodeResult>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.AgentResult is { } agent) { leaves.Add(agent); continue; }
            if (current.MultiAgentResult is not { } nested) continue;
            var latest = nested.GetLatestNodeResults();
            for (var index = latest.Count - 1; index >= 0; index--) pending.Push(latest[index]);
        }
        return Array.AsReadOnly(leaves.ToArray());
    }
    /// <summary>Renders this node using the explicit Python result presentation.</summary>
    /// <returns>The selected inner result presentation or the qualified native error/text fallback.</returns>
    public string RenderPython()
    {
        if (AgentResult is { } agent) return AgentResultRendering.Render(agent, AgentResultOutputProfile.Python);
        if (MultiAgentResult is { } nested) return nested.RenderPython();
        return Error?.Message ?? string.Concat(Content.OfType<TextBlock>().Select(block => block.Text));
    }
    internal static IReadOnlyList<Interrupt> CopyInterrupts(IReadOnlyList<Interrupt> interrupts)
    {
        var copy = interrupts.Select(value => value is null ? throw new ArgumentException("Interrupts cannot contain null.", nameof(interrupts)) : new Interrupt(value.Id, value.Name, value.ToolUseId, value.Reason, value.Source, value.IsBatch)).ToArray();
        if (copy.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("Interrupt identifiers must be unique.", nameof(interrupts));
        return Array.AsReadOnly(copy);
    }
    internal static void ValidateUsage(Usage usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CacheReadInputTokens < 0 || usage.CacheWriteInputTokens < 0 || usage.ReportedTotalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Node usage counters cannot be negative.");
        _ = usage.TotalTokens;
    }
}

/// <summary>Runtime state of one node, retained for the lifetime of its graph invocation.</summary>
public sealed class NodeState
{
    private readonly List<NodeResult> _results = [];
    private readonly object _gate = new();
    /// <summary>The most recent lifecycle status.</summary>
    public Status Status { get; internal set; } = Status.Pending;
    /// <summary>Whether an execution path ended at this node.</summary>
    public bool Terminus { get; internal set; }
    /// <summary>A snapshot of this node's visits in completion order.</summary>
    public IReadOnlyList<NodeResult> Results { get { lock (_gate) return Array.AsReadOnly(_results.ToArray()); } }
    /// <summary>Content of the most recent result.</summary>
    public IReadOnlyList<ContentBlock> Content { get { lock (_gate) return _results.LastOrDefault()?.Content ?? []; } }
    internal AgentSnapshot? BaselineSnapshot { get; set; }
    internal AgentSnapshot? InterruptedAgentSnapshot { get; set; }
    internal MultiAgentSnapshot? InterruptedMultiAgentSnapshot { get; set; }
    internal JsonElement? InterruptedNodeSnapshot { get; set; }
    internal bool IsResuming { get; set; }
    internal NodeHookPause? HookPause { get; set; }
    internal IReadOnlyList<InterruptResponse> DeferredResponses { get; set; } = [];
    internal bool HasPendingBody => InterruptedAgentSnapshot is not null || InterruptedMultiAgentSnapshot is not null || InterruptedNodeSnapshot is not null || _bodyInterrupts.Count > 0;
    internal bool HasPendingExecution => HookPause is not null || HasPendingBody;
    private IReadOnlyList<Interrupt> _bodyInterrupts = [];
    internal IReadOnlyList<Interrupt> PendingBodyInterrupts => _bodyInterrupts;
    /// <summary>Unanswered requests retained independently of a failed resume attempt's result.</summary>
    public IReadOnlyList<Interrupt> PendingInterrupts { get => NodeResult.CopyInterrupts((HookPause?.Unanswered ?? []).Concat(_bodyInterrupts.Where(question => !DeferredResponses.Any(response => response.InterruptId == question.Id))).ToArray()); internal set => _bodyInterrupts = NodeResult.CopyInterrupts(value); }
    internal NodeResult? Add(NodeResult result)
    {
        lock (_gate)
        {
            var replaced = _results.LastOrDefault() is { } previous && (IsResuming || previous.Status == Status.Interrupted) ? previous : null;
            if (replaced is not null) _results.RemoveAt(_results.Count - 1);
            _results.Add(result); Status = result.Status;
            IsResuming = false;
            if (result.Status == Status.Interrupted) PendingInterrupts = result.Interrupts.Where(question => HookPause?.Interrupts.Any(hook => hook.Id == question.Id) != true).ToArray();
            else if (InterruptedAgentSnapshot is null && InterruptedMultiAgentSnapshot is null && InterruptedNodeSnapshot is null) PendingInterrupts = [];
            return replaced;
        }
    }
}

/// <summary>Per-invocation graph state. Framework scheduling data is separate from caller invocation state.</summary>
public sealed class MultiAgentState
{
    private readonly object _gate = new();
    private readonly List<NodeResult> _results = [];
    internal MultiAgentState(IEnumerable<string> nodeIds, DateTimeOffset startTime = default)
    { Nodes = new ReadOnlyDictionary<string, NodeState>(nodeIds.ToDictionary(id => id, _ => new NodeState(), StringComparer.Ordinal)); StartTime = startTime == default ? DateTimeOffset.UtcNow : startTime; }
    /// <summary>The invocation start time.</summary>
    public DateTimeOffset StartTime { get; private set; }
    /// <summary>The number of node visits started.</summary>
    public long Steps { get; internal set; }
    /// <summary>Tracked node states keyed by identifier.</summary>
    public IReadOnlyDictionary<string, NodeState> Nodes { get; private set; }
    /// <summary>A snapshot of all node visits in completion order.</summary>
    public IReadOnlyList<NodeResult> Results { get { lock (_gate) return Array.AsReadOnly(_results.ToArray()); } }
    /// <summary>Application JSON state for graph callbacks and custom nodes.</summary>
    public StateStore App { get; } = new();
    /// <summary>Returns a tracked node state, or null when its identifier is absent.</summary>
    public NodeState? Node(string id) => Nodes.TryGetValue(id, out var state) ? state : null;
    internal void Add(NodeResult result) { var replaced = Nodes[result.NodeId].Add(result); lock (_gate) { if (replaced is not null) _results.Remove(replaced); _results.Add(result); } }
    internal void AdoptSessionState(MultiAgentState source)
    {
        if (ReferenceEquals(this, source)) return;
        var app = source.App.GetAll();
        var results = source.Results;
        lock (_gate)
        {
            App.Replace(app);
            StartTime = source.StartTime;
            Steps = source.Steps;
            Nodes = source.Nodes;
            _results.Clear();
            _results.AddRange(results);
        }
    }

}

/// <summary>The aggregate result of one complete or failed graph invocation.</summary>
public sealed class MultiAgentResult
{
    private readonly string _text;
    /// <summary>Copies node results and terminus content and aggregates usage.</summary>
    public MultiAgentResult(IReadOnlyList<NodeResult> results, IReadOnlyList<ContentBlock>? content = null, TimeSpan duration = default, Status? status = null, Exception? error = null, IReadOnlyList<Interrupt>? interrupts = null)
    {
        Guard.NotNull(results, nameof(results));
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (results.Any(result => result is null)) throw new ArgumentException("Node results cannot contain null.", nameof(results));
        var message = new Message(Role.Assistant, content ?? []);
        Results = Array.AsReadOnly(results.ToArray()); Content = message.Content; _text = message.Text; Duration = duration;
        Status = status ?? (results.Any(result => result.Status == Status.Failed) ? Status.Failed : results.Any(result => result.Status == Status.Interrupted) ? Status.Interrupted : results.Any(result => result.Status == Status.Cancelled) ? Status.Cancelled : Status.Completed);
        if (Status is not (Status.Completed or Status.Failed or Status.Cancelled or Status.Interrupted)) throw new ArgumentOutOfRangeException(nameof(status));
        Usage = results.Aggregate(new Usage(), (sum, result) => sum + result.Usage); Error = error;
        Interrupts = NodeResult.CopyInterrupts(interrupts ?? Results.SelectMany(result => result.Interrupts).ToArray());
    }
    /// <summary>The aggregate terminal status.</summary>
    public Status Status { get; }
    /// <summary>Detached node visits in completion order.</summary>
    public IReadOnlyList<NodeResult> Results { get; }
    /// <summary>Combined content from terminus nodes in graph declaration order.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }
    /// <summary>Total reported token usage across every node visit.</summary>
    public Usage Usage { get; }
    /// <summary>The detached unanswered human requests aggregated from current node results.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; }
    /// <summary>The elapsed invocation time.</summary>
    public TimeSpan Duration { get; }
    /// <summary>An optional original graph failure, excluded from serialization.</summary>
    [JsonIgnore] public Exception? Error { get; }
    /// <summary>Renders pending interrupts or labelled node results using the explicit Python presentation.</summary>
    /// <returns>The opt-in presentation without changing terminus content, node visits, or usage.</returns>
    public string RenderPython()
    {
        if (Interrupts.Count > 0) return AgentResultRendering.InterruptPython(Interrupts);
        var parts = new List<string>();
        foreach (var node in GetLatestNodeResults())
        {
            var text = node.RenderPython();
            if (text.EndsWith("\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
            if (text.Length > 0) parts.Add(node.NodeId + ": " + text);
        }
        return string.Join("\n", parts);
    }

    internal IReadOnlyList<NodeResult> GetLatestNodeResults()
    {
        // Replacing a Python dictionary value retains its original insertion position.
        var latest = new List<NodeResult>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var visit in Results)
        {
            if (positions.TryGetValue(visit.NodeId, out var position)) latest[position] = visit;
            else { positions.Add(visit.NodeId, latest.Count); latest.Add(visit); }
        }
        return latest;
    }
    /// <summary>Concatenates text content for display.</summary>
    public override string ToString() => _text;
}
