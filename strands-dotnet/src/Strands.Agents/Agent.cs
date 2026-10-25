using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

/// <summary>A native, in-process Strands agent. Each instance owns one conversation.</summary>
public sealed partial class Agent : IAsyncDisposable
{
    private static readonly AsyncLocal<LifecycleScope?> ActiveLifecycle = new();
    private readonly AgentOptions _options;
    private readonly List<Message> _messages;
    private readonly object _historyGate = new();
    private readonly Dictionary<string, (ITool Tool, JsonSchema Schema)> _tools;
    private readonly HookRegistry _hooks;
    private readonly object _initializationGate = new();
    private IReadOnlyList<ITool> _toolSnapshot;
    private readonly List<ITool> _applicationTools;
    private Task? _initialization;
    private long _savedStateVersion;
    private long _savedModelStateVersion;
    private int _modelHistoryCount;
    private ModelInputReceipt? _modelInputReceipt;
    private int EffectiveModelHistoryCount => _modelInputReceipt?.ConsumedMessageCount ?? _modelHistoryCount;
    private Task? _disposal;
    private Task? _shutdown;
    private bool _disposed;
    private int _invoking;
    private bool _restored;
    private string _id;
    private readonly IReadOnlyList<IPlugin> _plugins;
    private readonly Context.ContextManager? _contextManager;
    private readonly Memory.MemoryManager? _memoryManager;
    internal IReadOnlyDictionary<string, JsonElement>? RestoredContextStash { get; private set; }

    /// <summary>Constructs an agent and validates tools, limits, and initial conversation history.</summary>
    public Agent(AgentOptions options)
    {
        Guard.NotNull(options, nameof(options));
        if (options.SystemPrompt is not null && options.SystemPromptContent is not null)
            throw new ArgumentException("Configure either SystemPrompt or SystemPromptContent, not both.", nameof(options));
        var systemPromptContent = SessionSnapshot.CopySystemPromptContent(options.SystemPromptContent, allowGuardContent: true);
        Guard.NotNull(options.Model, nameof(options.Model));
        var structuredOutput = options.StructuredOutput is null ? null : CopyStructuredOutputOptions(options.StructuredOutput);
        if (structuredOutput is not null) _ = new StructuredOutputTool<JsonElement>(structuredOutput);
        Guard.NotNull(options.Hooks, nameof(options.Hooks));
        Guard.NotNull(options.Middleware, nameof(options.Middleware));
        Guard.NotNull(options.Interventions, nameof(options.Interventions));
        Guard.NotNull(options.Tools, nameof(options.Tools));
        Guard.NotNull(options.ToolProviders, nameof(options.ToolProviders));
        var providers = options.ToolProviders.ToArray();
        foreach (var provider in providers) Guard.NotNull(provider, nameof(options.ToolProviders));
        Guard.NotNull(options.Messages, nameof(options.Messages));
        Guard.NotNull(options.Plugins, nameof(options.Plugins));
        Guard.NotNull(options.State, nameof(options.State));
        Guard.NotNull(options.ModelState, nameof(options.ModelState));
        Guard.NotNull(options.OwnedResources, nameof(options.OwnedResources));
        Guard.NotNullOrWhiteSpace(options.Name, nameof(options.Name));
        Guard.NotNullOrWhiteSpace(options.Id, nameof(options.Id));
        var catalogManager = SnapshotSessionManager.Select(options);
        var repositoryManager = SessionRepositories.RepositorySessionManager.Select(options);
        if (options.MaximumInvocationHistory is <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumInvocationHistory));
        if (!Enum.IsDefined(typeof(Telemetry.ContextSizePolicy), options.MetricsContextSizePolicy)) throw new ArgumentOutOfRangeException(nameof(options.MetricsContextSizePolicy));
        if (!Enum.IsDefined(typeof(InvocationTelemetryScope), options.InvocationTelemetryScope)) throw new ArgumentOutOfRangeException(nameof(options.InvocationTelemetryScope));
        if (!Enum.IsDefined(typeof(CheckpointBehavior), options.CheckpointBehavior)) throw new ArgumentOutOfRangeException(nameof(options.CheckpointBehavior));
        if (!Enum.IsDefined(typeof(ModelSelectionTiming), options.ModelSelectionTiming)) throw new ArgumentOutOfRangeException(nameof(options.ModelSelectionTiming));
        if (!Enum.IsDefined(typeof(GuideFeedbackDelivery), options.GuideFeedbackDelivery)) throw new ArgumentOutOfRangeException(nameof(options.GuideFeedbackDelivery));
        if (!Enum.IsDefined(typeof(ModelHistoryBehavior), options.ModelHistoryBehavior)) throw new ArgumentOutOfRangeException(nameof(options.ModelHistoryBehavior));
        if (options.ModelHistoryBehavior != ModelHistoryBehavior.Native && (repositoryManager is not null || options.SessionStore is SessionRepositories.RepositoryAgentJournal))
            throw new ArgumentException("Retained-history profiles cannot bind an indexed session journal.", nameof(options));
        if (!Enum.IsDefined(typeof(IdleCancellationBehavior), options.IdleCancellationBehavior)) throw new ArgumentOutOfRangeException(nameof(options.IdleCancellationBehavior));
        if (!Enum.IsDefined(typeof(ExplicitCancellationTiming), options.ExplicitCancellationTiming)) throw new ArgumentOutOfRangeException(nameof(options.ExplicitCancellationTiming));
        if (!Enum.IsDefined(typeof(ToolNameConflictPolicy), options.ToolNameConflictPolicy)) throw new ArgumentOutOfRangeException(nameof(options.ToolNameConflictPolicy));
        if (!Enum.IsDefined(typeof(ToolNameResolution), options.DirectToolNameResolution)) throw new ArgumentOutOfRangeException(nameof(options.DirectToolNameResolution));
        var ownedResources = options.OwnedResources.ToArray();
        var resourceIdentities = new HashSet<IAsyncDisposable>(new ResourceIdentityComparer());
        foreach (var resource in ownedResources)
        {
            Guard.NotNull(resource, nameof(options.OwnedResources));
            if (!resourceIdentities.Add(resource)) throw new ArgumentException("Owned resources must be unique by reference.", nameof(options.OwnedResources));
            if (options.OwnsModel && ReferenceEquals(resource, options.Model)) throw new ArgumentException("An owned model cannot also appear in OwnedResources.", nameof(options.OwnedResources));
        }
        ConversationHistory.Validate(options.Messages);
        if (options.MaxTurns < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxTurns must be positive.");
        if (options.MaxParallelTools < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxParallelTools must be positive.");
        if (!Enum.IsDefined(typeof(ToolExecutionStrategy), options.ToolExecution)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown tool execution strategy.");
        if (!Enum.IsDefined(typeof(InputRedactionTiming), options.InputRedactionTiming)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown input redaction timing.");
        if (options.SessionStore is not null && string.IsNullOrWhiteSpace(options.SessionId))
            throw new ArgumentException("A session store requires a nonempty SessionId.", nameof(options));
        var initialTools = new List<ITool>();
        foreach (var tool in options.Tools)
        {
            Guard.NotNull(tool, nameof(options.Tools));
            if (options.ToolFilter?.Invoke(tool) != false) initialTools.Add(tool);
        }
        _options = options with { StructuredOutput = structuredOutput, ToolProviders = Array.AsReadOnly(providers), Tools = Array.AsReadOnly(initialTools.ToArray()), Messages = options.Messages.ToArray(), Plugins = options.Plugins.ToArray(), OwnedResources = Array.AsReadOnly(ownedResources), Interventions = Array.AsReadOnly(options.Interventions.ToArray()), BackgroundTasks = CopyBackgroundTasks(options.BackgroundTasks), TraceAttributes = CopyTraceAttributes(options.TraceAttributes) };
        _model = options.Model;
        _modelStateful = options.Model is IStatefulModel { Stateful: true };
        _auxModel = options.AuxModel;
        _tracer = new(_options.UsePythonToolErrorStatus, _options.TraceAttributes);
        _meter = new(_options.MaximumInvocationHistory, null, _options.MetricsContextSizePolicy);
        _id = options.Id;
        SystemPromptContent = systemPromptContent;
        SystemPrompt = systemPromptContent is null ? options.SystemPrompt : SessionSnapshot.GetSystemPromptText(systemPromptContent);
        _contextManager = options.ContextManager ?? _options.Plugins.OfType<Context.ContextManager>().SingleOrDefault();
        _plugins = _options.Plugins.Where(plugin => _contextManager is null || plugin is not Conversation.ConversationManager)
            .Concat(options.ContextManager is { } manager ? new IPlugin[] { manager } : [])
            .Concat(options.MemoryManager is { } memory ? new IPlugin[] { memory } : [])
            .Concat(_contextManager is null && options.ConversationManager is Conversation.ConversationManager classic && !_options.Plugins.Any(plugin => ReferenceEquals(plugin, classic)) ? new IPlugin[] { classic } : []).ToArray();
        _memoryManager = options.MemoryManager ?? _options.Plugins.OfType<Memory.MemoryManager>().SingleOrDefault();
        if (IsStateful && (_contextManager is not null || options.ConversationManager is not null || _plugins.OfType<Conversation.ConversationManager>().Any()))
            throw new ArgumentException("ContextManager and ConversationManager cannot be used with a stateful model.", nameof(options));
        _hooks = new(options.Hooks);
        Middleware = new(options.Middleware);
        State = new(options.State);
        ModelState = new(options.ModelState);
        _continuationInputs = new(this);
        _backgroundTasks = _options.BackgroundTasks is { } background ? new(this, background) : null;
        _savedStateVersion = State.Version;
        _savedModelStateVersion = ModelState.Version;
        _toolProviderContext = new(this);
        _toolSnapshot = _options.Tools;
        _applicationTools = _options.Tools.ToList();
        var pluginNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in _plugins)
        {
            Guard.NotNull(plugin, nameof(options.Plugins));
            Guard.NotNullOrWhiteSpace(plugin.Name, nameof(plugin.Name));
            if (!pluginNames.Add(plugin.Name)) throw new ArgumentException($"Duplicate plugin name: {plugin.Name}.", nameof(options));
        }
        _messages = options.Messages.ToList();
        _tools = new(StringComparer.Ordinal);
        foreach (var tool in _options.Tools)
        {
            Guard.NotNull(tool, nameof(tool));
            if (_tools.ContainsKey(tool.Spec.Name))
                throw new ArgumentException($"Duplicate tool name: {tool.Spec.Name}.", nameof(options));
            ValidateNormalizedToolName(tool.Spec.Name, _tools.Keys);
            _tools.Add(tool.Spec.Name, (tool, ToolSchema.Compile(tool.Spec.InputSchema)));
        }
        ValidateDelegationRegistration(_options.Tools);
        _interventions = new(_options.Interventions, Hooks);
        if (repositoryManager is not null)
            _options = _options with { SessionStore = repositoryManager.Bind(this), SessionId = repositoryManager.SessionId };
        if (catalogManager is not null)
            _options = _options with { SessionStore = catalogManager.Bind(this), SessionId = catalogManager.SessionId };
    }

    /// <summary>The model used for the main conversation loop.</summary>
    public IModel Model => Volatile.Read(ref _model);
    /// <summary>Auxiliary model; falls back to the main model.</summary>
    public IModel AuxModel => Volatile.Read(ref _auxModel) ?? Model;
    /// <summary>The stable identity of this agent, including a restored session identity.</summary>
    public string Id => _id;
    /// <summary>Optional shared SDK byte storage. The caller retains backend ownership.</summary>
    public Storage.IStorage? Storage => _options.Storage;
    /// <summary>The selected filesystem for SDK components, inherited by children.</summary>
    public Files.IReadOnlyFileSystem? FileSystem => _options.FileSystem ?? _options.Sandbox;
    /// <summary>The configured context manager, including an explicitly registered manager plugin.</summary>
    public Context.ContextManager? ContextManager => _contextManager;
    /// <summary>The configured memory manager, including an explicitly registered manager plugin.</summary>
    public Memory.MemoryManager? MemoryManager => _memoryManager;
    /// <summary>The agent's display name, used by default when exposing it as a tool.</summary>
    public string Name => _options.Name;
    /// <summary>The model-facing description used when exposing this agent as a tool.</summary>
    public string? Description => _options.Description;
    /// <summary>Instructions sent separately from conversational messages.</summary>
    public string? SystemPrompt { get; private set; }
    /// <summary>The accepted read-only ordered text and cache-point instructions, or null for a legacy string prompt.</summary>
    public IReadOnlyList<ContentBlock>? SystemPromptContent { get; private set; }
    /// <summary>The session key selected at construction, or null when no key was supplied.</summary>
    public string? SessionId => _options.SessionId;
    /// <summary>The typed lifecycle hooks used by this agent.</summary>
    public HookRegistry Hooks => _hooks;
    /// <summary>The current tools, including tools returned by initialized plugins.</summary>
    public IReadOnlyList<ITool> Tools => Volatile.Read(ref _toolSnapshot);
    /// <summary>Application-owned JSON state, excluded from model context and persisted with sessions.</summary>
    public StateStore State { get; }
    /// <summary>The application state store, using the TypeScript SDK's conceptual name.</summary>
    public StateStore AppState => State;
    /// <summary>Agent-owned provider state, such as server conversation identifiers, persisted separately from application state and invocation context.</summary>
    public StateStore ModelState { get; }
    private bool IsStateful => _modelStateful;
    /// <summary>A read-only snapshot of the current conversation. A context manager may reduce it while retaining originals in its stash.</summary>
    public IReadOnlyList<Message> Messages { get { lock (_historyGate) return Array.AsReadOnly(_messages.ToArray()); } }

    /// <summary>Adds an application tool while the agent is inactive.</summary>
    public void AddTool(ITool tool) => AddTools([tool]);

    /// <summary>Validates and registers a complete tool batch atomically. Active invocation or initialization prevents registration.</summary>
    public void AddTools(IReadOnlyList<ITool> tools)
    {
        MutateApplicationTools(tools, ApplicationToolMutation.Add);
    }

    /// <summary>Restores the session and initializes plugins once; subsequent calls share the same success, failure, or admitted cancellation.</summary>
    /// <remarks>
    /// A token canceled before admission does not start or cache an initialization attempt. After admission, cancellation is terminal for this instance; dispose it and create a fresh agent to retry.
    /// Rollback attempts SDK-owned cleanup but does not undo accepted restored state or application callback effects.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(cancellationToken, preserveCheckpointHistory: false);

    private Task InitializeAsync(CancellationToken cancellationToken, bool preserveCheckpointHistory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool>? completion = null;
        Task result;
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_catalogOperation is not null && _initialization is null) throw new InvalidOperationException("A catalog operation owns initialization admission.");
            if (_initialization is { IsCompleted: false })
                for (var scope = ActiveLifecycle.Value; scope is not null; scope = scope.Parent)
                    if (ReferenceEquals(scope.Agent, this)) throw new InvalidOperationException("An initialization callback cannot await its own initialization.");
            if (_initialization is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = completion.Task;
            }
            result = _initialization;
        }
        if (completion is not null) _ = CompleteInitializationAsync(completion, cancellationToken, preserveCheckpointHistory: preserveCheckpointHistory);
        return result;
    }

    /// <summary>Ends the agent's invocation lifecycle and awaits every shutdown callback once. Owned resources remain available until DisposeAsync. Active initialization or invocation prevents shutdown.</summary>
    /// <exception cref="InvalidOperationException">Initialization or invocation is active, or awaiting this lifecycle from a shutdown/disposal callback would form a cycle.</exception>
    public Task ShutdownAsync()
    {
        ThrowIfLifecycleReentrant();
        return StartShutdownAsync();
    }

    private Task StartShutdownAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_initializationGate)
        {
            if (_shutdown is not null) return _shutdown;
            if (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _classicReductions != 0 || _catalogOperation is not null || _initialization is { IsCompleted: false })
                throw new InvalidOperationException("Finish initialization and dispose active invocations before shutting down the agent.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = completion.Task;
            _providerUpdatesStopping = true;
        }
        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource<bool> completion)
    {
        var parent = ActiveLifecycle.Value;
        ActiveLifecycle.Value = new(this, parent);
        try
        {
            Exception? failure = null;
            try { await Hooks.InvokeAsync(new ShutdownEvent(this), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { await ReleaseToolProviderConsumersAsync(failure).ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
            try { await _continuationInputs.AbandonDeferredAsync(new ObjectDisposedException(nameof(Agent), "Shutdown prevents appending deferred continuation input.")).ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
            if (failure is null) completion.TrySetResult(true);
            else completion.TrySetException(failure);
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { ActiveLifecycle.Value = parent; }
    }

    /// <summary>Awaits terminal shutdown, then releases owned resources in reverse order, the explicitly owned model, and agent-owned hooks. Every cleanup is attempted even when shutdown fails.</summary>
    /// <exception cref="InvalidOperationException">Initialization or invocation is active, or awaiting this lifecycle from a shutdown/disposal callback would form a cycle.</exception>
    public ValueTask DisposeAsync()
    {
        ThrowIfLifecycleReentrant();
        TaskCompletionSource<bool> completion;
        lock (_initializationGate)
        {
            if (_disposal is not null) return new(_disposal);
            if (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _classicReductions != 0 || _catalogOperation is not null || _initialization is { IsCompleted: false })
                throw new InvalidOperationException("Finish initialization and dispose active invocations before disposing the agent.");
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _ = CompleteDisposalAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion)
    {
        var parent = ActiveLifecycle.Value;
        ActiveLifecycle.Value = new(this, parent);
        Exception? failure = null;
        try { await StartShutdownAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try
        {
            for (var index = _options.OwnedResources.Count - 1; index >= 0; index--)
            {
                try { await _options.OwnedResources[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { if (failure is null) failure = error; else AttachCleanupError(failure, error); }
            }
            if (_options.OwnsModel)
            {
                if (_options.Model is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
                else if (_options.Model is IDisposable disposable) disposable.Dispose();
            }
        }
        catch (Exception error) { if (failure is null) failure = error; else AttachCleanupError(failure, error); }
        finally
        {
            try { _interventions.Dispose(); Hooks.ClearOwned(); Middleware.ClearOwned(); }
            finally
            {
                try { ReleaseRepositoryBinding(); }
                finally
                {
                    try { ReleaseSnapshotSessionBinding(); }
                    finally { ActiveLifecycle.Value = parent; }
                }
            }
        }
        if (failure is null) completion.TrySetResult(true);
        else completion.TrySetException(failure);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _shutdown is not null) throw new ObjectDisposedException(nameof(Agent));
    }

    private void ThrowIfLifecycleReentrant()
    {
        RefuseProviderUpdateLifecycleReentry();
        for (var scope = ActiveLifecycle.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Agent, this))
                throw new InvalidOperationException("An agent lifecycle callback cannot await its own or an ancestor agent's shutdown or disposal.");
    }

    private sealed record LifecycleScope(Agent Agent, LifecycleScope? Parent);

    private sealed class ResourceIdentityComparer : IEqualityComparer<IAsyncDisposable>
    {
        public bool Equals(IAsyncDisposable? x, IAsyncDisposable? y) => ReferenceEquals(x, y);
        public int GetHashCode(IAsyncDisposable value) => RuntimeHelpers.GetHashCode(value);
    }

    private async Task CompleteInitializationAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken, PreparedSessionRestore? selectedSession = null, bool preserveCheckpointHistory = false)
    {
        var parent = ActiveLifecycle.Value;
        ActiveLifecycle.Value = new(this, parent);
        var observed = new List<IAgentObservationLifetime>();
        try
        {
            var restoredSession = await RestoreSessionAsync(cancellationToken, selectedSession).ConfigureAwait(false);
            await InitializeToolProvidersAsync(cancellationToken).ConfigureAwait(false);
            InitializeSandboxTools(cancellationToken);
            // Reverse after-hook order puts Python clearing after classic management.
            if (_options.ModelHistoryBehavior == ModelHistoryBehavior.Python) Hooks.Register<AfterInvocationEvent>(ClearRetainedModelHistory);
            foreach (var plugin in _plugins)
            {
                await plugin.InitializeAsync(this, cancellationToken).ConfigureAwait(false);
                var tools = plugin.GetTools() ?? throw new InvalidOperationException($"Plugin {plugin.Name} returned null tools.");
                var pending = new Dictionary<string, (ITool Tool, JsonSchema Schema)>(StringComparer.Ordinal);
                foreach (var tool in tools)
                {
                    Guard.NotNull(tool, nameof(tools));
                    if (_options.ToolFilter?.Invoke(tool) == false) continue;
                    if (_tools.ContainsKey(tool.Spec.Name) || pending.ContainsKey(tool.Spec.Name)) throw new InvalidOperationException($"Duplicate tool name: {tool.Spec.Name}.");
                    ValidateNormalizedToolName(tool.Spec.Name, _tools.Keys.Concat(pending.Keys), initialization: true);
                    pending.Add(tool.Spec.Name, (tool, ToolSchema.Compile(tool.Spec.InputSchema)));
                }
                foreach (var pair in pending) _tools.Add(pair.Key, pair.Value);
                if (PluginFactory.IsAttributed(plugin)) _attributedPluginTools.AddRange(pending.Keys);
                Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(_tools.Values.Select(pair => pair.Tool).ToArray()));
            }
            // TypeScript's model plugin unwinds before earlier completion registrations.
            if (_options.ModelHistoryBehavior == ModelHistoryBehavior.TypeScript) Hooks.Register<AfterInvocationEvent>(ClearRetainedModelHistory);
            if (_options.InitializationTools is { } createTools)
            {
                var generated = createTools(this) ?? throw new InvalidOperationException("The initialization tool factory returned null.");
                var pending = new Dictionary<string, (ITool Tool, JsonSchema Schema)>(StringComparer.Ordinal);
                foreach (var tool in generated)
                {
                    Guard.NotNull(tool, nameof(generated));
                    if (_options.ToolFilter?.Invoke(tool) == false) continue;
                    if (_tools.ContainsKey(tool.Spec.Name) || pending.ContainsKey(tool.Spec.Name)) throw new InvalidOperationException($"Duplicate tool name: {tool.Spec.Name}.");
                    ValidateNormalizedToolName(tool.Spec.Name, _tools.Keys.Concat(pending.Keys), initialization: true);
                    pending.Add(tool.Spec.Name, (tool, ToolSchema.Compile(tool.Spec.InputSchema)));
                }
                foreach (var pair in pending) _tools.Add(pair.Key, pair.Value);
                _applicationTools.AddRange(pending.Values.Select(value => value.Tool));
                Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(_tools.Values.Select(value => value.Tool).ToArray()));
            }
            ValidateDelegationRegistration(Tools);
            _backgroundTasks?.Initialize();
            foreach (var observer in Interventions.OfType<IAgentLifecycleObserver>())
            {
                if (observer is IAgentObservationLifetime lifetime) observed.Add(lifetime);
                await observer.ObserveAgentAsync(this, cancellationToken).ConfigureAwait(false);
            }
            // Source profiles discard automatic restored history after plugins select the initialized model.
            if (restoredSession &&
                (_options.ModelHistoryBehavior is ModelHistoryBehavior.TypeScript or ModelHistoryBehavior.Python) &&
                Model is IStatefulModel { Stateful: true })
                lock (_historyGate)
                {
                    // Imported native pairs and explicit checkpoint replays retain committed history.
                    if (_interruptState is not { AssistantMessageIsIndependent: false, SourceHistoryOwned: false } && !preserveCheckpointHistory)
                    {
                        var changed = _messages.Count != 0;
                        if (_interruptState is { AssistantMessageIsIndependent: false, SourceHistoryOwned: true } pending)
                        {
                            _interruptState = pending with { AssistantMessageIsIndependent = true };
                            changed = true;
                        }
                        _messages.Clear();
                        if (changed) _restoredSourceHistoryDirty = true;
                    }
                }
            await Hooks.InvokeAsync(new InitializedEvent(this), cancellationToken).ConfigureAwait(false);
            if (_options.EnableToolProviderUpdates) CompleteProviderUpdatesInitialization(completion);
            else completion.TrySetResult(true);
        }
        catch (Exception error)
        {
            try { RollbackAttributedPlugins(); }
            catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            var hadProviderConsumers = _providerConsumers.Count > 0;
            try { RollbackSandboxTools(); }
            catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            RollbackToolProviderPublications();
            try { await ReleaseToolProviderConsumersAsync(error).ConfigureAwait(false); }
            catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            for (var index = observed.Count - 1; index >= 0; index--)
                try { await observed[index].RollbackObservationAsync(this).ConfigureAwait(false); }
                catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            try { ReleaseRepositoryBinding(); }
            catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            try { ReleaseSnapshotSessionBinding(); }
            catch (Exception cleanup) { AttachCleanupError(error, cleanup); }
            // A canceled TaskCompletionSource cannot retain the original exception's rollback diagnostics.
            if (!hadProviderConsumers && error is OperationCanceledException canceled && error.Data["Strands.CleanupErrors"] is not List<Exception> { Count: > 0 }) completion.TrySetCanceled(canceled.CancellationToken);
            else completion.TrySetException(error);
        }
        finally { ActiveLifecycle.Value = parent; }
    }

    /// <summary>Runs with a temporary structured response tool and returns the accepted typed result.</summary>
    /// <remarks>Model arguments are validated before tool hooks. In the TypeScript profile, changed final hook JSON is decoded to T without repeating custom validation.</remarks>
    public async Task<StructuredOutputResult<T>> InvokeStructuredAsync<T>(string? prompt, StructuredOutputOptions<T> options,
        InvocationOptions? invocationOptions = null, CancellationToken cancellationToken = default)
    {
        var tool = new StructuredOutputTool<T>(options);
        var result = await InvokeAsync(prompt, (invocationOptions ?? new()) with
        {
            StructuredOutputTool = tool,
            StructuredOutputForcePrompt = tool.ForcePrompt,
            StructuredOutputBehavior = options.Behavior,
            StructuredOutputValidationLimit = options.MaxValidationAttempts
        }, cancellationToken).ConfigureAwait(false);
        if (result.StopReason == StopReasons.Cancelled && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (result.StopReason != StopReasons.ToolUse)
            throw new StructuredOutputException($"The invocation ended with {result.StopReason} before completing structured output.") { Result = result };
        if (result.StructuredOutputToolUseId is { } toolUseId && tool.TryGetResult(toolUseId, out var output))
            return new(output!, result);
        if (result.AcceptedIdempotentStructuredValue is { } accepted && accepted.ToolUseId == result.StructuredOutputToolUseId)
        {
            if (accepted.Value is T typed) return new(typed, result);
            if (accepted.Value is null && default(T) is null) return new(default!, result);
            throw new StructuredOutputException($"The shared structured value is incompatible with {typeof(T).FullName}.");
        }
        throw new StructuredOutputException($"The invocation ended with {result.StopReason} before producing a valid structured response.");
    }

    /// <summary>Runs the agent to a terminal result. A null prompt continues existing history; caller cancellation produces a cancelled result.</summary>
    public async Task<AgentResult> InvokeAsync(string? prompt, InvocationOptions? options = null, CancellationToken cancellationToken = default)
    {
        AgentResult? result = null;
        await foreach (var item in StreamAsync(prompt, options, cancellationToken).ConfigureAwait(false))
            if (item is AgentResultEvent completed) result = completed.Result;
        return result ?? throw new InvalidOperationException("The agent stream did not produce a result.");
    }

    /// <summary>Streams model events and committed messages. Disposing the enumerator releases the invocation and repairs pending tool requests.</summary>
    private async IAsyncEnumerable<IAgentStreamEvent> StreamInvocationCoreAsync(string? prompt, InvocationOptions options,
        InvocationIdempotencyReceipt? idempotencyReceipt, Action? admittedProviderReport,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Action? providerUpdateReport;
        if (idempotencyReceipt is null)
        {
            options = CopyEphemeralTools(CopyInterruptResponses(options ?? new()));
            ValidateInvocationOptions(options);
            lock (_initializationGate)
            {
                ThrowIfDisposed();
                if (_inflightIdempotency is not null) throw new ConcurrentInvocationException();
                if (_directToolCalls != 0 || _classicReductions != 0 || _catalogOperation is not null) throw new ConcurrentInvocationException();
                providerUpdateReport = PublishPendingProviderUpdatesUnderGate();
                if (Interlocked.CompareExchange(ref _invoking, 1, 0) != 0) throw new ConcurrentInvocationException();
                BeginGoalInvocationIdentity();
                BeginCancellationAdmission();
            }
        }
        else
        {
            // Preparation and admission were performed atomically by the outer token coordinator.
            providerUpdateReport = admittedProviderReport;
        }
        providerUpdateReport?.Invoke();
        try { if (_restored || _options.SessionStore is null) ValidateInterruptResume(prompt, _backgroundTasks?.ForegroundOptions(options, prompt, false) ?? options); }
        catch
        {
            Action? report;
            lock (_initializationGate) { EndCancellationAdmission(); Volatile.Write(ref _invoking, 0); report = PublishPendingProviderUpdatesUnderGate(); }
            report?.Invoke();
            throw;
        }
        var progress = new InvocationProgress(options.InvocationState);
        Exception? telemetryError = null;
        AfterInvocationEvent? continuationBoundary = null;
        IReadOnlyList<Message>? automaticInputs = null;
        try
        {
            if (_options.InvocationTelemetryScope == InvocationTelemetryScope.PublicInvocation) BeginInvocationTelemetry();
            while (true)
            {
                using var invocationCancellation = OpenInvocationCancellation(cancellationToken);
                if (_options.InvocationTelemetryScope == InvocationTelemetryScope.AgentPass) _memoryInvocationTraceContext = null;
                Exception? error = null;
                AgentResult? result = null;
                IAsyncEnumerator<IAgentStreamEvent>? iterator = null;
                AfterInvocationEvent? after = null;
                try
                {
                    iterator = InvokeCoreAsync(prompt, options, progress, invocationCancellation, invocationCancellation.Token, continuationBoundary, automaticInputs).GetAsyncEnumerator(invocationCancellation.Token);
                    while (true)
                    {
                        var next = false;
                        try { next = await iterator.MoveNextAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException cancellation) when ((cancellationToken.IsCancellationRequested || invocationCancellation.Token.IsCancellationRequested) &&
                            cancellation.Data["Strands.CleanupErrors"] is not List<Exception> { Count: > 0 })
                        { result = progress.Result(StopReasons.Cancelled); break; }
                        catch (Exception failure) { error = telemetryError = failure; break; }
                        if (!next) break;
                        if (iterator.Current is AgentResultEvent completed) result = completed.Result;
                        else yield return iterator.Current;
                    }
                    Message? cancelled = null;
                    if (result?.StopReason == StopReasons.Cancelled && error is null)
                        try { cancelled = await CommitCancellationMessageAsync(invocationCancellation, progress).ConfigureAwait(false); }
                        catch (Exception failure) { error = telemetryError = failure; }
                    if (cancelled is not null)
                    {
                        result = progress.Result(StopReasons.Cancelled);
                        yield return new MessageAddedEvent(this, cancelled) { InvocationState = options.InvocationState };
                    }
                }
                finally
                {
                    Exception? disposalFailure = null;
                    try { if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup)
                    {
                        if (error is null) error = telemetryError = disposalFailure = cleanup;
                        else if (!ReferenceEquals(error, cleanup)) AttachCleanupError(error, cleanup);
                    }
                    if (result is null && error is null && invocationCancellation.Token.IsCancellationRequested)
                    {
                        result = progress.Result(StopReasons.Cancelled);
                        try
                        {
                            await CommitCancellationMessageAsync(invocationCancellation, progress).ConfigureAwait(false);
                            result = progress.Result(StopReasons.Cancelled);
                        }
                        catch (Exception cleanup) { error = telemetryError = disposalFailure = cleanup; }
                    }
                    if (continuationBoundary is not null)
                        await _continuationInputs.AbandonAsync(continuationBoundary, error ?? new InvalidOperationException("Continuation input was not appended.")).ConfigureAwait(false);
                    if (result is not null) result = CaptureResultTelemetry(result, error);
                    else CompleteCycleTelemetry(error);
                    if (_options.InvocationTelemetryScope == InvocationTelemetryScope.AgentPass)
                        CompleteInvocationTelemetry(progress, error, result?.StopReason);
                    try
                    {
                        after = await FinishInvocationAsync(result, error, options.InvocationState, invocationCancellation.Token, !progress.Denied && progress.Checkpoint is null,
                        error is null && result?.Checkpoint is { } checkpoint && ReferenceEquals(checkpoint, progress.Checkpoint)).ConfigureAwait(false);
                    }
                    catch (Exception failure) { telemetryError ??= failure; throw; }
                    continuationBoundary = after;
                    if (disposalFailure is not null) ExceptionDispatchInfo.Capture(disposalFailure).Throw();
                }
                // Yield outside cleanup: disposing at this boundary must never start a hidden continuation.
                yield return after;
                if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
                if (result is null) throw new InvalidOperationException("The agent invocation cycle did not produce a result.");
                if (after.AutomaticWaitCancelled) result = CaptureResultTelemetry(progress.Result(StopReasons.Cancelled));
                var automatic = await _continuationInputs.PrepareAsync(after, result.StopReason).ConfigureAwait(false);
                if ((after.Resume is not null || automatic is not null) && result.Checkpoint is null && !progress.Denied && result.StopReason != StopReasons.Cancelled && !cancellationToken.IsCancellationRequested && !invocationCancellation.Token.IsCancellationRequested)
                {
                    var resetLimits = after.Resume is not null && !after.ResumeIsAutomatic && options.ResetLimitsOnHookResume;
                    if (!resetLimits && result.StopReason != StopReasons.Interrupt && GetLimit(options, progress.Turns, progress.Usage) is { } limit)
                    {
                        CompleteInvocationTelemetry(progress, null, limit);
                        yield return await ObserveFinalResultAsync(CaptureResultTelemetry(progress.Result(limit))).ConfigureAwait(false);
                        yield break;
                    }
                    automaticInputs = automatic;
                    options = options with { CheckpointResume = null, InputMessage = null, InputMessages = after.Resume?.Messages ?? Array.Empty<Message>(), InterruptResponses = after.Resume?.InterruptResponses ?? Array.Empty<InterruptResponse>() };
                    prompt = null;
                    if (resetLimits) progress = new(options.InvocationState);
                    else progress.ResetCycle();
                    continue;
                }
                CompleteInvocationTelemetry(progress, null, result.StopReason);
                yield return await ObserveFinalResultAsync(result).ConfigureAwait(false);
                yield break;
            }
        }
        finally
        {
            try
            {
                if (continuationBoundary is not null)
                    await _continuationInputs.AbandonAsync(continuationBoundary, new OperationCanceledException("The invocation ended before appending continuation input.")).ConfigureAwait(false);
            }
            finally
            {
                try { CompleteInvocationTelemetry(progress, telemetryError); }
                finally
                {
                    _memoryInvocationTraceContext = null;
                    Action? report;
                    lock (_initializationGate) { EndCancellationAdmission(); Volatile.Write(ref _invoking, 0); report = PublishPendingProviderUpdatesUnderGate(); }
                    report?.Invoke();
                }
            }
        }

        async ValueTask<AgentResultEvent> ObserveFinalResultAsync(AgentResult result)
        {
            result = BindIdempotentStructuredResult(result, options, idempotencyReceipt);
            var value = new AgentResultEvent(this, result);
            try { await Hooks.InvokeAsync(value, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) { telemetryError ??= failure; throw; }
            return value;
        }
    }

    /// <summary>Exposes this agent's configuration as a tool. Every call creates a fresh conversation without a session. A cancelled child fails the tool call. A derived name replaces unsupported characters with underscores and is limited to 64 characters; explicit names are validated unchanged.</summary>
    public ITool AsTool(string? name = null, string? description = null)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { task = new { type = "string" } },
            required = new[] { "task" },
            additionalProperties = false
        });
        var toolName = name ?? new string(Name.Take(64).Select(value => Guard.IsAsciiLetterOrDigit(value) || value is '_' or '-' ? value : '_').ToArray());
        return new FunctionTool(new ToolSpec(toolName, description ?? Description ?? $"Delegate a task to {Name}.", schema), async (input, context, ct) =>
        {
            var child = CreateChild();
            await using var childScope = child.ConfigureAwait(false);
            var result = await child.InvokeAsync(input.GetProperty("task").GetString()!, new() { InvocationState = context.InvocationState }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.StopReason == StopReasons.Cancelled) throw new OperationCanceledException($"Agent '{toolName}' cancelled.", ct);
            return result.ToString();
        });
    }

    /// <summary>Creates a fresh child conversation, copying application state and inheriting construction policies.</summary>
    /// <param name="configure">An optional transformation of immutable child options. The default disables sessions, starts without messages, and borrows shared model/resources; explicit child-owned resources may be supplied.</param>
    /// <returns>An independent conversation with agent-owned plugin hooks and shared application dependencies.</returns>
    public Agent CreateChild(Func<AgentOptions, AgentOptions>? configure = null)
    {
        AgentOptions options;
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            options = _options with
            {
                Id = Guid.NewGuid().ToString(),
                Model = _model,
                SystemPrompt = SystemPromptContent is null ? SystemPrompt : null,
                SystemPromptContent = SystemPromptContent,
                Messages = [],
                State = CopyChildApplicationState(),
                ModelState = new Dictionary<string, JsonElement>(),
                Tools = GetChildApplicationTools(),
                ToolProviders = _options.ToolProviders,
                AuxModel = _auxModel,
                SessionStore = null,
                SessionId = null,
                OwnsModel = false,
                OwnedResources = [],
                TraceAttributes = CopyTraceAttributes(_options.TraceAttributes),
                InitializationTools = null,
                ContextManager = _options.ContextManager?.CreateForAgent(),
                MemoryManager = _options.MemoryManager?.CreateForAgent(),
                ConversationManager = _options.ConversationManager is Conversation.ConversationManager classic ? classic.CreateForAgent() : _options.ConversationManager,
                Plugins = _options.Plugins.Where(plugin => plugin is not SessionRepositories.RepositorySessionManager && plugin is not SnapshotSessionManager && (plugin is not Conversation.ConversationManager || !ReferenceEquals(plugin, _options.ConversationManager))).Select(plugin => plugin switch
                {
                    Context.ContextManager context => (IPlugin)context.CreateForAgent(),
                    Memory.MemoryManager memory => memory.CreateForAgent(),
                    Conversation.ConversationManager conversation => conversation.CreateForAgent(),
                    _ => plugin
                }).ToArray()
            };
        }
        if (configure is not null) options = configure(options) ?? throw new ArgumentException("Child configuration cannot return null.", nameof(configure));
        return new(options);
    }

    private async IAsyncEnumerable<IAgentStreamEvent> InvokeCoreAsync(string? prompt, InvocationOptions options, InvocationProgress progress, InvocationCancellationScope invocationCancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken, AfterInvocationEvent? continuationBoundary = null, IReadOnlyList<Message>? automaticInputs = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preserveCheckpointHistory = options.CheckpointResume is not null && _options.CheckpointBehavior == CheckpointBehavior.PythonReplay;
        await InitializeAsync(invocationCancellation.Caller, preserveCheckpointHistory).ConfigureAwait(false);
        invocationCancellation.Enable();
        var checkpointReplay = PrepareCheckpointReplay(options);
        progress.CheckpointCycleIndex = options.CheckpointResume is { } resumed
            ? resumed.Position == CheckpointPosition.AfterTools ? checked(resumed.CycleIndex + 1) : resumed.CycleIndex : 0;
        progress.CheckpointResumePosition = options.CheckpointResume?.Position;
        var combinedOptions = IncludeAutomaticInvocationInput(options, automaticInputs);
        if (_options.InvocationTelemetryScope == InvocationTelemetryScope.PublicInvocation) RecordInvocationInputTelemetry(prompt, combinedOptions);
        progress.StructuredTool = options.StructuredOutputTool;
        progress.StructuredBehavior = options.StructuredOutputBehavior;
        progress.ExposeStructuredOutput = options.ExposeStructuredOutput;
        if (progress.StructuredTool is { } structured && _tools.ContainsKey(structured.Spec.Name))
            throw new InvalidOperationException($"The structured output tool name {structured.Spec.Name} is reserved during a structured invocation.");
        if (progress.StructuredTool is { } structuredName) ValidateNormalizedToolName(structuredName.Spec.Name, _tools.Keys);
        progress.EphemeralTools = BindEphemeralTools(options);
        ValidateDelegationRegistration(progress.EphemeralTools.Values);
        var foregroundOptions = _backgroundTasks?.ForegroundOptions(combinedOptions, prompt, false) ?? combinedOptions;
        ValidateInterruptResume(prompt, foregroundOptions);
        lock (_historyGate) ConversationHistory.Validate(_messages.Concat(combinedOptions.InputMessages).ToArray(), _messages.Count == 0 ? ModelHistoryBehavior.Native : _options.ModelHistoryBehavior);
        await RetryRepositoryWritesAsync(CancellationPreparationToken(cancellationToken)).ConfigureAwait(false);
        var manualMessages = options.InputMessages;
        options = (_backgroundTasks?.ForegroundOptions(combinedOptions, prompt, true) ?? combinedOptions) with { InputMessages = manualMessages };
        await AcceptInterruptResponsesAsync(options).ConfigureAwait(false);
        if (checkpointReplay is null) await RepairPendingToolsAsync(options.InvocationState).ConfigureAwait(false);
        var originalInput = SnapshotManualInvocationInput(prompt, options);
        var beforeInvocation = new BeforeInvocationEvent(this, options.InvocationState) { Messages = originalInput };
        await Hooks.InvokeAsync(beforeInvocation, invocationCancellation.Caller).ConfigureAwait(false);
        yield return beforeInvocation;
        CheckInvocationCancellation(cancellationToken, NativeCancellationCheckpoints);
        if (beforeInvocation.CancelReason is { } denied)
        {
            progress.Denied = true;
            var denial = new Message(Role.Assistant, [new TextBlock(denied)]);
            progress.Message = denial;
            // A paused assistant already has pending tool traffic; a policy denial must not orphan its durable batch.
            if (InterruptState is null)
            {
                if (checkpointReplay is not null) await RepairPendingToolsAsync(options.InvocationState).ConfigureAwait(false);
                await AddMessageAsync(denial, options.InvocationState, cancellationToken).ConfigureAwait(false);
                yield return new MessageAddedEvent(this, denial) { InvocationState = options.InvocationState };
            }
            yield return new AgentResultEvent(progress.Result(StopReasons.EndTurn));
            yield break;
        }
        if (_options.ExplicitCancellationTiming == ExplicitCancellationTiming.BeforeInputAndModelOrTool)
            cancellationToken.ThrowIfCancellationRequested();
        var acceptedInput = PrepareInvocationInput(beforeInvocation.Messages ?? originalInput, automaticInputs, options);
        if (_options.InvocationTelemetryScope == InvocationTelemetryScope.AgentPass)
        {
            BeginInvocationTelemetry();
            RecordInvocationInputTelemetry(prompt, combinedOptions);
        }
        ResolveInvocationModel(progress, Model);
        var interruptedMessage = InterruptState?.AssistantMessage;
        progress.ResumeMessage = interruptedMessage ?? checkpointReplay;
        if (interruptedMessage is not null) progress.CheckpointResumePosition = CheckpointPosition.AfterModel;
        if (progress.ResumeMessage is not null) progress.Message = progress.ResumeMessage;
        if (acceptedInput.Count > 0)
        {
            await AddInputMessagesAsync(acceptedInput, options.InvocationState, cancellationToken, continuationBoundary is null ? null : () => _continuationInputs.MarkAppendedAsync(continuationBoundary)).ConfigureAwait(false);
            foreach (var input in acceptedInput) yield return new MessageAddedEvent(this, input) { InvocationState = options.InvocationState };
            await RepairPendingToolsAsync(options.InvocationState).ConfigureAwait(false);
        }
        while (true)
        {
            CheckInvocationCancellation(cancellationToken, BeforeInputCancellationCheckpoint);
            if (_backgroundTasks is { } background)
            {
                var questions = background.Questions();
                if (questions.Count > 0)
                {
                    progress.Interrupts = questions;
                    var interrupted = new InterruptEvent(this, questions, options.InvocationState);
                    await Hooks.InvokeAsync(interrupted, invocationCancellation.Caller).ConfigureAwait(false);
                    yield return interrupted;
                    yield return new AgentResultEvent(progress.Result(StopReasons.Interrupt));
                    yield break;
                }
            }
            var message = progress.ResumeMessage;
            progress.ResumeMessage = null;
            if (message is null)
            {
                if (GetLimit(options, progress.Turns, progress.Usage) is { } reached)
                {
                    yield return new AgentResultEvent(progress.Result(reached));
                    yield break;
                }
                BeginCycleTelemetry();
                var turn = new ModelTurn();
                await foreach (var item in StreamModelAsync(turn, progress, cancellationToken).ConfigureAwait(false)) yield return item;
                if (turn.BackgroundInterrupts is { } questions)
                {
                    progress.Interrupts = questions;
                    var interrupted = new InterruptEvent(this, questions, options.InvocationState);
                    await Hooks.InvokeAsync(interrupted, invocationCancellation.Caller).ConfigureAwait(false);
                    yield return interrupted;
                    yield return new AgentResultEvent(progress.Result(StopReasons.Interrupt));
                    yield break;
                }
                progress.Usage += turn.Usage;
                if (turn.Error is { } modelError)
                {
                    if (turn.Retry) continue;
                    ExceptionDispatchInfo.Capture(modelError).Throw();
                }
                if (turn.Retry) continue;
                message = turn.Message!;
                var modelState = turn.ModelState;
                if (turn.ProviderStateful && turn.ProviderCompleted && modelState is null) throw new ModelException("A stateful model must complete its response with provider state.");
                var discardStructuredResponse = progress.StructuredTool is not null &&
                    progress.StructuredBehavior == StructuredOutputBehavior.TypeScript && turn.StopReason != StopReasons.ToolUse;
                if (discardStructuredResponse && turn.StopReason == StopReasons.MaxTokens)
                {
                    invocationCancellation.Caller.ThrowIfCancellationRequested();
                    throw new StructuredOutputException("The model reached max_tokens before completing structured output.");
                }
                progress.Turns++;
                if (!discardStructuredResponse) progress.Message = message;
                await ApplyInputRedactionAsync(turn.InputRedaction, AcceptedWriteToken(cancellationToken)).ConfigureAwait(false);
                if (discardStructuredResponse)
                {
                    await AcceptDiscardedModelResponseAsync(turn, cancellationToken).ConfigureAwait(false);
                    progress.Message = Messages.LastOrDefault();
                    CheckInvocationCancellation(cancellationToken, CheckCompletedModelCancellation(turn));
                    if (progress.ForceStructured) throw new StructuredOutputException("The model did not invoke the required structured response tool.");
                    progress.ForceStructured = true;
                    continue;
                }
                if (turn.StopReason == StopReasons.ToolUse && _options.CheckpointBehavior == CheckpointBehavior.TypeScriptRegenerate &&
                    progress.CheckpointResumePosition != CheckpointPosition.AfterModel)
                {
                    CheckInvocationCancellation(cancellationToken, CheckCompletedModelCancellation(turn));
                    await AcceptDiscardedModelResponseAsync(turn, cancellationToken).ConfigureAwait(false);
                    CheckInvocationCancellation(cancellationToken, CheckCompletedModelCancellation(turn));
                    yield return new AgentResultEvent(PauseAtCheckpoint(progress, CheckpointPosition.AfterModel));
                    yield break;
                }
                await AddMessageAsync(message, options.InvocationState, cancellationToken, modelState, UsesNativeStatefulHistory && turn.ProviderCompleted).ConfigureAwait(false);
                yield return new MessageAddedEvent(this, message) { InvocationState = options.InvocationState };
                CheckInvocationCancellation(cancellationToken, CheckCompletedModelCancellation(turn));
                if (progress.StructuredTool is not null && progress.StructuredBehavior == StructuredOutputBehavior.Python && turn.StopReason == StopReasons.MaxTokens)
                    throw new StructuredOutputException("The model reached max_tokens before completing structured output.");
                if (turn.StopReason != StopReasons.ToolUse)
                {
                    var unexecuted = message.Content.OfType<ToolUseBlock>()
                        .Select(call => (ContentBlock)new ToolResultBlock(call.ToolUseId, $"Tool execution was skipped because the model stopped with {turn.StopReason}.", ToolResultStatus.Error)).ToArray();
                    if (unexecuted.Length > 0)
                    {
                        var skipped = new Message(Role.User, unexecuted);
                        await AddMessageAsync(skipped, options.InvocationState, CancellationToken.None).ConfigureAwait(false);
                        yield return new MessageAddedEvent(this, skipped) { InvocationState = options.InvocationState };
                    }
                    if (progress.StructuredTool is not null && turn.StopReason == StopReasons.EndTurn)
                    {
                        if (progress.ForceStructured) throw new StructuredOutputException("The model did not invoke the required structured response tool.");
                        if (progress.StructuredBehavior != StructuredOutputBehavior.Python)
                        {
                            var forceLimit = GetLimit(options, progress.Turns, progress.Usage);
                            if (forceLimit is not null) { yield return new AgentResultEvent(progress.Result(forceLimit)); yield break; }
                        }
                        progress.ForceStructured = true;
                        var force = Message.User(options.StructuredOutputForcePrompt!);
                        if (progress.StructuredBehavior == StructuredOutputBehavior.Python) progress.Message = force;
                        await AddMessageAsync(force, options.InvocationState, cancellationToken).ConfigureAwait(false);
                        yield return new MessageAddedEvent(this, force) { InvocationState = options.InvocationState };
                        continue;
                    }
                    yield return new AgentResultEvent(progress.Result(turn.StopReason!));
                    yield break;
                }
            }
            if (_options.CheckpointBehavior != CheckpointBehavior.Disabled && progress.CheckpointResumePosition != CheckpointPosition.AfterModel)
            {
                yield return new AgentResultEvent(PauseAtCheckpoint(progress, CheckpointPosition.AfterModel));
                yield break;
            }
            progress.CheckpointResumePosition = null;
            if (_metricCycle is null) BeginCycleTelemetry();
            var calls = message.Content.OfType<ToolUseBlock>().ToArray();
            var execution = new ToolBatchExecution();
            await foreach (var update in StreamInvocationToolsAsync(calls, options.InvocationState, progress, execution, invocationCancellation, cancellationToken).ConfigureAwait(false)) yield return update;
            var batch = execution.Batch!;
            if (execution.ReinsertedAssistant is not null) yield return new MessageAddedEvent(this, execution.ReinsertedAssistant) { InvocationState = options.InvocationState };
            if (execution.Message is not null) yield return new MessageAddedEvent(this, execution.Message) { InvocationState = options.InvocationState };
            if (batch.Error is { } pipelineError) ExceptionDispatchInfo.Capture(pipelineError).Throw();
            invocationCancellation.Caller.ThrowIfCancellationRequested();
            if (batch.Interrupted)
            {
                CheckInvocationCancellation(cancellationToken, NativeCancellationCheckpoints || batch.PausedByCancellation);
                progress.Interrupts = UnansweredInterrupts();
                var interrupted = new InterruptEvent(this, progress.Interrupts, options.InvocationState);
                await Hooks.InvokeAsync(interrupted, invocationCancellation.Caller).ConfigureAwait(false);
                yield return interrupted;
                yield return new AgentResultEvent(progress.Result(StopReasons.Interrupt));
                yield break;
            }
            if (batch.EndTurn is { } delegated)
            {
                progress.Message = delegated;
                await AddMessageAsync(delegated, options.InvocationState, CancellationToken.None).ConfigureAwait(false);
                yield return new MessageAddedEvent(this, delegated) { InvocationState = options.InvocationState };
                yield return new AgentResultEvent(progress.Result(StopReasons.EndTurn));
                yield break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (invocationCancellation.Deferred)
            {
                if (await CommitCancellationMessageAsync(invocationCancellation, progress).ConfigureAwait(false) is { } cancelled)
                    yield return new MessageAddedEvent(this, cancelled) { InvocationState = options.InvocationState };
                yield return new AgentResultEvent(progress.Result(StopReasons.Cancelled));
                yield break;
            }
            if (progress.StructuredTool is { } structuredOutput)
            {
                var acceptedId = SelectAcceptedStructuredOutput(structuredOutput, progress.StructuredBehavior, calls, batch, cancellationToken);
                if (acceptedId is not null)
                {
                    progress.StructuredOutputToolUseId = acceptedId;
                    yield return new AgentResultEvent(progress.Result(StopReasons.ToolUse));
                    yield break;
                }
            }
            if (progress.StructuredTool is not null)
            {
                progress.StructuredAttempts += calls.Count(call => call.Name == progress.StructuredTool.Spec.Name);
                if (progress.StructuredAttempts >= options.StructuredOutputValidationLimit)
                    throw new StructuredOutputException("The structured output validation attempt limit was reached.");
            }
            if (_options.CheckpointBehavior != CheckpointBehavior.Disabled)
            {
                yield return new AgentResultEvent(PauseAtCheckpoint(progress, CheckpointPosition.AfterTools));
                yield break;
            }
            var limit = GetLimit(options, progress.Turns, progress.Usage);
            if (limit is not null)
            {
                yield return new AgentResultEvent(progress.Result(limit));
                yield break;
            }
        }
    }

    private async IAsyncEnumerable<IAgentStreamEvent> StreamModelAsync(ModelTurn turn, InvocationProgress progress, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        turn.InvocationState = progress.InvocationState;
        var selectedModel = _options.ModelSelectionTiming == ModelSelectionTiming.BeforeModelHooks
            ? ResolveInvocationModel(progress, Model) : null;
        var messages = Messages;
        IReadOnlyList<Message> visible = UsesNativeStatefulHistory ? Array.AsReadOnly(messages.Skip(EffectiveModelHistoryCount).ToArray())
            : _contextManager is null ? _options.ConversationManager?.Reduce(messages) ?? messages : messages;
        var modelState = ModelState.GetAll();
        var onlyStructured = progress.ForceStructured && progress.StructuredBehavior == StructuredOutputBehavior.Python;
        var specs = onlyStructured ? new List<ToolSpec>() : Tools.Select(RegisteredToolSpec).ToList();
        if (!onlyStructured) specs.AddRange(progress.EphemeralTools.Values.Select(tool => tool.Spec));
        if (progress.StructuredTool is { } structured) specs.Add(structured.Spec);
        var request = new ModelRequest(visible, SystemPrompt, specs.ToArray())
        {
            SystemPromptContent = SystemPromptContent,
            SessionId = SessionId,
            ModelState = modelState,
            ToolChoice = progress.ForceStructured ? new(ToolChoiceMode.Tool, progress.StructuredTool!.Spec.Name) : null
        };
        var before = new BeforeModelCallEvent(this, request, progress.InvocationState);
        (ModelRequest Request, IReadOnlyList<Message> Appended)? prepared = null;
        try { prepared = await ApplyBeforeModelContinuationAsync(before, InvocationCallbackToken(cancellationToken)).ConfigureAwait(false); }
        catch (BackgroundModelInterruptException pause) { turn.BackgroundInterrupts = pause.Interrupts; }
        if (prepared is not { } preparedBefore) yield break;
        foreach (var appended in preparedBefore.Appended) yield return new MessageAddedEvent(this, appended) { InvocationState = progress.InvocationState };
        CheckInvocationCancellation(cancellationToken, NativeCancellationCheckpoints);
        if (before.CancelReason is { } denied)
        {
            turn.BeforeModelDenied = true;
            turn.Message = new(Role.Assistant, [new TextBlock(denied)]);
            turn.StopReason = StopReasons.EndTurn;
            await FinishModelAsync(null, turn, modelState, cancellationToken).ConfigureAwait(false);
            yield break;
        }
        selectedModel ??= ResolveInvocationModel(progress, Model);
        request = preparedBefore.Request;
        if (UsesNativeStatefulHistory && preparedBefore.Appended.Count > 0) visible = Messages.Skip(EffectiveModelHistoryCount).ToArray();
        if (UsesNativeStatefulHistory && !ContainsPendingHistory(request.Messages, visible))
            throw new InvalidOperationException("A stateful model request must retain every pending message and content block in order.");
        // Provider state belongs to the agent, not model-input transformations.
        request = request with { ModelState = modelState };
        CheckInvocationCancellation(cancellationToken, NativeCancellationCheckpoints);
        IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>>? iterator = null;
        var terminalAccepted = false;
        var middlewareCleanupFailed = false;
        try
        {
            try
            {
                long? projectedInputTokens = null;
                var projectionToken = CancellationPreparationToken(cancellationToken);
                try { projectedInputTokens = await selectedModel.CountTokensAsync(request, projectionToken).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (projectionToken.IsCancellationRequested ||
                    (cancellationToken.IsCancellationRequested && (error.CancellationToken == cancellationToken || error.Data["Strands.CleanupErrors"] is List<Exception> { Count: > 0 })))
                { throw; }
                catch (Exception) { /* An unavailable projection must not prevent the actual model operation. */ }
                var context = new InvokeModelContext(this, selectedModel, request, progress.InvocationState) { ProjectedInputTokens = projectedInputTokens };
                iterator = Middleware.InvokeAsync(MiddlewareStages.InvokeModel, context,
                    (value, _) => StreamModelTerminalAsync(value, turn, modelState, visible, cancellationToken), InvocationCallbackToken(cancellationToken)).GetAsyncEnumerator(InvocationCallbackToken(cancellationToken));
            }
            catch (Exception error) { turn.Error = error; }
            if (iterator is null) yield break;
            while (true)
            {
                var next = false;
                IMiddlewareEvent<InvokeModelResult>? current = null;
                IAgentStreamEvent? observation = null;
                try
                {
                    next = await iterator.MoveNextAsync().ConfigureAwait(false);
                    if (next)
                    {
                        current = iterator.Current;
                        if (current is MiddlewareResultEvent<InvokeModelResult> completed)
                        {
                            ValidateStatefulModelResult(turn, completed.Result.Message);
                            turn.Message = completed.Result.Message;
                            turn.StopReason = completed.Result.StopReason;
                            turn.Usage = completed.Result.Usage;
                            turn.UsageReported = completed.Result.UsageReported;
                            turn.PerformanceMetrics = completed.Result.PerformanceMetrics;
                            turn.InputRedaction = completed.Result.InputRedaction;
                            terminalAccepted = true;
                        }
                    }
                    if (!terminalAccepted) cancellationToken.ThrowIfCancellationRequested();
                    if (current is MiddlewareStreamEvent<InvokeModelResult> update)
                    {
                        observation = update.Event switch
                        {
                            ModelStreamUpdateEvent model => new ModelStreamUpdateEvent(this, CloneMiddlewareModelEvent(model.Event), turn.InvocationState),
                            ContentBlockEvent block => new ContentBlockEvent(this, Message.CloneBlock(block.ContentBlock), turn.InvocationState),
                            _ => update.Event
                        };
                        if (observation is ModelStreamUpdateEvent or ContentBlockEvent)
                            await Hooks.InvokeAsync(observation, InvocationCallbackToken(cancellationToken)).ConfigureAwait(false);
                    }
                }
                catch (Exception error) { turn.Error = error; }
                if (turn.Error is not null || !next) break;
                if (observation is not null) yield return observation;
            }
            if (turn.Error is null && terminalAccepted)
            {
                var completedIterator = iterator;
                iterator = null;
                try { await completedIterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) { middlewareCleanupFailed = true; turn.Error = failure; }
                ModelMessageEvent? completedObservation = null;
                if (turn.Error is null)
                    try
                    {
                        var message = turn.Message!;
                        completedObservation = new(this, new(message.Role, message.Content, message.TrackingId, message.Metadata), turn.StopReason!, turn.InvocationState);
                        await Hooks.InvokeAsync(completedObservation, InvocationCallbackToken(cancellationToken)).ConfigureAwait(false);
                    }
                    catch (Exception failure) { turn.Error = failure; }
                if (turn.Error is null && completedObservation is not null) yield return completedObservation;
            }
        }
        finally
        {
            try { await FinishModelAsync(iterator, turn, modelState, cancellationToken, middlewareCleanupFailed).ConfigureAwait(false); }
            finally { RecordModelTelemetry(turn); }
        }
    }

    private async ValueTask FinishModelAsync(IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>>? iterator, ModelTurn turn, IReadOnlyDictionary<string, JsonElement> modelState, CancellationToken cancellationToken, bool middlewareCleanupFailed = false)
    {
        var primary = turn.Error;
        var disposalFailed = middlewareCleanupFailed;
        try { if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { disposalFailed = true; if (turn.Error is null) turn.Error = cleanup; else AttachCleanupError(turn.Error, cleanup); }
        try { RestoreMiddlewareModelState(modelState); }
        catch (Exception cleanup) { if (turn.Error is null) turn.Error = cleanup; else AttachCleanupError(turn.Error, cleanup); }
        var after = new AfterModelCallEvent(this, turn.Message, turn.StopReason, turn.Error, turn.InvocationState) { InvocationCancellationToken = cancellationToken };
        try
        {
            await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false);
            turn.Retry = after.Retry && !disposalFailed && !turn.ProviderCleanupFailed && turn.Error is not OperationCanceledException && !cancellationToken.IsCancellationRequested;
            if (turn.Error is null && !turn.Retry && (turn.Message is not null || after.Message is not null))
            {
                var transformed = new InvokeModelResult(after.Message!, after.StopReason!, turn.Usage) { PerformanceMetrics = turn.PerformanceMetrics, UsageReported = turn.UsageReported }.Snapshot();
                ValidateStatefulModelResult(turn, transformed.Message);
                turn.Message = transformed.Message;
                turn.StopReason = transformed.StopReason;
            }
        }
        catch (Exception cleanup) { if (turn.Error is null) turn.Error = cleanup; else AttachCleanupError(turn.Error, cleanup); }
        if (primary is null && turn.Error is not null) ExceptionDispatchInfo.Capture(turn.Error).Throw();
    }

    private async Task<ToolBatch> ExecuteToolsAsync(ToolUseBlock[] calls, IReadOnlyDictionary<string, object?> state, InvocationProgress progress, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish = null)
    {
        progress.ToolUseCount = calls.Length;
        var resumingPausedBatch = _activeToolBatch!.IsPaused;
        var before = new BeforeToolsEvent(this, Array.AsReadOnly(calls), state);
        try { await Hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false); }
        catch (InterruptException interrupted)
        {
            foreach (var question in interrupted.Interrupts) _activeToolBatch!.Register(question, question.ToolUseId);
            return new(_activeToolBatch!.Snapshot().CompletedToolResults.Cast<ContentBlock>().ToArray(), interrupted.Data["Strands.CleanupErrors"] is List<Exception> { Count: > 0 } ? interrupted : null) { Interrupted = true };
        }
        // A postponed request must not turn unanswered, durable human calls into completed cancellation results.
        if (!NativeCancellationCheckpoints && resumingPausedBatch && cancellationToken.IsCancellationRequested)
            return new(_activeToolBatch!.Snapshot().CompletedToolResults.Cast<ContentBlock>().ToArray(), null) { Interrupted = true, PausedByCancellation = true };
        var denied = before.CancelReason ?? (calls.Length > 1 && calls.Any(call => IsRegisteredDelegation(call.Name, progress))
            ? "A delegation tool must be the only tool called in a turn. Retry with one delegation tool or only ordinary tools." : null);
        ToolBatch batch;
        if (denied is not null)
        {
            var results = new List<ContentBlock>();
            Exception? failure = null;
            foreach (var call in calls)
            {
                var outcome = await PublishToolResultAsync(new(new(call.ToolUseId, denied, ToolResultStatus.Error), null), state, cancellationToken, publish).ConfigureAwait(false);
                _activeToolBatch!.Complete(outcome.Result); results.Add(outcome.Result);
                if (outcome.Error is { } error) { if (failure is null) failure = error; else AttachCleanupError(failure, error); }
            }
            batch = new(results, failure);
        }
        else batch = await CollectToolsAsync(calls, state, progress, cancellationToken, publish).ConfigureAwait(false);
        if (progress.StructuredTool is { } structured)
            batch = batch with { ValidatedStructuredIds = _activeToolBatch!.SuccessfulBodyIds(structured.Spec.Name) };
        // Python captures the validated value before final batch hooks and keeps it on preempting results.
        if (batch.Error is null && progress.StructuredBehavior == StructuredOutputBehavior.Python && progress.StructuredTool is { } pythonOutput)
            progress.StructuredOutputToolUseId = SelectAcceptedStructuredOutput(pythonOutput, progress.StructuredBehavior, calls, batch, cancellationToken);
        var after = new AfterToolsEvent(this, batch.Results.Cast<ToolResultBlock>().ToArray(), state)
        { EndTurn = progress.StructuredTool is null && !IsStateful ? DelegationContent(batch) : null };
        try
        {
            await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false);
            if (after.EndTurn is { } content)
            {
                if (IsStateful) throw new InvalidOperationException("EndTurn cannot skip server-side tool acknowledgement on a stateful model.");
                batch = batch with { EndTurn = ValidateEndTurn(content) };
            }
        }
        catch (Exception error)
        {
            if (batch.Error is null) return batch with { Error = error };
            AttachCleanupError(batch.Error, error);
        }
        return batch;
    }

    private async Task<ToolBatch> CollectToolsAsync(ToolUseBlock[] calls, IReadOnlyDictionary<string, object?> state, InvocationProgress progress, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
    {
        if (_options.ToolBatchExecutor is { } executor)
            return await CollectCustomToolsAsync(executor, calls, state, progress, cancellationToken, publish).ConfigureAwait(false);
        var active = _activeToolBatch!;
        if (_options.ToolExecution == ToolExecutionStrategy.Sequential)
        {
            var results = new List<ToolResultBlock>();
            Exception? failure = null;
            string? delegationId = null;
            for (var i = 0; i < calls.Length; i++)
            {
                if (failure is not null)
                {
                    var skipped = await PublishToolResultAsync(new(CancelledTool(calls[i]), null), state, cancellationToken, publish).ConfigureAwait(false);
                    active.Complete(skipped.Result);
                    results.Add(skipped.Result);
                    if (skipped.Error is { } cleanup) AttachCleanupError(failure, cleanup);
                    continue;
                }
                var outcome = await RunToolPipelineAsync(calls[i], state, progress, cancellationToken, publish).ConfigureAwait(false);
                if (outcome.Interrupt is not null) break;
                results.Add(outcome.Result);
                if (outcome.Delegation) delegationId = calls[i].ToolUseId;
                failure = outcome.Error;
            }
            return new(results.Cast<ContentBlock>().ToArray(), failure) { Interrupted = active.IsPaused, DelegationToolUseId = delegationId };
        }
        using var concurrency = new SemaphoreSlim(_options.MaxParallelTools);
        var outcomes = await Task.WhenAll(calls.Select(async call =>
        {
            await concurrency.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { return await RunToolPipelineAsync(call, state, progress, cancellationToken, publish).ConfigureAwait(false); }
            finally { concurrency.Release(); }
        })).ConfigureAwait(false);
        Exception? primary = null;
        foreach (var outcome in outcomes)
        {
            if (outcome.Error is not { } error) continue;
            if (primary is null) primary = error;
            else if (primary is { } existing && !ReferenceEquals(existing, error)) AttachCleanupError(existing, error);
        }
        return new(outcomes.Where(outcome => outcome.Interrupt is null).Select(outcome => outcome.Result).Cast<ContentBlock>().ToArray(), primary)
        { Interrupted = active.IsPaused, DelegationToolUseId = outcomes.SingleOrDefault(outcome => outcome.Delegation)?.Result.ToolUseId };
    }

    private async ValueTask<ToolOutcome> RunToolPipelineAsync(ToolUseBlock call, IReadOnlyDictionary<string, object?> state, InvocationProgress progress, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
    {
        ToolOutcome outcome;
        var active = _activeToolBatch!;
        if (active.TryGetResult(call.ToolUseId, out var completed))
        {
            // A new structured invocation owns a fresh typed decoder. Restore accepted input without replaying an external tool.
            if (completed.Status == ToolResultStatus.Success && progress.StructuredTool is { } structured && call.Name == structured.Spec.Name && !structured.TryGetResult(call.ToolUseId, out _))
                await structured.InvokeAsync(call.Input, new(this, call, state), cancellationToken).ConfigureAwait(false);
            return new(completed, null);
        }
        try { outcome = await ExecuteToolAsync(call, state, progress, cancellationToken, publish).ConfigureAwait(false); }
        catch (InterruptException interrupted)
        {
            foreach (var question in interrupted.Interrupts) active.Register(question, call.ToolUseId);
            return new(CancelledTool(call), interrupted.Data["Strands.CleanupErrors"] is List<Exception> { Count: > 0 } ? interrupted : null) { Interrupt = interrupted.Interrupt };
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { outcome = new(CancelledTool(call) with { Error = error }, null); }
        catch (Exception error) { outcome = new(new(call.ToolUseId, $"Tool pipeline failed: {error.Message}", ToolResultStatus.Error) { Error = error }, error); }
        active.Complete(outcome.Result);
        return await PublishToolResultAsync(outcome, state, cancellationToken, publish).ConfigureAwait(false);
    }

    private async ValueTask<ToolOutcome> PublishToolResultAsync(ToolOutcome outcome, IReadOnlyDictionary<string, object?> state, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
    {
        if (publish is null || cancellationToken.IsCancellationRequested) return outcome;
        try { await publish(new ToolResultEvent(this, outcome.Result, state), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (outcome.Error is null) return outcome with { Error = error };
            AttachCleanupError(outcome.Error, error);
        }
        return outcome;
    }

    private async ValueTask<ToolOutcome> ExecuteToolAsync(ToolUseBlock call, IReadOnlyDictionary<string, object?> state, InvocationProgress progress, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
    {
        var requestedName = call.Name;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested) return new(CancelledTool(call), null);
            var before = new BeforeToolCallEvent(this, call, state);
            await Hooks.InvokeAsync(before, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var providerId = call.ToolUseId;
            call = (ToolUseBlock)Message.CloneBlock(before.ToolUse with { ToolUseId = providerId });
            var selected = before.SelectedTool ?? (progress.StructuredTool is { } structured && call.Name == structured.Spec.Name
                ? structured : progress.EphemeralTools.TryGetValue(call.Name, out var ephemeral) ? ephemeral : _tools.TryGetValue(call.Name, out var registered) ? registered.Tool : null);
            ToolResultBlock? admissionError = null;
            if (_backgroundTasks is { } background && before.CancelReason is null)
            {
                try
                {
                    var prepared = background.Policy.Prepare(call, requestedName); call = prepared.Call;
                    if (background.Policy.ShouldDispatch(selected, prepared.Selected, selected is null ? null : RegisteredToolSpec(selected)))
                        return new(background.Dispatch(call, selected!, state, progress.Message!.TrackingId, cancellationToken), null);
                }
                catch (ArgumentException error) { admissionError = new(call.ToolUseId, error.Message, ToolResultStatus.Error) { Error = error }; }
            }
            if (selected is not null && IsDelegationTool(selected) && (IsStateful || progress.ToolUseCount > 1))
                admissionError = new(call.ToolUseId, IsStateful ? "Delegation is not supported by a stateful parent model." : "A delegation tool must be the only tool called in a turn.", ToolResultStatus.Error);
            var result = admissionError ?? await InvokeToolMiddlewareAsync(new(this, selected, call, state, cancellationToken), before.CancelReason, publish, cancellationToken).ConfigureAwait(false);
            result = result with { IteratorCleanupFailed = result.IteratorCleanupFailed || _activeToolBatch?.IteratorCleanupFailed(providerId) == true };
            var after = new AfterToolCallEvent(this, call, result, state);
            try { await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error)
            {
                try { result = NormalizeToolResult(after.Result, providerId); }
                catch (Exception invalidResult) { AttachCleanupError(error, invalidResult); }
                return new(result, error);
            }
            if (ShouldRetryTool(after, result, cancellationToken))
            {
                if (selected is IStructuredOutputAttempt structuredAttempt) structuredAttempt.DiscardResult(providerId);
                _activeToolBatch?.ResetAttempt(providerId);
                continue;
            }
            return new(NormalizeToolResult(after.Result, providerId), null) { Delegation = selected is not null && IsDelegationTool(selected) && admissionError is null };
        }
    }

    private static bool ShouldRetryTool(AfterToolCallEvent after, ToolResultBlock original, CancellationToken cancellationToken) =>
        after.Retry && !cancellationToken.IsCancellationRequested && original.Error is not OperationCanceledException &&
        after.Result?.Error is not OperationCanceledException && !original.IteratorCleanupFailed;

    private static ToolResultBlock NormalizeToolResult(ToolResultBlock result, string providerId)
    {
        if (result is null || !Enum.IsDefined(typeof(ToolResultStatus), result.Status))
            throw new InvalidOperationException("A tool hook supplied an invalid result.");
        return (ToolResultBlock)Message.CloneBlock(result with { ToolUseId = providerId });
    }

    private async ValueTask<ToolResultBlock> InvokeToolBodyAsync(ITool tool, ToolUseBlock call, IReadOnlyDictionary<string, object?> state, CancellationToken cancellationToken,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish,
        Func<string, JsonElement?, JsonElement?, JsonElement>? interruptHandler = null, AgentToolCallContext? agentToolInterrupts = null,
        CancellationToken? executionCancelSignal = null)
    {
        var signal = CancelSignal;
        var context = new ToolContext(this, call, state)
        {
            ExecutionCancelSignal = executionCancelSignal ?? (signal.CanBeCanceled ? signal : cancellationToken),
            InterruptHandler = interruptHandler,
            AgentToolInterrupts = agentToolInterrupts ?? _activeToolBatch?.AgentToolContext(call.ToolUseId)
        };
        var iteratorCleanupFailed = false;
        try
        {
            if (tool is IStreamingTool streaming)
                return await ToolStreamRunner.InvokeAsync(streaming, call.Input, context,
                    publish is null ? null : (update, token) => publish(IsDelegationTool(tool) && update.Data is ModelStreamUpdateEvent native
                        ? (IAgentStreamEvent)native : new ToolStreamUpdateEvent(this, call, update, state), token), cancellationToken, () => iteratorCleanupFailed = true).ConfigureAwait(false)
                    with
                { IteratorCleanupFailed = iteratorCleanupFailed };
            if (tool is IRichTool rich)
            {
                var value = await rich.InvokeResultAsync(call.Input, context, cancellationToken).ConfigureAwait(false);
                if (value.ToolUseId != call.ToolUseId) throw new InvalidOperationException("A rich tool returned a different tool-use identifier.");
                return (ToolResultBlock)Message.CloneBlock(value) with { IteratorCleanupFailed = false };
            }
            var result = await tool.InvokeAsync(call.Input, context, cancellationToken).ConfigureAwait(false);
            return new(call.ToolUseId, result);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { return CancelledTool(call) with { Error = error, IteratorCleanupFailed = iteratorCleanupFailed }; }
        catch (InterruptException) { throw; }
        catch (Exception error) { return new(call.ToolUseId, error.Message, ToolResultStatus.Error) { Error = error, IteratorCleanupFailed = iteratorCleanupFailed }; }
    }

    private async ValueTask<bool> RestoreSessionAsync(CancellationToken cancellationToken, PreparedSessionRestore? selectedSession = null)
    {
        if (_restored) return false;
        var loaded = false;
        if (selectedSession is not null)
        {
            ApplyPreparedSessionRestore(selectedSession, initial: true);
            loaded = true;
        }
        else if (_options.SessionStore is { } store)
        {
            var snapshot = await store.LoadAsync(SessionId!, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                var catalog = store is SessionCatalogs.SnapshotSessionStore;
                var prepared = PrepareSessionRestore(snapshot, catalog);
                if (catalog) cancellationToken.ThrowIfCancellationRequested();
                ApplyPreparedSessionRestore(prepared, initial: true);
                loaded = true;
            }
        }
        _restored = true;
        return loaded;
    }

    private async ValueTask AddMessageAsync(Message message, IReadOnlyDictionary<string, object?> invocationState, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, JsonElement>? modelState = null, bool acknowledgeHistory = false,
        InvocationCancellationScope? acceptedCancellation = null)
    {
        var repositoryAppend = PrepareRepositoryAppend(new[] { message });
        lock (_historyGate)
        {
            if (modelState is not null) ModelState.Replace(modelState);
            _messages.Add(message);
            if (acknowledgeHistory) { _modelHistoryCount = _messages.Count; _modelInputReceipt = null; }
        }
        MarkAcceptedHistory(cancellationToken, acceptedCancellation);
        repositoryAppend?.Accept();
        Exception? failure = null;
        try { await Hooks.InvokeAsync(new MessageAddedEvent(this, message) { InvocationState = invocationState }, InvocationCallbackToken(cancellationToken)).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await SaveSessionAsync(failure is null ? AcceptedWriteToken(cancellationToken) : CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (failure is not null) { AttachCleanupError(failure, cleanup); }
        }
    }

    private async ValueTask AddInputMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyDictionary<string, object?> invocationState, CancellationToken cancellationToken, Func<ValueTask>? onCommitted = null)
    {
        CheckInvocationCancellation(cancellationToken, BeforeInputCancellationCheckpoint);
        var repositoryAppend = PrepareRepositoryAppend(messages);
        lock (_historyGate)
        {
            ConversationHistory.Validate(_messages.Concat(messages).ToArray(), _messages.Count == 0 ? ModelHistoryBehavior.Native : _options.ModelHistoryBehavior);
            _messages.AddRange(messages);
        }
        MarkAcceptedHistory(cancellationToken);
        repositoryAppend?.Accept();
        var catalogBatchSaved = false;
        Exception? failure = null;
        try
        {
            if (onCommitted is not null) await onCommitted().ConfigureAwait(false);
            foreach (var message in messages)
            {
                await Hooks.InvokeAsync(new MessageAddedEvent(this, message) { InvocationState = invocationState }, InvocationCallbackToken(cancellationToken)).ConfigureAwait(false);
                if (UsesCatalogMessageStrategy) await SaveSessionAsync(AcceptedWriteToken(cancellationToken)).ConfigureAwait(false);
            }
            catalogBatchSaved = UsesCatalogMessageStrategy;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { if (!catalogBatchSaved) await SaveSessionAsync(failure is null ? AcceptedWriteToken(cancellationToken) : CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (failure is not null) { AttachCleanupError(failure, cleanup); }
        }
    }

    private async ValueTask SaveSessionAsync(CancellationToken cancellationToken)
    {
        if (_options.SessionStore is not { } store) return;
        // Deferral occurs before capture or persisted-version bookkeeping, never as a fake store success.
        if (store is SessionCatalogs.SnapshotSessionStore catalog && Volatile.Read(ref _invoking) != 0 && !_abortingInterrupts && catalog.Manager.SaveLatestOn != SnapshotSessionSaveStrategy.Message) return;
        await FlushRepositoryRedactionsAsync().ConfigureAwait(false);
        var cancellation = Volatile.Read(ref _invocationCancellation);
        var historyVersion = cancellation?.HistoryVersion ?? 0;
        var write = await PrepareSessionWriteAsync(Messages, InterruptState, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(SessionId!, write.Snapshot, cancellationToken).ConfigureAwait(false);
        _savedStateVersion = write.StateVersion;
        _savedModelStateVersion = write.ModelStateVersion;
        _restoredSourceHistoryDirty = false;
        RecordSavedConversationState(write.Snapshot);
        cancellation?.MarkHistoryPersisted(historyVersion);
    }

    internal void ValidateOwnedContextHistory(IReadOnlyList<Message> messages)
        => ConversationHistory.Validate(messages, _options.ModelHistoryBehavior);

    internal async ValueTask ReplaceContextAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        ValidateOwnedContextHistory(messages);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_historyGate) { _messages.Clear(); _messages.AddRange(messages); }
        await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal IReadOnlyDictionary<string, JsonElement>? TakeRestoredContextStash()
    {
        var entries = RestoredContextStash;
        RestoredContextStash = null;
        return entries;
    }

    private async ValueTask RepairPendingToolsAsync(IReadOnlyDictionary<string, object?> invocationState)
    {
        if (!_restored) return;
        if (InterruptState is not null) return;
        Message? last;
        lock (_historyGate) last = _messages.LastOrDefault();
        if (last?.Role != Role.Assistant) return;
        var pending = last.Content.OfType<ToolUseBlock>().ToArray();
        if (pending.Length > 0)
            await AddMessageAsync(new(Role.User, pending.Select(CancelledTool).Cast<ContentBlock>().ToArray()), invocationState, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<AfterInvocationEvent> FinishInvocationAsync(AgentResult? result, Exception? primary, IReadOnlyDictionary<string, object?> invocationState,
        CancellationToken cancellationToken, bool continuationAllowed, bool checkpointPaused)
    {
        var failure = primary;
        try { if (!checkpointPaused) await RepairPendingToolsAsync(invocationState).ConfigureAwait(false); }
        catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
        var clearedHistory = false;
        try
        {
            if (!checkpointPaused && _restored && UsesNativeStatefulHistory && InterruptState is null)
            {
                lock (_historyGate)
                {
                    // Pending results survive cancellation/errors; only acknowledged history belongs exclusively to the server.
                    if (_modelHistoryCount > 0 && _modelHistoryCount == _messages.Count)
                    {
                        var repositoryClear = PrepareRepositoryAcknowledgedHistoryClear(_messages);
                        _messages.Clear(); _modelHistoryCount = 0; clearedHistory = true;
                        repositoryClear?.Accept();
                    }
                }
            }
        }
        catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
        var after = new AfterInvocationEvent(this, result, failure, invocationState)
        { InvocationCancellationToken = cancellationToken, ContinuationAllowed = continuationAllowed };
        try { await Hooks.InvokeAsync(after, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
        try
        {
            if (_options.SessionStore is SessionCatalogs.SnapshotSessionStore catalog)
                await CompleteCatalogInvocationAsync(catalog, CancellationToken.None).ConfigureAwait(false);
            else if (_restored && (clearedHistory || _restoredSourceHistoryDirty || State.Version != _savedStateVersion || ModelState.Version != _savedModelStateVersion || ConversationStateChanged() || Volatile.Read(ref _invocationCancellation)?.HasUnpersistedHistory == true)) await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanup) { if (failure is null) failure = cleanup; else AttachCleanupError(failure, cleanup); }
        if (checkpointPaused && failure is not null)
        {
            try { await RepairPendingToolsAsync(invocationState).ConfigureAwait(false); }
            catch (Exception cleanup) { AttachCleanupError(failure, cleanup); }
        }
        if (primary is null && failure is not null)
        {
            await _continuationInputs.AbandonAsync(after, failure).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return after;
    }

    private static bool ContainsPendingHistory(IReadOnlyList<Message> actual, IReadOnlyList<Message> pending)
    {
        if (actual is null) return false;
        var next = 0;
        foreach (var original in pending)
        {
            while (next < actual.Count && actual[next].TrackingId != original.TrackingId) next++;
            if (next == actual.Count || actual[next].Role != original.Role) return false;
            var blocks = actual[next++].Content;
            var index = 0;
            foreach (var block in original.Content)
            {
                var expected = JsonSerializer.SerializeToElement<ContentBlock>(block);
                while (index < blocks.Count && !JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement<ContentBlock>(blocks[index]))) index++;
                if (index++ == blocks.Count) return false;
            }
        }
        return true;
    }

    private static void AttachCleanupError(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }

    private string? GetLimit(InvocationOptions options, int turns, Usage usage)
    {
        if (turns >= (options.MaxTurns ?? _options.MaxTurns)) return StopReasons.MaxTurns;
        if (options.MaxTotalTokens is { } total && usage.TotalTokens >= total) return StopReasons.MaxTotalTokens;
        if (options.MaxOutputTokens is { } output && usage.OutputTokens >= output) return StopReasons.MaxOutputTokens;
        return null;
    }

    private static ToolResultBlock CancelledTool(ToolUseBlock call) => new(call.ToolUseId, "Tool execution was cancelled before a result was committed.", ToolResultStatus.Error);
    private static void ValidateInvocationOptions(InvocationOptions options)
    {
        Guard.NotNull(options.InvocationState, nameof(options.InvocationState));
        if (options.MaxTurns is < 1 || options.MaxTotalTokens is < 1 || options.MaxOutputTokens is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Invocation limits must be positive.");
    }

    private sealed class InvocationProgress(IReadOnlyDictionary<string, object?> state)
    {
        public IReadOnlyDictionary<string, object?> InvocationState => state;
        public IModel? ModelOwner { get; set; }
        public IModel? Model { get; set; }
        public IStructuredOutputTool? StructuredTool { get; set; }
        public StructuredOutputBehavior StructuredBehavior { get; set; }
        public bool ExposeStructuredOutput { get; set; }
        public IReadOnlyDictionary<string, ITool> EphemeralTools { get; set; } = new Dictionary<string, ITool>();
        public AgentCheckpoint? Checkpoint { get; set; }
        public int CheckpointCycleIndex { get; set; }
        public CheckpointPosition? CheckpointResumePosition { get; set; }
        public string? StructuredOutputToolUseId { get; set; }
        public bool ForceStructured { get; set; }
        public int StructuredAttempts { get; set; }
        public int ToolUseCount { get; set; }
        public Usage Usage { get; set; } = new();
        public int Turns { get; set; }
        public Message? Message { get; set; }
        public Message? ResumeMessage { get; set; }
        public IReadOnlyList<Interrupt> Interrupts { get; set; } = [];
        public bool Denied { get; set; }
        public void ResetCycle()
        {
            ModelOwner = null; Model = null; ResumeMessage = null; Interrupts = []; Denied = false;
            ForceStructured = false; StructuredOutputToolUseId = null;
        }
        public AgentResult Result(string reason) => new(reason, Message, Usage, Turns, state) { Checkpoint = reason == StopReasons.Checkpoint ? Checkpoint : null, StructuredOutputToolUseId = StructuredOutputToolUseId, StructuredOutput = ExposeStructuredOutput ? AcceptedStructuredJson(StructuredTool, StructuredOutputToolUseId, reason, StructuredBehavior) : null, Interrupts = Interrupts };
    }
    private sealed class ModelTurn
    {
        public bool UsageReported { get; set; }
        public Telemetry.ModelPerformanceMetrics? PerformanceMetrics { get; set; }
        public IReadOnlyDictionary<string, object?> InvocationState { get; set; } = new Dictionary<string, object?>();
        public Usage Usage { get; set; } = new();
        public IReadOnlyDictionary<string, JsonElement>? ModelState { get; set; }
        public bool ProviderCompleted { get; set; }
        public bool ProviderStateful { get; set; }
        public int ProviderInputMessageCount { get; set; }
        public string? ProviderInputLastTrackingId { get; set; }
        public bool BeforeModelDenied { get; set; }
        public bool ProviderCleanupFailed { get; set; }
        public Message? ProviderMessage { get; set; }
        public string? InputRedaction { get; set; }
        public IReadOnlyList<Interrupt>? BackgroundInterrupts { get; set; }
        public Message? Message { get; set; }
        public string? StopReason { get; set; }
        public Exception? Error { get; set; }
        public bool Retry { get; set; }
    }
    private sealed record ToolOutcome(ToolResultBlock Result, Exception? Error)
    {
        internal Interrupt? Interrupt { get; init; }
        internal bool Delegation { get; init; }
    }
    private sealed record ToolBatch(IReadOnlyList<ContentBlock> Results, Exception? Error)
    {
        internal bool Interrupted { get; init; }
        internal bool PausedByCancellation { get; init; }
        internal IReadOnlyList<string> ValidatedStructuredIds { get; init; } = [];
        internal string? DelegationToolUseId { get; init; }
        internal Message? EndTurn { get; init; }
    }
}
