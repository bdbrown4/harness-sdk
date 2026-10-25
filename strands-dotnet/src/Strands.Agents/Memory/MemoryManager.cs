using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Memory;

/// <summary>Routes retrieval and writes across named memory backends and exposes model tools.</summary>
public sealed partial class MemoryManager : IPlugin
{
    private readonly MemoryStore[] _stores;
    private readonly MemoryTelemetry _telemetry;
    private readonly MemoryManagerOptions _options;
    private readonly MemoryStore[] _searchScope;
    private readonly MemoryStore[] _addScope;
    private readonly ExtractionBinding[] _extractionBindings;
    private ExtractionCoordinator? _coordinator;
    private readonly List<IDisposable> _extractionTriggers = [];
    private readonly object _initializationGate = new();
    private Task? _initialization;
    private Agent? _agent;
    private IReadOnlyList<ITool> _tools;
    /// <summary>Creates a manager from explicit memory backends.</summary>
    public MemoryManager(MemoryManagerOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Stores, nameof(options.Stores));
        Guard.NotNull(options.Telemetry, nameof(options.Telemetry));
        _stores = options.Stores.ToArray();
        if (_stores.Length == 0) throw new ArgumentException("At least one memory store is required.", nameof(options));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in _stores)
        {
            Guard.NotNull(store, nameof(options.Stores));
            Guard.NotNullOrWhiteSpace(store.Name, nameof(options.Stores));
            if (!names.Add(store.Name)) throw new ArgumentException($"Duplicate memory store name: {store.Name}.", nameof(options));
            if (store.MaxSearchResults is <= 0) throw new ArgumentException($"The result limit for store '{store.Name}' must be positive.", nameof(options));
            if (store.Writable && store is not IWritableMemoryStore && store is not IMessageMemoryStore)
                throw new ArgumentException($"Writable memory store '{store.Name}' has no write capability.", nameof(options));
        }
        _options = options with
        {
            Stores = Array.AsReadOnly(_stores),
            SearchTool = options.SearchTool is { } search ? search with { Stores = search.Stores?.ToArray() } : null,
            AddTool = options.AddTool is { } add ? add with { Stores = add.Stores?.ToArray() } : null,
            Telemetry = options.Telemetry with
            {
                TraceAttributes = options.Telemetry.TraceAttributes is { } attributes
                    ? new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(attributes.ToDictionary(
                        pair => pair.Key, pair => pair.Value is Array array ? array.Clone() : pair.Value, StringComparer.Ordinal)) : null
            }
        };
        _telemetry = new(_options.Telemetry);
        if (_options.Injection is { } injection && (injection.MaxEntries <= 0 || !Enum.IsDefined(typeof(MemoryInjectionTrigger), injection.Trigger)))
            throw new ArgumentException("Memory injection requires a positive result limit and a known trigger.", nameof(options));
        _searchScope = _options.SearchTool is { } searchTool ? ResolveStores(searchTool.Stores) : [];
        _addScope = _options.AddTool is { } addTool ? ResolveStores(addTool.Stores) : [];
        if (_options.AddTool?.Stores is null) _addScope = _addScope.Where(store => store.Writable && store is IWritableMemoryStore).ToArray();
        if (_options.AddTool is not null)
        {
            if (!_stores.Any(store => store.Writable && store is IWritableMemoryStore)) throw new ArgumentException("The add tool requires a writable entry store.", nameof(options));
            if (_addScope.Any(store => !store.Writable || store is not IWritableMemoryStore)) throw new ArgumentException("The add tool scope includes a store without entry-write permission.", nameof(options));
        }
        _tools = Array.AsReadOnly(CreateTools().ToArray());
        _extractionBindings = _stores.Where(store => store.Extraction is not null).Select(store =>
        {
            var extraction = store.Extraction!;
            if (!store.Writable) throw new ArgumentException($"Memory store '{store.Name}' enables extraction without write permission.", nameof(options));
            if (extraction.Triggers is null || extraction.Triggers.Count == 0 || extraction.Triggers.Any(trigger => trigger is null))
                throw new ArgumentException($"Memory store '{store.Name}' requires at least one extraction trigger.", nameof(options));
            if (extraction.ExcludeContentBlocks is null || extraction.ExcludeContentBlocks.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Memory store '{store.Name}' has invalid excluded content kinds.", nameof(options));
            var extractor = extraction.Extractor ?? (store is IMessageMemoryStore ? null : new ModelExtractor());
            if (extractor is not null && store is not IWritableMemoryStore) throw new ArgumentException($"Memory store '{store.Name}' has an extractor without an entry-write capability.", nameof(options));
            return new ExtractionBinding(store, extraction with { Triggers = extraction.Triggers.ToArray(), ExcludeContentBlocks = extraction.ExcludeContentBlocks.ToArray() }, extractor);
        }).ToArray();
    }
    /// <inheritdoc />
    public string Name => "strands:memory-manager";
    /// <summary>The configured backends in their default search order.</summary>
    public IReadOnlyList<MemoryStore> Stores => _options.Stores;
    /// <summary>Creates a separate retrieval-only manager, removing write capabilities, extraction, and all backend tools.</summary>
    public MemoryManager ToReadOnly() => new(_options with { Stores = _stores.Select(store => (MemoryStore)new ReadOnlyStore(store)).ToArray(), AddTool = null });

    internal MemoryManager CreateForAgent() => new(_options);

    private sealed class ReadOnlyStore(MemoryStore store) : MemoryStore
    {
        public override string Name => store.Name;
        public override string? Description => store.Description;
        public override int? MaxSearchResults => store.MaxSearchResults;
        public override ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => store.InitializeAsync(agent, cancellationToken);
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => store.SearchAsync(query, maxSearchResults, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool>? completion = null;
        Task task;
        lock (_initializationGate)
        {
            if (_agent is not null && !ReferenceEquals(_agent, agent)) throw new InvalidOperationException("A memory manager binds to one agent. Create a separate manager per conversation.");
            _agent = agent;
            if (_initialization is null)
            { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _initialization = completion.Task; }
            task = _initialization;
        }
        if (completion is not null) _ = InitializeCoreAsync(agent, completion, cancellationToken);
        return new(task);
    }

    private async Task InitializeCoreAsync(Agent agent, TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var store in _stores)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await store.InitializeAsync(agent, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _tools = Array.AsReadOnly(CreateTools().Concat(_stores.SelectMany(store => store.GetTools())).ToArray());
            if (_extractionBindings.Length > 0)
            {
                var coordinator = new ExtractionCoordinator(_extractionBindings, agent.AuxModel, _telemetry);
                _coordinator = coordinator;
                _extractionTriggers.Add(agent.Hooks.Register<MessageAddedEvent>((value, _) => { coordinator.Record(value.Message); return default; }));
                foreach (var binding in _extractionBindings)
                    foreach (var trigger in binding.Options.Triggers)
                    {
                        Action fire = trigger is InvocationTrigger or IntervalTrigger
                            ? () => coordinator.Process(binding, agent.MemoryInvocationTraceContext)
                            : () => coordinator.Process(binding);
                        _extractionTriggers.Add(trigger.Attach(agent, fire) ?? throw new InvalidOperationException("An extraction trigger returned no subscription handle."));
                    }
            }
            if (_options.Injection is { } injection) RegisterInjection(agent, injection);
            agent.Hooks.Register<ShutdownEvent>((_, token) => ShutdownMemoryAsync(token), HookOrder.SdkLast);
            cancellationToken.ThrowIfCancellationRequested();
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException error)
        { foreach (var cleanup in DisposeExtractionSubscriptions()) AttachCleanup(error, cleanup); completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error)
        { foreach (var cleanup in DisposeExtractionSubscriptions()) AttachCleanup(error, cleanup); completion.TrySetException(error); }
    }

    private async ValueTask ShutdownMemoryAsync(CancellationToken cancellationToken)
    {
        // Stop custom timer triggers before draining, so a periodic producer cannot keep the drain open indefinitely.
        var cleanup = DisposeExtractionSubscriptions();
        Exception? failure = null;
        try { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        foreach (var error in cleanup)
        { if (failure is null) failure = error; else AttachCleanup(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private List<Exception> DisposeExtractionSubscriptions()
    {
        var subscriptions = _extractionTriggers.ToArray();
        _extractionTriggers.Clear();
        var errors = new List<Exception>();
        for (var index = subscriptions.Length - 1; index >= 0; index--)
        { try { subscriptions[index].Dispose(); } catch (Exception error) { errors.Add(error); } }
        return errors;
    }

    private static void AttachCleanup(Exception primary, Exception secondary)
    {
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(secondary);
    }
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => _tools;
    /// <summary>Searches selected stores, retaining successful results when another backend fails.</summary>
    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, MemorySearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query, nameof(query));
        if (options?.MaxSearchResults is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "The result limit must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var names = options?.Stores?.ToArray();
        using var operation = _telemetry.Search(query, names ?? _stores.Select(store => store.Name).ToArray(), options?.MaxSearchResults);
        var failures = 0;
        try
        {
            var selected = ResolveStores(names);
            var results = await Task.WhenAll(selected.Select(SearchStoreAsync)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var entries = results.SelectMany(items => items).ToArray();
            operation.Complete(entries, failures);
            return entries;
        }
        catch (Exception error)
        {
            var failure = NormalizeCallerCancellation(error, cancellationToken);
            operation.Complete([], failures, failure);
            if (!ReferenceEquals(error, failure)) throw failure;
            throw;
        }

        async Task<IReadOnlyList<MemoryEntry>> SearchStoreAsync(MemoryStore store)
        {
            try
            {
                var entries = await operation.RunAsync(() => store.SearchAsync(query, options?.MaxSearchResults ?? store.MaxSearchResults ?? 3, cancellationToken)).ConfigureAwait(false);
                if (entries is null || entries.Any(entry => entry is null)) throw new InvalidOperationException("A memory store returned null entries.");
                return entries.Select(entry => new MemoryEntry(entry.Content, store.Name, entry.Metadata)).ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Interlocked.Increment(ref failures);
                try { SdkLogging.Warning(error, "Memory search failed for store '{Store}': {Error}", store.Name, error.Message); }
                catch (Exception) { /* A diagnostic sink cannot turn an isolated backend failure into a retrieval failure. */ }
                return [];
            }
        }
    }
    /// <summary>Writes a fact to all selected writable entry stores and reports every backend failure.</summary>
    public ValueTask AddAsync(string content, MemoryAddOptions? options = null, CancellationToken cancellationToken = default)
        => AddCoreAsync(content, options, false, cancellationToken);

    private async ValueTask AddCoreAsync(string content, MemoryAddOptions? options, bool detached, CancellationToken cancellationToken)
    {
        Guard.NotNull(content, nameof(content));
        if (options?.Metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Memory metadata must be a JSON object.", nameof(options));
        var metadata = options?.Metadata?.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        var names = options?.Stores?.ToArray();
        using var operation = _telemetry.Add(content, names ?? _stores.Where(store => store.Writable && store is IWritableMemoryStore).Select(store => store.Name).ToArray(), detached);
        var failedStores = 0;
        try
        {
            var selected = ResolveStores(names);
            if (names is null) selected = selected.Where(store => store.Writable && store is IWritableMemoryStore).ToArray();
            foreach (var store in selected)
                if (!store.Writable || store is not IWritableMemoryStore) throw new ArgumentException($"Memory store '{store.Name}' cannot accept entry writes.", nameof(options));
            if (selected.Length == 0) throw new ArgumentException("No writable entry stores were selected.", nameof(options));
            var errors = await Task.WhenAll(selected.Select(WriteStoreAsync)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            failedStores = errors.Count(error => error is not null);
            var failures = errors.Where(error => error is not null).SelectMany(error => error is AggregateException aggregate ? (IEnumerable<Exception>)aggregate.Flatten().InnerExceptions : new[] { error! }).ToArray();
            if (failures.Length > 0)
                throw new AggregateException("Memory writes failed for: " + string.Join(", ", selected.Where((_, index) => errors[index] is not null).Select(store => store.Name)) + ".", failures);
            operation.CompleteWrite(0);
        }
        catch (Exception error)
        {
            var failure = NormalizeCallerCancellation(error, cancellationToken);
            operation.CompleteWrite(failedStores, failure);
            if (!ReferenceEquals(error, failure)) throw failure;
            throw;
        }

        async Task<Exception?> WriteStoreAsync(MemoryStore store)
        {
            try
            {
                await operation.RunAsync(async () =>
                {
                    await ((IWritableMemoryStore)store).AddAsync(content, metadata, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return error; }
        }
    }

    private MemoryStore[] ResolveStores(IReadOnlyList<string>? names)
    {
        if (names is null) return _stores;
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<MemoryStore>();
        foreach (var name in names)
        {
            Guard.NotNullOrWhiteSpace(name, nameof(names));
            var store = _stores.FirstOrDefault(store => store.Name == name) ?? throw new ArgumentException($"Unknown memory store: {name}.", nameof(names));
            if (requested.Add(name)) selected.Add(store);
        }
        return selected.ToArray();
    }

    private static Exception NormalizeCallerCancellation(Exception error, CancellationToken cancellationToken)
        => error is OperationCanceledException canceled && cancellationToken.IsCancellationRequested && canceled.CancellationToken != cancellationToken
            ? new OperationCanceledException(canceled.Message, canceled, cancellationToken) : error;
}
