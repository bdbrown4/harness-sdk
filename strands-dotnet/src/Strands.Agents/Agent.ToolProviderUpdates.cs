using System.Runtime.ExceptionServices;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private static readonly AsyncLocal<ProviderUpdateReceipt?> ActiveProviderUpdate = new();
    [ThreadStatic] private static SynchronousProviderUpdateScope? _synchronousProviderUpdate;
    private readonly List<ProviderUpdateSubscription> _providerUpdates = [];
    private bool _providerUpdatesReady;
    private bool _providerUpdatesStopping;

    private async ValueTask InitializeUpdatingProviderAsync(IToolProvider provider, IToolProviderUpdates source, CancellationToken cancellationToken)
    {
        var subscription = new ProviderUpdateSubscription(this, provider);
        lock (_initializationGate) _providerUpdates.Add(subscription);
        try
        {
            var lease = await source.WatchToolsAsync(_toolProviderContext, subscription, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A tool provider update subscription requires a lease.");
            lock (_initializationGate) subscription.Lease = lease;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_initializationGate)
            {
                subscription.Establishing = false;
                if (subscription.InitialError is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                if (subscription.Initial is not { } initial) throw new InvalidOperationException("A tool provider update subscription requires an initial snapshot.");
                CommitProviderUpdate(subscription, initial, initialization: true);
            }
        }
        catch (Exception primary)
        {
            try { await subscription.CloseAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                lock (_initializationGate) subscription.CloseFailureObserved = true;
                AttachProviderUpdateCleanup(primary, cleanup);
            }
            throw;
        }
    }

    private List<ProviderRegistration> PrepareProviderUpdate(ProviderUpdateSubscription owner, IReadOnlyList<ITool> tools)
    {
        Guard.NotNull(tools, nameof(tools));
        var pending = new List<ProviderRegistration>();
        foreach (var tool in tools.ToArray())
        {
            Guard.NotNull(tool, nameof(tools));
            if (_options.ToolFilter?.Invoke(tool) == false) continue;
            var original = tool.Spec ?? throw new InvalidOperationException("A provider tool requires a specification.");
            var spec = new ToolSpec(original.Name, original.Description, original.InputSchema)
            { OutputSchema = original.OutputSchema?.Clone(), Annotations = original.Annotations?.Clone() };
            if (_options.ProviderSpecFilter?.Invoke(tool, spec) == false) continue;
            if (pending.Any(value => value.Spec.Name == spec.Name || ReferenceEquals(value.Tool, tool)))
                throw new InvalidOperationException("A provider snapshot cannot repeat a tool name or capability reference.");
            pending.Add(new(tool, owner.Provider, spec, ToolSchema.Compile(spec.InputSchema), owner));
        }
        ValidateDelegationRegistration(pending.Select(value => value.Tool));
        _backgroundTasks?.Policy.Validate(pending.Select(value => value.Tool).ToArray(), tool => pending.Single(value => ReferenceEquals(value.Tool, tool)).Spec);
        return pending;
    }

    // Pure table work only. Callers hold the admission gate; all borrowed preparation already completed.
    private void CommitProviderUpdate(ProviderUpdateSubscription owner, List<ProviderRegistration> pending, bool initialization = false)
    {
        var previous = Volatile.Read(ref _providerRegistrations);
        var owned = new HashSet<string>(previous.Values.Where(value => ReferenceEquals(value.Updates, owner)).Select(value => value.Spec.Name), StringComparer.Ordinal);
        var next = OrderedRegistrations().Where(value => !owned.Contains(value.Key)).ToList();
        foreach (var entry in pending)
        {
            if (next.Any(value => value.Key == entry.Spec.Name)) throw new InvalidOperationException($"Duplicate tool name: {entry.Spec.Name}.");
            if (next.Any(value => ReferenceEquals(value.Value.Tool, entry.Tool)))
                throw new InvalidOperationException("A provider capability reference cannot belong to a different registration owner.");
            ValidateNormalizedToolName(entry.Spec.Name, next.Select(value => value.Key), initialization);
            next.Add(new(entry.Spec.Name, (entry.Tool, entry.Schema)));
        }
        var registrations = previous.Where(pair => !ReferenceEquals(pair.Value.Updates, owner))
            .ToDictionary(pair => pair.Key, pair => pair.Value, ToolIdentityComparer.Instance);
        foreach (var entry in pending) registrations.Add(entry.Tool, entry);
        _tools.Clear();
        foreach (var entry in next) _tools.Add(entry.Key, entry.Value);
        Volatile.Write(ref _providerRegistrations, registrations);
        Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(next.Select(value => value.Value.Tool).ToArray()));
    }

    private bool CanPublishProviderUpdates() => _providerUpdatesReady && !_providerUpdatesStopping && !_disposed && _shutdown is null &&
        Volatile.Read(ref _invoking) == 0 && _directToolCalls == 0 && !_abortingInterrupts && _classicReductions == 0 && _catalogOperation is null && InterruptState is null;

    private void CompleteProviderUpdatesInitialization(TaskCompletionSource<bool> completion)
    {
        Action? report;
        lock (_initializationGate) { _providerUpdatesReady = true; report = PublishPendingProviderUpdatesUnderGate(); completion.TrySetResult(true); }
        report?.Invoke();
    }

    private void PublishIdleProviderUpdates()
    {
        Action? report;
        lock (_initializationGate) report = PublishPendingProviderUpdatesUnderGate();
        report?.Invoke();
    }

    private Action? PublishPendingProviderUpdatesUnderGate()
    {
        if (!CanPublishProviderUpdates()) return null;
        List<(ProviderUpdateSubscription Owner, Exception Error)> failures = [];
        foreach (var owner in _providerUpdates)
        {
            if (owner.Closing || owner.Terminal || owner.Pending is not { } pending) continue;
            owner.Pending = null;
            try { CommitProviderUpdate(owner, pending); }
            catch (Exception error) { failures.Add((owner, error)); }
        }
        if (failures.Count == 0) return null;
        Action report = () =>
        {
            foreach (var failure in failures)
                try
                {
                    using var scope = new ProviderUpdateReceipt(failure.Owner);
                    using var synchronous = new SynchronousProviderUpdateScope(this);
                    SdkLogging.Warning(failure.Error, "A tool provider update could not be published.");
                }
                finally { failure.Owner.EndActivity(); }
        };
        foreach (var failure in failures) failure.Owner.BeginActivityUnderGate();
        return report;
    }

    private void RefuseProviderUpdateLifecycleReentry()
    {
        for (var scope = _synchronousProviderUpdate; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Agent, this))
                throw new InvalidOperationException("A provider update callback cannot await its consuming agent's lifecycle.");
        for (var scope = ActiveProviderUpdate.Value; scope is not null; scope = scope.Parent)
            if (scope.Active && ReferenceEquals(scope.Owner.Agent, this))
                throw new InvalidOperationException("A provider update callback cannot await its consuming agent's lifecycle.");
    }

    private static void AttachProviderUpdateCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        var errors = primary.Data[key] as List<Exception> ?? [];
        if (errors.Any(value => ReferenceEquals(value, cleanup))) return;
        errors.Add(cleanup); primary.Data[key] = errors;
    }

    private async ValueTask CloseProviderUpdateSubscriptionsAsync(Exception? primary)
    {
        ProviderUpdateSubscription[] owners;
        lock (_initializationGate) { _providerUpdatesStopping = true; owners = _providerUpdates.ToArray(); }
        Exception? failure = null;
        foreach (var owner in owners)
            try { await owner.CloseAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (_initializationGate)
                {
                    if (owner.CloseFailureObserved) continue;
                    owner.CloseFailureObserved = true;
                }
                if (primary is not null) AttachProviderUpdateCleanup(primary, error);
                else if (failure is null) failure = error;
                else AttachProviderUpdateCleanup(failure, error);
            }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ProviderUpdateReceipt : IDisposable
    {
        internal ProviderUpdateSubscription Owner { get; }
        internal ProviderUpdateReceipt? Parent { get; } = ActiveProviderUpdate.Value;
        internal bool Active { get; private set; } = true;
        internal ProviderUpdateReceipt(ProviderUpdateSubscription owner) { Owner = owner; ActiveProviderUpdate.Value = this; }
        public void Dispose() { Active = false; ActiveProviderUpdate.Value = Parent; }
    }

    // Physical synchronous callbacks remain visible when user code restores an older ExecutionContext.
    // This stack never spans an await and therefore cannot leak onto a continuation thread.
    private sealed class SynchronousProviderUpdateScope : IDisposable
    {
        internal Agent Agent { get; }
        internal SynchronousProviderUpdateScope? Parent { get; } = _synchronousProviderUpdate;
        internal SynchronousProviderUpdateScope(Agent agent) { Agent = agent; _synchronousProviderUpdate = this; }
        public void Dispose() => _synchronousProviderUpdate = Parent;
    }

    private sealed class ProviderUpdateSubscription(Agent agent, IToolProvider provider) : IObserver<IReadOnlyList<ITool>>
    {
        internal Agent Agent { get; } = agent;
        internal IToolProvider Provider { get; } = provider;
        internal IAsyncDisposable? Lease;
        internal bool Establishing = true;
        internal bool Closing;
        internal bool Terminal;
        internal bool CloseFailureObserved;
        internal Exception? InitialError;
        internal List<ProviderRegistration>? Initial;
        internal List<ProviderRegistration>? Pending;
        private long _received;
        private long _accepted;
        private int _active;
        private TaskCompletionSource<bool> _drained = CompletedDrain();
        private Task? _close;

        public void OnNext(IReadOnlyList<ITool> value)
        {
            long sequence;
            lock (Agent._initializationGate)
            {
                if (Closing || Terminal || Agent._providerUpdatesStopping) return;
                sequence = ++_received;
                BeginActivityUnderGate();
            }
            using var scope = new ProviderUpdateReceipt(this);
            using var synchronous = new SynchronousProviderUpdateScope(Agent);
            try
            {
                var prepared = Agent.PrepareProviderUpdate(this, value);
                lock (Agent._initializationGate)
                {
                    if (Closing || Terminal || Agent._providerUpdatesStopping || sequence <= _accepted) return;
                    _accepted = sequence;
                    if (Establishing) Initial = prepared; else Pending = prepared;
                }
                Agent.PublishIdleProviderUpdates();
            }
            catch (Exception error)
            {
                bool establishing;
                lock (Agent._initializationGate) { establishing = Establishing; if (establishing) InitialError ??= error; }
                if (establishing) throw;
                SdkLogging.Warning(error, "A tool provider update was rejected.");
            }
            finally { EndActivity(); }
        }

        public void OnError(Exception error)
        {
            Guard.NotNull(error, nameof(error));
            lock (Agent._initializationGate)
            {
                if (Closing || Terminal) return;
                BeginActivityUnderGate();
                Terminal = true; Pending = null;
                if (Establishing) InitialError ??= error;
            }
            try
            {
                using var scope = new ProviderUpdateReceipt(this);
                using var synchronous = new SynchronousProviderUpdateScope(Agent);
                SdkLogging.Warning(error, "A tool provider update stream ended with an error; retaining its published tools.");
            }
            finally { EndActivity(); }
        }

        public void OnCompleted() { lock (Agent._initializationGate) { if (Closing || Terminal) return; Terminal = true; Pending = null; } }

        internal void BeginActivityUnderGate() { if (_active++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void EndActivity() { lock (Agent._initializationGate) if (--_active == 0) _drained.TrySetResult(true); }

        internal Task CloseAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (Agent._initializationGate)
            {
                if (_close is not null) return _close;
                Closing = true; Pending = null;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _close = completion.Task;
            }
            _ = CloseCoreAsync(completion);
            return completion.Task;
        }

        private async Task CloseCoreAsync(TaskCompletionSource<bool> completion)
        {
            Exception? failure = null;
            using var scope = new ProviderUpdateReceipt(this);
            try
            {
                if (Lease is { } lease)
                    try
                    {
                        ValueTask disposal;
                        using (var synchronous = new SynchronousProviderUpdateScope(Agent)) disposal = lease.DisposeAsync();
                        await disposal.ConfigureAwait(false);
                    }
                    catch (Exception error) { failure = error; }
                Task drained; lock (Agent._initializationGate) drained = _drained.Task;
                await drained.ConfigureAwait(false);
                if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
            }
            catch (Exception error) { if (failure is not null) AttachProviderUpdateCleanup(failure, error); completion.TrySetException(failure ?? error); }
        }

        private static TaskCompletionSource<bool> CompletedDrain() { var value = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(true); return value; }
    }
}
