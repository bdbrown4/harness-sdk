using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Interventions;

/// <summary>Bridges ordered policy handlers to awaited hooks with input priority 90 and output priority -90.</summary>
public sealed class InterventionRegistry : IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private int _disposed;
    /// <summary>Validates and copies the complete handler list before registering any callbacks.</summary>
    public InterventionRegistry(IReadOnlyList<InterventionHandler> handlers, HookRegistry hooks)
    {
        Guard.NotNull(handlers, nameof(handlers)); Guard.NotNull(hooks, nameof(hooks));
        var snapshot = handlers.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handler in snapshot)
        {
            Guard.NotNull(handler, nameof(handlers)); Guard.NotNullOrWhiteSpace(handler.Name, nameof(handlers));
            if (!names.Add(handler.Name)) throw new ArgumentException("Intervention handler names must be unique.", nameof(handlers));
            if (!Enum.IsDefined(typeof(InterventionErrorPolicy), handler.OnError)) throw new ArgumentException("An intervention error policy is invalid.", nameof(handlers));
        }
        Handlers = Array.AsReadOnly(snapshot);
        Register<BeforeInvocationEvent>(hooks, nameof(InterventionHandler.BeforeInvocationAsync), (handler, value, token) => handler.BeforeInvocationAsync(value, token), HookOrder.InterventionInput);
        Register<BeforeToolCallEvent>(hooks, nameof(InterventionHandler.BeforeToolCallAsync), (handler, value, token) => handler.BeforeToolCallAsync(value, token), HookOrder.InterventionInput);
        Register<BeforeModelCallEvent>(hooks, nameof(InterventionHandler.BeforeModelCallAsync), (handler, value, token) => handler.BeforeModelCallAsync(value, token), HookOrder.InterventionInput);
        Register<AfterToolCallEvent>(hooks, nameof(InterventionHandler.AfterToolCallAsync), (handler, value, token) => handler.AfterToolCallAsync(value, token), HookOrder.InterventionOutput);
        Register<AfterModelCallEvent>(hooks, nameof(InterventionHandler.AfterModelCallAsync), (handler, value, token) => handler.AfterModelCallAsync(value, token), HookOrder.InterventionOutput);
    }
    /// <summary>The immutable registration-order snapshot of caller-owned handlers.</summary>
    public IReadOnlyList<InterventionHandler> Handlers { get; }
    /// <summary>Removes only callbacks owned by this registry. Handler instances remain caller-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var registration in _registrations) registration.Dispose();
    }

    private void Register<T>(HookRegistry hooks, string methodName,
        Func<InterventionHandler, T, CancellationToken, ValueTask<IInterventionAction>> evaluate, int order) where T : HookEvent
    {
        var handlers = Handlers.Where(handler =>
        {
            var method = handler.GetType().GetMethod(methodName, [typeof(T), typeof(CancellationToken)]);
            return method is not null && method.DeclaringType != typeof(InterventionHandler) && method.GetBaseDefinition().DeclaringType == typeof(InterventionHandler);
        }).ToArray();
        if (handlers.Length > 0)
            _registrations.Add(hooks.Register<T>((value, token) => DispatchAsync(handlers, value, handler => evaluate(handler, value, token), token), order));
    }

    private static async ValueTask DispatchAsync(IReadOnlyList<InterventionHandler> handlers, HookEvent value,
        Func<InterventionHandler, ValueTask<IInterventionAction>> evaluate, CancellationToken cancellationToken)
    {
        var guides = new List<string>();
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IInterventionAction? action;
            try { action = await evaluate(handler).ConfigureAwait(false); }
            catch (InterruptException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { action = HandleError(handler, error); if (action is null) continue; }
            if (action is null) throw new InvalidOperationException($"Intervention handler {handler.Name} returned null.");
            if (action is Guide guide)
            {
                guides.Add($"[{handler.Name}] {guide.Feedback}");
                continue;
            }
            try { if (await ApplyAsync(value, action, handler.Name, cancellationToken).ConfigureAwait(false)) return; }
            catch (InterruptException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                var replacement = HandleError(handler, error);
                if (replacement is not null && await ApplyAsync(value, replacement, handler.Name, cancellationToken).ConfigureAwait(false)) return;
            }
        }
        if (guides.Count > 0) await ApplyAsync(value, new Guide(string.Join("\n", guides)), "", cancellationToken).ConfigureAwait(false);
    }

    private static IInterventionAction? HandleError(InterventionHandler handler, Exception error)
    {
        if (handler.OnError is not (InterventionErrorPolicy.Proceed or InterventionErrorPolicy.Deny)) ExceptionDispatchInfo.Capture(error).Throw();
        Warn($"Intervention handler {handler.Name} failed with policy {handler.OnError}: {error.Message}");
        return handler.OnError == InterventionErrorPolicy.Deny ? new Deny("Handler threw: " + error.Message) : null;
    }

    private static async ValueTask<bool> ApplyAsync(HookEvent value, IInterventionAction action, string name, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case Proceed: return false;
            case Transform transform:
                await transform.Apply(value, cancellationToken).ConfigureAwait(false);
                return false;
            case Deny deny:
                switch (value)
                {
                    case BeforeInvocationEvent before: before.CancelReason = "DENIED: " + deny.Reason; return true;
                    case BeforeToolCallEvent before: before.CancelReason = "DENIED: " + deny.Reason; return true;
                    case BeforeModelCallEvent before: before.CancelReason = "DENIED: " + deny.Reason; return true;
                }
                break;
            case Confirm confirm when value is BeforeToolCallEvent before:
                var response = before.Interrupt(name, JsonSerializer.SerializeToElement(confirm.Prompt), confirm.Response);
                if (confirm.Evaluate(response.Clone())) return false;
                before.CancelReason = "CONFIRMATION_FAILED: " + confirm.Prompt;
                return true;
            case Guide guide:
                switch (value)
                {
                    case BeforeInvocationEvent before: before.CancelReason = "GUIDANCE: " + guide.Feedback; return false;
                    case BeforeToolCallEvent before: before.CancelReason = "GUIDANCE: " + guide.Feedback; return false;
                    case BeforeModelCallEvent before:
                        var message = await before.Agent.AppendInterventionGuidanceAsync(guide.Feedback, before.InvocationState, cancellationToken).ConfigureAwait(false);
                        before.Request = before.Request with { Messages = before.Request.Messages.Concat([message]).ToArray(), DynamicTrailingBlocks = 0 };
                        return false;
                    case AfterModelCallEvent after:
                        if (after.InvocationCancellationToken.IsCancellationRequested || after.Error is OperationCanceledException) return false;
                        await after.Agent.AppendInterventionGuidanceAsync(guide.Feedback, after.InvocationState, cancellationToken).ConfigureAwait(false);
                        after.Retry = true;
                        return false;
                }
                break;
            default:
                if (action is not Confirm) throw new ArgumentException("An intervention returned an unknown action type.", nameof(action));
                break;
        }
        Warn($"Intervention action {action.GetType().Name} from {name} has no effect on {value.GetType().Name}.");
        return false;
    }

    private static void Warn(string message)
    {
        try { SdkLogging.Warning("{Message}", message); }
        catch (Exception) { /* Diagnostics cannot change a deliberate policy decision or replace its original error. */ }
    }
}
