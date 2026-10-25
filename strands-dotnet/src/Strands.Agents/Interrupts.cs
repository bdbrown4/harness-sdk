using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>The boundary at which human input was requested.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterruptSource>))]
public enum InterruptSource
{
    /// <summary>A tool implementation requested input.</summary>
    Tool,
    /// <summary>A before-tool lifecycle callback requested input.</summary>
    Hook,
    /// <summary>A tool middleware callback requested input.</summary>
    Middleware,
    /// <summary>A multi-agent orchestrator callback requested input before a node call; its correlation identifies a node.</summary>
    OrchestratorHook,
    /// <summary>A remote agent requested input; its local correlation identifies an orchestration node rather than a model-issued tool call.</summary>
    RemoteAgent
}

/// <summary>An immutable request for human input, scoped to one model-issued tool call.</summary>
public sealed record Interrupt
{
    /// <summary>Creates a detached request. The agent generates opaque identifiers stable across replay.</summary>
    [JsonConstructor]
    public Interrupt(string id, string name, string toolUseId, JsonElement? reason = null, InterruptSource source = InterruptSource.Hook, bool isBatch = false)
    {
        Guard.NotNullOrWhiteSpace(id, nameof(id)); Guard.NotNullOrWhiteSpace(name, nameof(name)); Guard.NotNullOrWhiteSpace(toolUseId, nameof(toolUseId));
        if (!Enum.IsDefined(typeof(InterruptSource), source)) throw new ArgumentOutOfRangeException(nameof(source));
        Id = id; Name = name; ToolUseId = toolUseId; Reason = InterruptJson.Copy(reason, nameof(reason)); Source = source; IsBatch = isBatch;
    }
    /// <summary>The opaque identifier to use when providing a response.</summary>
    public string Id { get; }
    /// <summary>The application-defined name of the request.</summary>
    public string Name { get; }
    /// <summary>The original correlated tool call, or node identifier for OrchestratorHook and RemoteAgent.</summary>
    public string ToolUseId { get; }
    /// <summary>Optional detached JSON explaining the requested input.</summary>
    public JsonElement? Reason { get; }
    /// <summary>The boundary that requested the input.</summary>
    public InterruptSource Source { get; }
    /// <summary>Whether the request gates the complete tool batch rather than its correlated call alone.</summary>
    public bool IsBatch { get; }
}

/// <summary>A detached human response. JSON null is a supplied response, distinct from an absent response.</summary>
public sealed record InterruptResponse
{
    /// <summary>Creates a response to one outstanding interrupt.</summary>
    [JsonConstructor]
    public InterruptResponse(string interruptId, JsonElement response)
    {
        Guard.NotNullOrWhiteSpace(interruptId, nameof(interruptId));
        InterruptId = interruptId; Response = InterruptJson.Copy(response, nameof(response))!.Value;
    }
    /// <summary>The identifier from the interrupt request.</summary>
    public string InterruptId { get; }
    /// <summary>The detached response, including an explicit JSON null.</summary>
    public JsonElement Response { get; }
}

/// <summary>A completed tool body whose surrounding middleware has not yet accepted its output.</summary>
public sealed record ToolExecutionCheckpoint
{
    /// <summary>Captures the effective input, selected tool name, and rich output without retaining caller-owned JSON.</summary>
    [JsonConstructor]
    public ToolExecutionCheckpoint(ToolUseBlock toolUse, string toolName, ToolResultBlock result)
    {
        Guard.NotNull(toolUse, nameof(toolUse)); Guard.NotNullOrWhiteSpace(toolName, nameof(toolName)); Guard.NotNull(result, nameof(result));
        if (toolUse.ToolUseId != result.ToolUseId || !Enum.IsDefined(typeof(ToolResultStatus), result.Status)) throw new ArgumentException("An execution checkpoint must retain its original correlation and a valid result status.", nameof(result));
        ToolUse = (ToolUseBlock)Message.CloneBlock(toolUse); ToolName = toolName; Result = (ToolResultBlock)Message.CloneBlock(result);
        IteratorCleanupFailed = result.IteratorCleanupFailed;
    }
    /// <summary>The effective input used by the completed tool body.</summary>
    public ToolUseBlock ToolUse { get; }
    /// <summary>The selected implementation's model-facing name.</summary>
    public string ToolName { get; }
    /// <summary>The body output reused when middleware resumes.</summary>
    public ToolResultBlock Result { get; }
    [JsonInclude]
    internal bool IteratorCleanupFailed { get; init; }
}

/// <summary>The durable pending tool batch. Completed side effects are retained separately until every call has a result.</summary>
public sealed record InterruptState
{
    /// <summary>Creates and validates a detached, JSON-serializable pending batch.</summary>
    [JsonConstructor]
    public InterruptState(Message assistantMessage, IReadOnlyList<ToolResultBlock> completedToolResults,
        IReadOnlyList<Interrupt> interrupts, IReadOnlyList<InterruptResponse> responses, IReadOnlyList<ToolExecutionCheckpoint>? toolExecutions = null)
    {
        Guard.NotNull(assistantMessage, nameof(assistantMessage)); Guard.NotNull(completedToolResults, nameof(completedToolResults));
        Guard.NotNull(interrupts, nameof(interrupts)); Guard.NotNull(responses, nameof(responses));
        AssistantMessage = new(assistantMessage.Role, assistantMessage.Content, assistantMessage.TrackingId, assistantMessage.Metadata);
        var calls = AssistantMessage.Content.OfType<ToolUseBlock>().ToArray();
        if (AssistantMessage.Role != Role.Assistant || calls.Length == 0 || calls.Select(call => call.ToolUseId).Distinct(StringComparer.Ordinal).Count() != calls.Length)
            throw new ArgumentException("A pending batch requires an assistant message with unique tool calls.", nameof(assistantMessage));
        var ids = new HashSet<string>(calls.Select(call => call.ToolUseId), StringComparer.Ordinal);
        var results = completedToolResults.Select(result => result is null ? throw new ArgumentException("A completed result cannot be null.", nameof(completedToolResults)) : (ToolResultBlock)Message.CloneBlock(result)).ToArray();
        if (results.Any(result => !ids.Contains(result.ToolUseId) || !Enum.IsDefined(typeof(ToolResultStatus), result.Status)) || results.Select(result => result.ToolUseId).Distinct(StringComparer.Ordinal).Count() != results.Length)
            throw new ArgumentException("Completed results must uniquely correspond to the pending calls.", nameof(completedToolResults));
        var questions = interrupts.Select(value => value is null ? throw new ArgumentException("An interrupt cannot be null.", nameof(interrupts)) : new Interrupt(value.Id, value.Name, value.ToolUseId, value.Reason, value.Source, value.IsBatch)).ToArray();
        if (questions.Any(value => !ids.Contains(value.ToolUseId) || value.Source is InterruptSource.OrchestratorHook or InterruptSource.RemoteAgent) || questions.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != questions.Length)
            throw new ArgumentException("Agent interrupts must be unique and correspond to pending tool calls, rather than orchestrator nodes.", nameof(interrupts));
        if (questions.GroupBy(value => (value.ToolUseId, value.Name, value.Source, value.IsBatch)).Any(group => group.Count() > 1 && group.Any(question => !AgentToolInterruptJournal.IsForwarded(question))))
            throw new ArgumentException("A pending boundary cannot contain ambiguous requests with the same name.", nameof(interrupts));
        var questionIds = new HashSet<string>(questions.Select(value => value.Id), StringComparer.Ordinal);
        var answers = responses.Select(value => value is null ? throw new ArgumentException("A response cannot be null.", nameof(responses)) : new InterruptResponse(value.InterruptId, value.Response)).ToArray();
        if (answers.Any(value => !questionIds.Contains(value.InterruptId)) || answers.Select(value => value.InterruptId).Distinct(StringComparer.Ordinal).Count() != answers.Length)
            throw new ArgumentException("Responses must uniquely correspond to known interrupts.", nameof(responses));
        CompletedToolResults = Array.AsReadOnly(results); Interrupts = Array.AsReadOnly(questions); Responses = Array.AsReadOnly(answers);
        var executions = (toolExecutions ?? []).Select(value => value is null ? throw new ArgumentException("An execution checkpoint cannot be null.", nameof(toolExecutions)) : new ToolExecutionCheckpoint(value.ToolUse, value.ToolName, value.Result with { IteratorCleanupFailed = value.IteratorCleanupFailed })).ToArray();
        if (executions.Any(value => !ids.Contains(value.ToolUse.ToolUseId)) || executions.Select(value => value.ToolUse.ToolUseId).Distinct(StringComparer.Ordinal).Count() != executions.Length)
            throw new ArgumentException("Execution checkpoints must uniquely correspond to the pending calls.", nameof(toolExecutions));
        ToolExecutions = Array.AsReadOnly(executions);
    }
    /// <summary>The owned assistant tool request, committed in paired history unless AssistantMessageIsIndependent is true.</summary>
    public Message AssistantMessage { get; }
    /// <summary>Accepted results which must never be executed again during resume.</summary>
    public IReadOnlyList<ToolResultBlock> CompletedToolResults { get; }
    /// <summary>Requests raised in this batch, including requests already answered.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; }
    /// <summary>Responses retained while an incomplete call may need to replay multiple input boundaries.</summary>
    public IReadOnlyList<InterruptResponse> Responses { get; }
    /// <summary>Completed body outputs reused if an outer middleware approval interrupted after execution.</summary>
    public IReadOnlyList<ToolExecutionCheckpoint> ToolExecutions { get; }
    private IReadOnlyDictionary<string, JsonElement> _context = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    [JsonInclude]
    internal IReadOnlyDictionary<string, JsonElement> Context { get => _context; init => _context = AgentToolInterruptJournal.Copy(value, this); }
    /// <summary>Whether the assistant is owned independently of retained history. False preserves the native paired domain.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AssistantMessageIsIndependent { get; init; }
    // Producer origin survives paired pre-clear saves without changing the committed-history domain.
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    internal bool SourceHistoryOwned { get; init; }
    internal InterruptState Copy() => new(AssistantMessage, CompletedToolResults, Interrupts, Responses, ToolExecutions)
    { AssistantMessageIsIndependent = AssistantMessageIsIndependent, SourceHistoryOwned = SourceHistoryOwned, Context = Context };
    internal void ValidateHistory(IReadOnlyList<Message> messages) => ValidateHistory(messages, allowIndependent: false);
    internal void ValidateHistory(IReadOnlyList<Message> messages, bool allowIndependent)
        => ValidateHistory(messages, allowIndependent, independentOwnerProfile: null);

    // Null means a generic legacy envelope has not identified the receiving history owner.
    internal void ValidateHistory(IReadOnlyList<Message> messages, bool allowIndependent, ModelHistoryBehavior? independentOwnerProfile)
    {
        if (Interrupts.Count == 0 || CompletedToolResults.Count >= AssistantMessage.Content.OfType<ToolUseBlock>().Count())
            throw new InvalidDataException("A durable interrupted batch requires a question and at least one incomplete call.");
        if (SourceHistoryOwned && (!allowIndependent || independentOwnerProfile == ModelHistoryBehavior.Native))
            throw new InvalidDataException("Source-owned pending execution requires explicit retained-history admission.");
        if (AssistantMessageIsIndependent)
        {
            if (!allowIndependent || independentOwnerProfile == ModelHistoryBehavior.Native)
                throw new InvalidDataException("Independent pending execution requires explicit retained-history admission.");
            if (independentOwnerProfile is { } profile && !Enum.IsDefined(typeof(ModelHistoryBehavior), profile))
                throw new InvalidDataException("The independent history owner is unknown.");
            if (independentOwnerProfile == ModelHistoryBehavior.Python && messages.Count != 0)
                throw new InvalidDataException("Independent Python pending execution requires empty retained history.");
            ConversationHistory.Validate(messages, independentOwnerProfile ?? ModelHistoryBehavior.Native);
            if (messages.LastOrDefault()?.Content.OfType<ToolUseBlock>().Any() == true)
                throw new InvalidDataException("Independent pending execution requires complete retained history.");
            return;
        }
        var last = messages.LastOrDefault();
        if (last?.Role != Role.Assistant || last.TrackingId != AssistantMessage.TrackingId ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(last), JsonSerializer.SerializeToElement(AssistantMessage)))
            throw new InvalidDataException("The pending interrupt batch must match the last committed assistant message.");
    }
}

/// <summary>Intentional control flow requesting human input. Tool error conversion must not consume it.</summary>
public sealed class InterruptException : Exception
{
    /// <summary>Creates a pause signal carrying one detached request.</summary>
    public InterruptException(Interrupt interrupt) : this([interrupt]) { }
    /// <summary>Creates a pause signal carrying multiple detached requests aggregated by lifecycle callbacks.</summary>
    public InterruptException(IReadOnlyList<Interrupt> interrupts) : base("Human input is required to continue tool execution.")
    {
        Guard.NotNull(interrupts, nameof(interrupts));
        if (interrupts.Count == 0) throw new ArgumentException("A pause signal requires at least one interrupt.", nameof(interrupts));
        Interrupts = Array.AsReadOnly(interrupts.Select(value => value is null ? throw new ArgumentException("An interrupt cannot be null.", nameof(interrupts)) : new Interrupt(value.Id, value.Name, value.ToolUseId, value.Reason, value.Source, value.IsBatch)).ToArray());
    }
    /// <summary>The first human input request.</summary>
    public Interrupt Interrupt => Interrupts[0];
    /// <summary>All human input requests raised at this boundary.</summary>
    public IReadOnlyList<Interrupt> Interrupts { get; }
}

internal static class InterruptJson
{
    internal static JsonElement? Copy(JsonElement? value, string name)
    {
        try
        {
            if (value is { ValueKind: JsonValueKind.Undefined }) throw new ArgumentException("An interrupt JSON value cannot be undefined.", name);
            return value?.Clone();
        }
        catch (ObjectDisposedException error) { throw new ArgumentException("An interrupt JSON value must remain readable until it is copied.", name, error); }
    }
}
