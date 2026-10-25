namespace Strands.Agents.VendedTools;

/// <summary>Application-selected metadata and duration limit for the optional sleep tool.</summary>
public sealed record SleepOptions
{
    /// <summary>The model-facing name, defaulting to sleep.</summary>
    public string Name { get; init; } = "sleep";
    /// <summary>An optional replacement for the generated duration-aware description.</summary>
    public string? Description { get; init; }
    /// <summary>The positive maximum accepted duration, defaulting to sixty seconds.</summary>
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(60);
}
