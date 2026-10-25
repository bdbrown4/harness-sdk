using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private readonly ToolProviderContext _toolProviderContext;
    private readonly List<IToolProvider> _providerConsumers = [];
    private IReadOnlyDictionary<ITool, ProviderRegistration> _providerRegistrations = new Dictionary<ITool, ProviderRegistration>(ToolIdentityComparer.Instance);

    private async ValueTask InitializeToolProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _options.ToolProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Track before application code: even a failed admission may have acquired a resource.
            _providerConsumers.Add(provider);
            await provider.AddConsumerAsync(_toolProviderContext, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.EnableToolProviderUpdates && provider is IToolProviderUpdates updating)
            {
                await InitializeUpdatingProviderAsync(provider, updating, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var loaded = await provider.LoadToolsAsync(_toolProviderContext, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A tool provider returned null tools.");
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new List<ProviderRegistration>();
            foreach (var tool in loaded.ToArray())
            {
                Guard.NotNull(tool, nameof(loaded));
                if (_options.ToolFilter?.Invoke(tool) == false) continue;
                var original = tool.Spec ?? throw new InvalidOperationException("A provider tool requires a specification.");
                var spec = new ToolSpec(original.Name, original.Description, original.InputSchema)
                { OutputSchema = original.OutputSchema?.Clone(), Annotations = original.Annotations?.Clone() };
                if (_options.ProviderSpecFilter?.Invoke(tool, spec) == false) continue;
                if (_tools.ContainsKey(spec.Name) || pending.Any(value => value.Spec.Name == spec.Name))
                    throw new InvalidOperationException($"Duplicate tool name: {spec.Name}.");
                ValidateNormalizedToolName(spec.Name, _tools.Keys.Concat(pending.Select(value => value.Spec.Name)), initialization: true);
                pending.Add(new(tool, provider, spec, ToolSchema.Compile(spec.InputSchema)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var registrations = Volatile.Read(ref _providerRegistrations).ToDictionary(pair => pair.Key, pair => pair.Value, ToolIdentityComparer.Instance);
            foreach (var entry in pending) registrations.Add(entry.Tool, entry);
            foreach (var entry in pending) _tools.Add(entry.Spec.Name, (entry.Tool, entry.Schema));
            Volatile.Write(ref _providerRegistrations, registrations);
            Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(Tools.Concat(pending.Select(value => value.Tool)).ToArray()));
        }
    }

    private async ValueTask ReleaseToolProviderConsumersAsync(Exception? updatePrimary = null)
    {
        // The agent's memoized lifecycle gates serialize this with initialization and shutdown.
        // Clear before callbacks so a later Dispose cannot repeat a failed removal.
        var consumers = _providerConsumers.ToArray();
        _providerConsumers.Clear();
        Exception? failure = null;
        try { await CloseProviderUpdateSubscriptionsAsync(updatePrimary).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        foreach (var provider in consumers)
        {
            try { await provider.RemoveConsumerAsync(_toolProviderContext, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { if (failure is null) failure = error; else AttachCleanupError(failure, error); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void RollbackToolProviderPublications()
    {
        var registrations = Volatile.Read(ref _providerRegistrations);
        foreach (var entry in registrations.Values)
            if (_tools.TryGetValue(entry.Spec.Name, out var current) && ReferenceEquals(current.Tool, entry.Tool)) _tools.Remove(entry.Spec.Name);
        Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(Tools.Where(tool => !registrations.ContainsKey(tool)).ToArray()));
        Volatile.Write(ref _providerRegistrations, new Dictionary<ITool, ProviderRegistration>(ToolIdentityComparer.Instance));
    }

    // Provider metadata is fixed for this consumer. Hook-selected unregistered tools retain existing behavior.
    internal ToolSpec RegisteredToolSpec(ITool tool) => Volatile.Read(ref _providerRegistrations).TryGetValue(tool, out var entry) ? entry.Spec : tool.Spec;
    private JsonSchema RegisteredToolSchema(ITool tool) => Volatile.Read(ref _providerRegistrations).TryGetValue(tool, out var entry) ? entry.Schema : ToolSchema.Compile(tool.Spec.InputSchema);
    internal IToolProvider? RegisteredToolProvider(ITool tool) => Volatile.Read(ref _providerRegistrations).TryGetValue(tool, out var entry) ? entry.Provider : null;
    private sealed record ProviderRegistration(ITool Tool, IToolProvider Provider, ToolSpec Spec, JsonSchema Schema, ProviderUpdateSubscription? Updates = null);
    private sealed class ToolIdentityComparer : IEqualityComparer<ITool>
    {
        internal static readonly ToolIdentityComparer Instance = new();
        public bool Equals(ITool? x, ITool? y) => ReferenceEquals(x, y);
        public int GetHashCode(ITool value) => RuntimeHelpers.GetHashCode(value);
    }
}
