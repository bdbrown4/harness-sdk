using System.Runtime.ExceptionServices;

namespace Strands.Agents.MultiAgent;

/// <summary>Awaited orchestration callbacks. Priorities ascend; after-events reverse registration order within each equal priority.</summary>
public sealed class MultiAgentHookRegistry
{
    private readonly RegistrationState _state;
    private readonly MultiAgentHookRegistry? _parent;
    private readonly List<Registration> _registrations = [];
    /// <summary>Creates a caller-owned callback registry.</summary>
    public MultiAgentHookRegistry() => _state = new();

    internal MultiAgentHookRegistry(MultiAgentHookRegistry parent)
    {
        _parent = parent;
        _state = parent._state;
    }

    /// <summary>Registers a callback and returns a handle that removes it. Concurrent node events may invoke callbacks concurrently.</summary>
    public IDisposable Register<T>(Func<T, CancellationToken, ValueTask> callback, int priority = 0) where T : class, IMultiAgentStreamEvent
    {
        Guard.NotNull(callback, nameof(callback));
        lock (_state.Gate)
        {
            var registration = new Registration(typeof(T), (value, token) => callback((T)value, token), priority, _state.Sequence++);
            _registrations.Add(registration);
            return new Subscription(this, registration);
        }
    }
    internal IDisposable RegisterSessionInitialized(Func<MultiAgentInitializedEvent, CancellationToken, ValueTask> callback, bool beforeCaller)
    {
        lock (_state.Gate)
        {
            var registration = new Registration(typeof(MultiAgentInitializedEvent), (value, token) => callback((MultiAgentInitializedEvent)value, token), 0, _state.Sequence++, beforeCaller);
            _registrations.Add(registration);
            return new Subscription(this, registration);
        }
    }
    internal async ValueTask InvokeAsync(IMultiAgentStreamEvent value, CancellationToken cancellationToken)
    {
        var after = value is AfterNodeCallEvent or AfterMultiAgentInvocationEvent;
        Registration[] registrations;
        lock (_state.Gate)
        {
            var captured = new List<Registration>();
            AddSnapshot(captured);
            registrations = captured.ToArray();
        }
        registrations = registrations.Where(item => item.Type.IsInstanceOfType(value)).OrderBy(item => item.Priority).ThenBy(item => item.InitializedSessionFirst ? 0 : 1).ThenBy(item => after ? -item.Sequence : item.Sequence).ToArray();
        Exception? primary = null;
        var interruptions = new List<Interrupt>();
        foreach (var registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await registration.Callback(value, cancellationToken).ConfigureAwait(false); }
            catch (InterruptException interrupted) when (value is BeforeNodeCallEvent)
            { interruptions.AddRange(interrupted.Interrupts); }
            catch (Exception error)
            {
                if (!after) throw;
                if (primary is null) primary = error;
                else if (primary is { } existing && !ReferenceEquals(existing, error))
                {
                    if (existing.Data["Strands.CleanupErrors"] is not List<Exception> errors) existing.Data["Strands.CleanupErrors"] = errors = [];
                    errors.Add(error);
                }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (interruptions.Count > 0) throw new InterruptException(NodeResult.CopyInterrupts(interruptions));
    }
    private void AddSnapshot(List<Registration> captured)
    {
        _parent?.AddSnapshot(captured);
        captured.AddRange(_registrations);
    }

    private sealed class RegistrationState
    {
        internal object Gate { get; } = new();
        internal long Sequence;
    }

    private sealed record Registration(Type Type, Func<IMultiAgentStreamEvent, CancellationToken, ValueTask> Callback, int Priority, long Sequence, bool InitializedSessionFirst = false);
    private sealed class Subscription(MultiAgentHookRegistry owner, Registration registration) : IDisposable
    {
        public void Dispose() { lock (owner._state.Gate) owner._registrations.Remove(registration); }
    }
}
