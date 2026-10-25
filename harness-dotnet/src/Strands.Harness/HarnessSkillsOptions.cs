using Strands.Agents.Skills;

namespace Strands.Harness;

/// <summary>Progressive skill disclosure for the native harness.</summary>
public sealed record HarnessSkillsOptions
{
    /// <summary>Whether skills are enabled. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Explicit native skills, paths, or HTTPS sources. Null scans ./.agent/skills when present; an empty list disables loading.</summary>
    public IReadOnlyList<SkillSource>? Sources { get; init; }
    /// <summary>An optional configured skills plugin used verbatim; a skills plugin in the application plugins takes precedence.</summary>
    public AgentSkills? Plugin { get; init; }
}
