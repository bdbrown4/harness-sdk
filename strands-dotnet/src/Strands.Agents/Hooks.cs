using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>An incremental update or terminal result from an agent invocation.</summary>
public interface IAgentStreamEvent;
/// <summary>Forwards a model event as soon as the agent processes it.</summary>
/// <param name="Event">The underlying provider-neutral model event.</param>
public sealed record ModelStreamUpdateEvent(ModelStreamEvent Event) : IAgentStreamEvent
{
    private static readonly IReadOnlyDictionary<string, object?> UnboundState = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    /// <summary>Creates an invocation-bound observation while retaining the positional event constructor.</summary>
    public ModelStreamUpdateEvent(Agent agent, ModelStreamEvent @event, IReadOnlyDictionary<string, object?> invocationState) : this(@event)
    { Guard.NotNull(agent, nameof(agent)); Guard.NotNull(invocationState, nameof(invocationState)); Agent = agent; InvocationState = invocationState; }
    /// <summary>The invoking agent, or null for an unbound manually constructed event.</summary>
    [JsonIgnore]
    public Agent? Agent { get; init; }
    /// <summary>The unchanged caller-owned invocation context; empty for an unbound manually constructed event.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?> InvocationState { get; init; } = UnboundState;
}
/// <summary>Observes a completed provider content block after its raw block-stop update.</summary>
/// <param name="Agent">The invoking agent.</param>
/// <param name="ContentBlock">The detached, fully assembled block.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
[JsonConverter(typeof(ContentBlockEventJsonConverter))]
public sealed record ContentBlockEvent(Agent Agent, ContentBlock ContentBlock, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent);
/// <summary>Observes a completed middleware model result before its after hook and history commitment.</summary>
/// <param name="Agent">The invoking agent.</param>
/// <param name="Message">The detached authoritative middleware response.</param>
/// <param name="StopReason">The completed attempt's stop reason.</param>
/// <param name="InvocationState">The unchanged caller-owned context.</param>
[JsonConverter(typeof(ModelMessageEventJsonConverter))]
public sealed record ModelMessageEvent(Agent Agent, Message Message, string StopReason, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent);
/// <summary>Reports one tool progress update, correlated with the model-issued call and caller invocation context.</summary>
/// <param name="Agent">The agent executing the tool.</param>
/// <param name="ToolUse">The effective call retaining its authoritative model-issued identifier.</param>
/// <param name="Event">The tool's progress update.</param>
/// <param name="InvocationState">The unchanged caller-owned invocation context.</param>
public sealed record ToolStreamUpdateEvent(Agent Agent, ToolUseBlock ToolUse, ToolStreamEvent Event, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent);
/// <summary>Reports the correlated terminal result of a tool call after result hooks and before its batch is committed.</summary>
/// <param name="Agent">The agent executing the tool call.</param>
/// <param name="Result">The normalized rich result, including errors, denials, and missing tools.</param>
/// <param name="InvocationState">The unchanged caller-owned invocation context.</param>
public sealed record ToolResultEvent(Agent Agent, ToolResultBlock Result, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent);
/// <summary>Reports a message committed to the agent's conversation.</summary>
/// <param name="Agent">The agent whose history received the message.</param>
/// <param name="Message">The committed message.</param>
public sealed record MessageAddedEvent(Agent Agent, Message Message) : HookEvent(Agent)
{
    private static readonly IReadOnlyDictionary<string, object?> UnboundState = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    /// <summary>The unchanged caller-owned invocation context; empty for an unbound or out-of-invocation event.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?> InvocationState { get; init; } = UnboundState;
}
/// <summary>Reports the terminal result of a fully consumed agent invocation.</summary>
/// <param name="Result">The invocation result.</param>
public sealed record AgentResultEvent(AgentResult Result) : IAgentStreamEvent
{
    /// <summary>Creates an invocation-bound final observation while retaining the positional result constructor.</summary>
    public AgentResultEvent(Agent agent, AgentResult result) : this(result)
    { Guard.NotNull(agent, nameof(agent)); Agent = agent; }
    /// <summary>The invoking agent, or null for a manually constructed event.</summary>
    [JsonIgnore]
    public Agent? Agent { get; init; }
    /// <summary>The unchanged caller-owned context carried by the result.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?> InvocationState => Result.InvocationState;
}
/// <summary>Reports human input requests after completed sibling results have been durably retained.</summary>
public sealed record InterruptEvent(Agent Agent, IReadOnlyList<Interrupt> Interrupts, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent);
/// <summary>The base event for awaited agent lifecycle callbacks.</summary>
/// <param name="Agent">The agent whose lifecycle is being observed.</param>
public abstract record HookEvent(Agent Agent) : IAgentStreamEvent;
/// <summary>Runs after restoring the session and before adding the invocation's user prompt.</summary>
/// <param name="Agent">The invoking agent.</param>
/// <param name="InvocationState">The caller-owned context for this invocation.</param>
public sealed record BeforeInvocationEvent(Agent Agent, IReadOnlyDictionary<string, object?> InvocationState) : HookEvent(Agent)
{
    /// <summary>The incoming manual message batch. Null retains the original input; an empty batch appends no manual input. Agent-emitted events initially contain a detached snapshot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Message>? Messages { get; set; }
    /// <summary>Denies this invocation before its new input is committed, using the supplied assistant response.</summary>
    public string? CancelReason { get; set; }
}
/// <summary>Runs as an invocation exits, including failures and early stream disposal.</summary>
/// <param name="Agent">The invoking agent.</param>
/// <param name="Result">The terminal result when one was produced, otherwise null.</param>
/// <param name="Error">The invocation failure when one was captured, otherwise null.</param>
public sealed record AfterInvocationEvent(Agent Agent, AgentResult? Result, Exception? Error) : HookEvent(Agent)
{
    /// <summary>Creates the completion boundary with the caller's original invocation context.</summary>
    public AfterInvocationEvent(Agent agent, AgentResult? result, Exception? error, IReadOnlyDictionary<string, object?> invocationState)
        : this(agent, result, error)
    { Guard.NotNull(invocationState, nameof(invocationState)); InvocationState = invocationState; }
    /// <summary>The unchanged caller-owned context shared by every resumed cycle.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; } = new Dictionary<string, object?>();
    /// <summary>Requests another complete invocation cycle. Null stops; an empty input continues without appending. Ignored after errors, cancellation, before-invocation denial, or early stream disposal. The last callback to run wins.</summary>
    public InvocationContinuation? Resume { get => _resume; set { _resume = value; ResumeIsAutomatic = false; } }
    private InvocationContinuation? _resume;
    internal bool ResumeIsAutomatic { get; private set; }
    internal void RequestAutomaticResume()
    {
        if (_resume is not null) return;
        _resume = InvocationContinuation.Continue; ResumeIsAutomatic = true;
    }
    internal CancellationToken InvocationCancellationToken { get; init; }
    internal bool ContinuationAllowed { get; init; } = true;
    internal bool AutomaticWaitCancelled { get; set; }
}
/// <summary>Runs once during terminal shutdown, before owned resources and hook registrations are released. Every callback is attempted in ascending priority and registration order.</summary>
/// <param name="Agent">The agent ending its lifecycle.</param>
public sealed record ShutdownEvent(Agent Agent) : HookEvent(Agent);
/// <summary>Runs before a provider receives its model-visible request.</summary>
public sealed record BeforeModelCallEvent : HookEvent
{
    /// <summary>Creates a mutable model-call boundary for ephemeral context and request policy.</summary>
    public BeforeModelCallEvent(Agent agent, ModelRequest request) : this(agent, request, new Dictionary<string, object?>()) { }
    /// <summary>Creates a mutable model boundary with the original caller-owned invocation context.</summary>
    public BeforeModelCallEvent(Agent agent, ModelRequest request, IReadOnlyDictionary<string, object?> invocationState) : base(agent)
    { Guard.NotNull(invocationState, nameof(invocationState)); Request = request; InvocationState = invocationState; }
    /// <summary>The request sent after all callbacks finish. Replacing it does not change durable history.</summary>
    public ModelRequest Request { get; set; }
    /// <summary>Returns a synthetic completed response instead of calling model middleware or the provider.</summary>
    public string? CancelReason { get; set; }
    /// <summary>The unchanged caller-owned context shared with tools and other invocation hooks.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
}
/// <summary>Runs when a model call completes, fails, or its stream is disposed.</summary>
/// <param name="Agent">The invoking agent.</param>
/// <param name="Message">The assembled response if the call completed.</param>
/// <param name="StopReason">The response's stop reason if the call completed.</param>
/// <param name="Error">The captured model failure, or null.</param>
public sealed record AfterModelCallEvent(Agent Agent, Message? Message, string? StopReason, Exception? Error) : HookEvent(Agent)
{
    /// <summary>Creates a model completion boundary with the original caller-owned invocation context.</summary>
    public AfterModelCallEvent(Agent agent, Message? message, string? stopReason, Exception? error, IReadOnlyDictionary<string, object?> invocationState)
        : this(agent, message, stopReason, error)
    { Guard.NotNull(invocationState, nameof(invocationState)); InvocationState = invocationState; }
    /// <summary>The unchanged caller-owned context shared throughout this invocation.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; } = new Dictionary<string, object?>();
    /// <summary>The authoritative transformed response, validated and detached before it is committed.</summary>
    public Message? Message { get; set; } = Message;
    /// <summary>The authoritative transformed stop reason, validated before commit.</summary>
    public string? StopReason { get; set; } = StopReason;
    /// <summary>Requests another model attempt before committing this attempt's response. Cancellation is never retried.</summary>
    public bool Retry { get; set; }
    internal CancellationToken InvocationCancellationToken { get; init; }
}
/// <summary>Runs before a tool call; a callback may deny execution using CancelReason.</summary>
public sealed record BeforeToolCallEvent : HookEvent
{
    private readonly string _interruptToolUseId;
    private readonly string? _interruptBatchId;
    /// <summary>Returns a supplied human response, or pauses before executing the selected call.</summary>
    public System.Text.Json.JsonElement Interrupt(string name, System.Text.Json.JsonElement? reason = null, System.Text.Json.JsonElement? response = null) => InterruptHandler is { } handler ? handler(name, reason, response) : Agent.ResolveInterrupt(_interruptToolUseId, name, reason, InterruptSource.Hook, response, originBatchId: _interruptBatchId);
    internal Func<string, System.Text.Json.JsonElement?, System.Text.Json.JsonElement?, System.Text.Json.JsonElement>? InterruptHandler { get; init; }
    /// <summary>Creates the mutable boundary before validating and invoking a selected tool.</summary>
    public BeforeToolCallEvent(Agent agent, ToolUseBlock toolUse, IReadOnlyDictionary<string, object?> invocationState) : base(agent)
    { ToolUse = toolUse; InvocationState = invocationState; _interruptToolUseId = toolUse.ToolUseId; _interruptBatchId = agent.ActiveInterruptBatchId; }
    /// <summary>The effective tool name and input. The model's original tool-use identifier remains authoritative.</summary>
    public ToolUseBlock ToolUse { get; set; }
    /// <summary>An optional replacement implementation, validated against its own input schema.</summary>
    public ITool? SelectedTool { get; set; }
    /// <summary>The unchanged caller-owned context.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>Set a reason to deny this call. The model receives an error result.</summary>
    public string? CancelReason { get; set; }
}
/// <summary>Runs after a tool attempt has produced a success, error, denial, or cancellation result.</summary>
public sealed record AfterToolCallEvent : HookEvent
{
    /// <summary>Creates a mutable result boundary after a tool completes.</summary>
    public AfterToolCallEvent(Agent agent, ToolUseBlock toolUse, ToolResultBlock result) : this(agent, toolUse, result, new Dictionary<string, object?>()) { }
    /// <summary>Creates a mutable result boundary with the original caller-owned invocation context.</summary>
    public AfterToolCallEvent(Agent agent, ToolUseBlock toolUse, ToolResultBlock result, IReadOnlyDictionary<string, object?> invocationState) : base(agent)
    { Guard.NotNull(invocationState, nameof(invocationState)); ToolUse = toolUse; Result = result; InvocationState = invocationState; }
    /// <summary>The effective request, retaining the original model correlation identifier.</summary>
    public ToolUseBlock ToolUse { get; }
    /// <summary>The result returned to the model after callbacks finish.</summary>
    public ToolResultBlock Result { get; set; }
    /// <summary>Discards this attempt's result and executes the call again. Cancellation and failed cleanup prevent retries.</summary>
    public bool Retry { get; set; }
    /// <summary>The unchanged caller-owned context shared throughout this invocation.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
}

/// <summary>Typed, awaited hooks ordered by priority and registration. After hooks unwind in reverse order.</summary>
public sealed class HookRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private static long _sequence;
    private readonly HookRegistry? _parent;

    /// <summary>Creates an application hook registry.</summary>
    public HookRegistry() { }
    internal HookRegistry(HookRegistry parent) => _parent = parent;

    /// <summary>Registers an awaited callback and returns a handle that removes the registration.</summary>
    /// <typeparam name="T">The event type, including a base type to observe derived events.</typeparam>
    /// <param name="callback">The callback invoked for matching events.</param>
    /// <param name="order">Priority; lower values run first. Equal-priority after callbacks run in reverse registration order.</param>
    /// <returns>A disposable registration handle.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public IDisposable Register<T>(Func<T, CancellationToken, ValueTask> callback, int order = 0) where T : class, IAgentStreamEvent
    {
        Guard.NotNull(callback, nameof(callback));
        return Register(typeof(T), (value, ct) => callback((T)value, ct), order);
    }

    internal IDisposable Register(Type eventType, Func<IAgentStreamEvent, CancellationToken, ValueTask> callback, int order)
    {
        Guard.NotNull(eventType, nameof(eventType));
        Guard.NotNull(callback, nameof(callback));
        if (!typeof(IAgentStreamEvent).IsAssignableFrom(eventType) || (!eventType.IsClass && !eventType.IsInterface))
            throw new ArgumentException("Hooks require a class or interface event type.", nameof(eventType));
        lock (_gate)
        {
            var registration = new Registration(this, eventType, callback, order, Interlocked.Increment(ref _sequence));
            _registrations.Add(registration);
            return registration;
        }
    }

    /// <summary>Invokes matching callbacks sequentially. Shutdown attempts every callback and retains secondary errors; other events stop at the first failure.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="value">The event shared with matching callbacks.</param>
    /// <param name="cancellationToken">The token forwarded to each callback.</param>
    /// <returns>A task that completes after every matching callback completes.</returns>
    public async ValueTask InvokeAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, IAgentStreamEvent
    {
        var registrations = Snapshot().Where(r => r.Type.IsInstanceOfType(value)).OrderBy(r => r.Order).ThenBy(r => r.Sequence).ToArray();
        if (value is AfterInvocationEvent or AfterModelCallEvent or AfterToolCallEvent or AfterToolsEvent)
            registrations = registrations.OrderBy(r => r.Order).ThenByDescending(r => r.Sequence).ToArray();
        Exception? failure = null;
        var interruptions = new List<InterruptException>();
        foreach (var registration in registrations)
        {
            try { await registration.Callback(value, cancellationToken).ConfigureAwait(false); }
            catch (InterruptException interrupted) when (value is not ShutdownEvent) { interruptions.Add(interrupted); }
            catch (Exception error) when (value is ShutdownEvent)
            {
                if (failure is null) failure = error;
                else
                {
                    const string key = "Strands.CleanupErrors";
                    if (failure.Data[key] is not List<Exception> errors) failure.Data[key] = errors = [];
                    errors.Add(error);
                }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (interruptions.Count > 0)
        {
            var questions = interruptions.SelectMany(error => error.Interrupts).ToArray();
            if (questions.Select(question => question.Name).Distinct(StringComparer.Ordinal).Count() != questions.Length)
                throw new ArgumentException("Lifecycle callbacks cannot use duplicate interrupt names at the same boundary.");
            if (interruptions.Count == 1) ExceptionDispatchInfo.Capture(interruptions[0]).Throw();
            var combined = new InterruptException(questions);
            var errors = interruptions.SelectMany(error => error.Data["Strands.CleanupErrors"] as List<Exception> ?? []).ToList();
            if (errors.Count > 0) combined.Data["Strands.CleanupErrors"] = errors;
            throw combined;
        }
    }

    private Registration[] Snapshot()
    {
        Registration[] local;
        lock (_gate) local = _registrations.ToArray();
        return _parent is null ? local : _parent.Snapshot().Concat(local).ToArray();
    }

    internal void ClearOwned() { lock (_gate) _registrations.Clear(); }

    private sealed record Registration(HookRegistry Owner, Type Type, Func<IAgentStreamEvent, CancellationToken, ValueTask> Callback, int Order, long Sequence) : IDisposable
    {
        public void Dispose() { lock (Owner._gate) Owner._registrations.Remove(this); }
    }
}

internal sealed class ContentBlockEventJsonConverter : JsonConverter<ContentBlockEvent>
{
    public ContentBlockEventJsonConverter() { }
    public override ContentBlockEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("A completed observation payload cannot restore its invoking agent.");
    public override void Write(Utf8JsonWriter writer, ContentBlockEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(nameof(ContentBlockEvent.ContentBlock)) ?? nameof(ContentBlockEvent.ContentBlock));
        JsonSerializer.Serialize(writer, value.ContentBlock, options);
        writer.WriteEndObject();
    }
}

internal sealed class ModelMessageEventJsonConverter : JsonConverter<ModelMessageEvent>
{
    public ModelMessageEventJsonConverter() { }
    public override ModelMessageEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("A completed observation payload cannot restore its invoking agent.");
    public override void Write(Utf8JsonWriter writer, ModelMessageEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(nameof(ModelMessageEvent.Message)) ?? nameof(ModelMessageEvent.Message));
        JsonSerializer.Serialize(writer, value.Message, options);
        writer.WriteString(options.PropertyNamingPolicy?.ConvertName(nameof(ModelMessageEvent.StopReason)) ?? nameof(ModelMessageEvent.StopReason), value.StopReason);
        writer.WriteEndObject();
    }
}
