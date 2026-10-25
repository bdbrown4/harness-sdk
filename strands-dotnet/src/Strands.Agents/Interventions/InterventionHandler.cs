namespace Strands.Agents.Interventions;

/// <summary>The policy applied when evaluating a handler or applying its action fails. Interrupts and requested cancellation always propagate.</summary>
public enum InterventionErrorPolicy
{
    /// <summary>Propagates the original error; this is the default.</summary>
    Throw,
    /// <summary>Records a warning and continues to later handlers.</summary>
    Proceed,
    /// <summary>Converts the error to a denial on supported before-events.</summary>
    Deny
}

/// <summary>Overrides the lifecycle policies it needs. Instances are caller-owned and may be shared by children; callbacks must support that concurrency.</summary>
public abstract class InterventionHandler
{
    /// <summary>The unique name used to label guidance and approval requests.</summary>
    public abstract string Name { get; }
    /// <summary>The handler failure policy, defaulting to Throw.</summary>
    public virtual InterventionErrorPolicy OnError => InterventionErrorPolicy.Throw;
    /// <summary>Evaluates an invocation before its new user input is committed.</summary>
    public virtual ValueTask<IInterventionAction> BeforeInvocationAsync(BeforeInvocationEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <summary>Evaluates a tool call before schema validation and execution.</summary>
    public virtual ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <summary>Evaluates or transforms the completed tool result.</summary>
    public virtual ValueTask<IInterventionAction> AfterToolCallAsync(AfterToolCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <summary>Evaluates the finalized model request before invoking middleware or the provider.</summary>
    public virtual ValueTask<IInterventionAction> BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
    /// <summary>Evaluates a completed or failed model attempt. Guidance requests a retry without committing its response.</summary>
    public virtual ValueTask<IInterventionAction> AfterModelCallAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default) => new(new Proceed());
}
