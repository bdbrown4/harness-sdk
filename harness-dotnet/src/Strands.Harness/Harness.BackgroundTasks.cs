using Strands.Agents.Tasks;

namespace Strands.Harness;

internal static class HarnessBackgroundTasks
{
    /// <summary>Forces accepted delegation tools to run in the background without inspecting denied registrations.</summary>
    internal static BackgroundTasksConfig Resolve(IReadOnlyList<Strands.Agents.ITool> tools, BackgroundTasksConfig configured, Func<Strands.Agents.ITool, Strands.Agents.ToolSpec> resolveSpec)
    {
        var forced = new HashSet<string>(tools.Select(resolveSpec).Where(spec => spec.Name == "subagent").Select(spec => spec.Name), StringComparer.Ordinal);
        return ResolveNames(configured, forced);
    }

    internal static void Validate(BackgroundTasksConfig? configured)
    {
        if (configured is null) return;
        // Accepted delegation names are known only after the registration predicate runs.
        ResolveNames(configured, new(new[] { "subagent" }, StringComparer.Ordinal)).Validate();
    }

    private static BackgroundTasksConfig ResolveNames(BackgroundTasksConfig configured, HashSet<string> forced)
    {
        if (forced.Count == 0) return configured;
        return configured with
        {
            Agentic = configured.Agentic?.Where(name => !forced.Contains(name)).ToArray(),
            Always = (configured.Always ?? throw new ArgumentException("Always cannot be null.")).Where(name => !forced.Contains(name)).Concat(forced).ToArray(),
            Never = (configured.Never ?? throw new ArgumentException("Never cannot be null.")).Where(name => !forced.Contains(name)).ToArray(),
        };
    }
}
