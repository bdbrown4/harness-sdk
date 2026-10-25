namespace Strands.Agents.Injection;

/// <summary>Built-in policies determining when context is rendered.</summary>
public enum InjectionTrigger
{
    /// <summary>The latest message is a user ask containing no tool result.</summary>
    UserTurn,
    /// <summary>Every model call, including tool-result turns.</summary>
    EveryTurn
}

/// <summary>The name and trigger for an ephemeral text injector.</summary>
public sealed record ContextInjectorOptions
{
    /// <summary>The plugin identifier; multiple injectors require distinct names.</summary>
    public string Name { get; init; } = "strands:context-injector";
    /// <summary>The built-in trigger, used when Predicate is absent. Defaults to a fresh user ask.</summary>
    public InjectionTrigger Trigger { get; init; } = InjectionTrigger.UserTurn;
    /// <summary>An optional custom trigger replacing the built-in policy. Exceptions skip injection unless caller cancellation was requested.</summary>
    public Func<InjectionContext, bool>? Predicate { get; init; }
}
