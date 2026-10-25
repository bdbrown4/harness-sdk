using System.Runtime.ExceptionServices;

namespace Strands.Agents.Bidi;

/// <summary>Typed, awaited bidirectional lifecycle callbacks ordered by priority and registration.</summary>
public sealed class BidiHookRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private long _sequence;

    /// <summary>Registers a callback; disposing the returned handle removes only this registration.</summary>
    /// <typeparam name="T">A lifecycle event or its shared interface.</typeparam>
    /// <param name="callback">The awaited callback.</param>
    /// <param name="order">Priority, lower first; stop reverses registration order within each priority.</param>
    /// <returns>An idempotent registration removal handle.</returns>
    public IDisposable Register<T>(Func<T, CancellationToken, ValueTask> callback, int order = 0) where T : class, IBidiHookEvent
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        lock (_gate)
        {
            var registration = new Registration(this, typeof(T), (value, token) => callback((T)value, token), order, ++_sequence);
            _registrations.Add(registration); return registration;
        }
    }

    /// <summary>Awaits a stable callback snapshot. Stop attempts every callback and retains secondary failures.</summary>
    /// <param name="value">The lifecycle notification.</param>
    /// <param name="cancellationToken">The unchanged token forwarded to callbacks.</param>
    public async ValueTask InvokeAsync(IBidiHookEvent value, CancellationToken cancellationToken = default)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        Registration[] callbacks;
        lock (_gate) callbacks = _registrations.Where(registration => registration.Type.IsInstanceOfType(value)).ToArray();
        callbacks = callbacks.OrderBy(registration => registration.Order)
            .ThenBy(registration => value is BidiAgentStopEvent ? -registration.Sequence : registration.Sequence).ToArray();
        Exception? primary = null;
        foreach (var registration in callbacks)
        {
            try { await registration.Callback(value, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (value is BidiAgentStopEvent)
            {
                if (primary is null) primary = error;
                else if (!ReferenceEquals(primary, error))
                {
                    if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
                    errors.Add(error);
                }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private sealed record Registration(BidiHookRegistry Owner, Type Type, Func<IBidiHookEvent, CancellationToken, ValueTask> Callback, int Order, long Sequence) : IDisposable
    {
        public void Dispose() { lock (Owner._gate) Owner._registrations.Remove(this); }
    }
}
