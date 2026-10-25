namespace Strands.Agents.Memory;

/// <summary>Schedules background extraction without blocking the agent loop.</summary>
public abstract class ExtractionTrigger
{
    /// <summary>The stable trigger name used in diagnostics.</summary>
    public abstract string Name { get; }
    /// <summary>Attaches a separate subscription and returns a handle that detaches it.</summary>
    public abstract IDisposable Attach(Agent agent, Action fire);
}

/// <summary>Schedules extraction after every invocation attempt, including failures.</summary>
public sealed class InvocationTrigger : ExtractionTrigger
{
    /// <inheritdoc />
    public override string Name => "invocation";
    /// <inheritdoc />
    public override IDisposable Attach(Agent agent, Action fire)
    {
        Guard.NotNull(agent, nameof(agent));
        Guard.NotNull(fire, nameof(fire));
        return agent.Hooks.Register<AfterInvocationEvent>((_, _) => { fire(); return default; }, HookOrder.SdkLast);
    }
}

/// <summary>Schedules extraction every fixed number of invocation attempts, with independent counters per attachment.</summary>
public sealed class IntervalTrigger : ExtractionTrigger
{
    /// <summary>Creates a trigger with a positive interval.</summary>
    public IntervalTrigger(int interval = 5)
    {
        if (interval < 1) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
    }
    /// <summary>The number of invocation attempts between scheduled saves.</summary>
    public int Interval { get; }
    /// <inheritdoc />
    public override string Name => "interval";
    /// <inheritdoc />
    public override IDisposable Attach(Agent agent, Action fire)
    {
        Guard.NotNull(agent, nameof(agent));
        Guard.NotNull(fire, nameof(fire));
        var turns = 0;
        return agent.Hooks.Register<AfterInvocationEvent>((_, _) => { if (++turns % Interval == 0) fire(); return default; }, HookOrder.SdkLast);
    }
}
