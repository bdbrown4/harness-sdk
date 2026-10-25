using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Interventions.Steering;

/// <summary>The actions allowed before a tool is executed.</summary>
public interface IToolSteeringAction : IInterventionAction;
/// <summary>The actions allowed when evaluating a completed model response.</summary>
public interface IModelSteeringAction : IInterventionAction;

/// <summary>A contextual intervention handler with independent provider subscriptions for every agent.</summary>
public abstract class SteeringHandler : InterventionHandler, IAgentLifecycleObserver, IAgentObservationLifetime
{
    private readonly ISteeringContextProvider[] _providers;
    private readonly ConditionalWeakTable<Agent, Observation> _observations = new();
    /// <summary>Copies the borrowed context providers. Provider subscriptions are created during agent initialization.</summary>
    protected SteeringHandler(SteeringHandlerOptions? options = null)
    {
        var providers = (options ?? new()).ContextProviders;
        Guard.NotNull(providers, nameof(options));
        _providers = providers.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            Guard.NotNull(provider, nameof(options)); Guard.NotNullOrWhiteSpace(provider.Name, nameof(options));
            if (!names.Add(provider.Name)) throw new ArgumentException("Steering provider names must be unique within a handler.", nameof(options));
        }
    }
    /// <summary>Creates context subscriptions once for this agent and releases them during terminal shutdown.</summary>
    public ValueTask ObserveAgentAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent)); cancellationToken.ThrowIfCancellationRequested();
        var observation = _observations.GetValue(agent, _ => new());
        TaskCompletionSource<bool>? completion = null;
        lock (observation)
        {
            if (observation.Initialization is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                observation.Initialization = completion.Task;
            }
        }
        if (completion is not null) _ = InitializeAsync(agent, observation, completion, cancellationToken);
        return new(observation.Initialization);
    }
    /// <summary>Returns detached context after observation has completed for the supplied agent.</summary>
    public IReadOnlyList<JsonElement> GetSteeringContext(Agent agent)
    {
        Guard.NotNull(agent, nameof(agent));
        if (!_observations.TryGetValue(agent, out var observation) || observation.Initialization?.Status != TaskStatus.RanToCompletion || observation.Bindings is not { } group)
            throw new InvalidOperationException("Steering context requires completed observation for this agent.");
        return group.Snapshot();
    }
    ValueTask IAgentObservationLifetime.RollbackObservationAsync(Agent agent)
        => _observations.TryGetValue(agent, out var observation) && observation.Bindings is { } bindings ? bindings.DisposeAsync() : default;
    /// <summary>Evaluates a tool using agent-local context. The default permits execution.</summary>
    public virtual ValueTask<IToolSteeringAction> SteerBeforeToolAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <summary>Evaluates model output using agent-local context. The default accepts the response.</summary>
    public virtual ValueTask<IModelSteeringAction> SteerAfterModelAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <inheritdoc />
    public sealed override async ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default)
        => await SteerBeforeToolAsync(value, cancellationToken).ConfigureAwait(false);
    /// <inheritdoc />
    public sealed override async ValueTask<IInterventionAction> AfterModelCallAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default)
        => await SteerAfterModelAsync(value, cancellationToken).ConfigureAwait(false);

    private async Task InitializeAsync(Agent agent, Observation observation, TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        var created = new List<ISteeringContext>();
        try
        {
            foreach (var provider in _providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = await provider.CreateAsync(agent, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Steering provider {provider.Name} returned a null subscription.");
                created.Add(binding);
                _ = SnapshotContext(binding);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var group = new BindingGroup(created.ToArray());
            group.ShutdownRegistration = agent.Hooks.Register<ShutdownEvent>((_, _) => group.DisposeAsync(), HookOrder.SdkLast);
            observation.Bindings = group;
            completion.TrySetResult(true);
        }
        catch (Exception error)
        {
            for (var index = created.Count - 1; index >= 0; index--)
                try { await created[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            completion.TrySetException(error);
        }
    }

    private static JsonElement SnapshotContext(ISteeringContext binding)
    {
        var context = binding.Context;
        if (context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Steering context must be a JSON object with a string type discriminator.", nameof(binding));
        return context.Clone();
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(cleanup);
    }

    private sealed class Observation
    {
        internal Task? Initialization { get; set; }
        internal BindingGroup? Bindings { get; set; }
    }

    private sealed class BindingGroup(ISteeringContext[] bindings) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposal;
        private bool _closing;
        internal IDisposable? ShutdownRegistration { get; set; }
        internal IReadOnlyList<JsonElement> Snapshot()
        {
            lock (_gate)
            {
                if (_closing) throw new ObjectDisposedException(nameof(ISteeringContext));
                return Array.AsReadOnly(bindings.Select(SnapshotContext).ToArray());
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate) { _closing = true; return new(_disposal ??= DisposeCoreAsync()); }
        }
        private async Task DisposeCoreAsync()
        {
            ShutdownRegistration?.Dispose();
            Exception? failure = null;
            for (var index = bindings.Length - 1; index >= 0; index--)
                try { await bindings[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
