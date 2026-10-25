
namespace Strands.Agents.Context;

/// <summary>Selects content for an offload policy.</summary>
public sealed class OffloadTarget
{
    private readonly HashSet<string>? _include;
    private readonly HashSet<string>? _exclude;
    private OffloadTarget(TargetKind kind, HashSet<string>? include = null, HashSet<string>? exclude = null) { Kind = kind; _include = include; _exclude = exclude; }
    internal TargetKind Kind { get; }
    /// <summary>All reducible text, tool results, and media.</summary>
    public static OffloadTarget All { get; } = new(TargetKind.All);
    /// <summary>Successful tool results.</summary>
    public static OffloadTarget ToolResults { get; } = new(TargetKind.Results);
    /// <summary>Failed tool results.</summary>
    public static OffloadTarget ToolResultErrors { get; } = new(TargetKind.Errors);
    /// <summary>Ordinary assistant text blocks.</summary>
    public static OffloadTarget AssistantText { get; } = new(TargetKind.AssistantText);
    /// <summary>Ordinary user text blocks, excluding tool results.</summary>
    public static OffloadTarget UserText { get; } = new(TargetKind.UserText);
    /// <summary>Selects results by the tool name in the originating assistant request. A tool:: filter prefix is optional; ! excludes. When mixed, includes win and exclusions are ignored with a warning.</summary>
    /// <param name="tools">One or more case-sensitive tool names or filters.</param>
    /// <returns>An immutable tool-name target.</returns>
    public static OffloadTarget ForTools(params string[] tools)
    {
        Guard.NotNull(tools, nameof(tools));
        if (tools.Length == 0) throw new ArgumentException("A tool target must contain at least one filter.", nameof(tools));
        var include = new HashSet<string>(StringComparer.Ordinal);
        var exclude = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tools)
        {
            Guard.NotNullOrWhiteSpace(entry, nameof(tools));
            var excluded = entry[0] == '!';
            var name = excluded ? entry.Substring(1) : entry;
            if (name.StartsWith("tool::", StringComparison.Ordinal)) name = name.Substring(6);
            Guard.NotNullOrWhiteSpace(name, nameof(tools));
            (excluded ? exclude : include).Add(name);
        }
        if (include.Count > 0 && exclude.Count > 0) SdkLogging.Warning("An offload tool filter contains includes and excludes; exclusions are ignored.");
        return new(TargetKind.Tools, include.Count == 0 ? null : include, include.Count > 0 || exclude.Count == 0 ? null : exclude);
    }
    internal bool MatchesTool(ToolResultBlock result, string? name) => Kind switch
    {
        TargetKind.All => true,
        TargetKind.Results => result.Status == ToolResultStatus.Success,
        TargetKind.Errors => result.Status == ToolResultStatus.Error,
        TargetKind.Tools => name is not null && (_include?.Contains(name) ?? !(_exclude?.Contains(name) ?? true)),
        _ => false
    };
    internal bool MatchesText(Role role) => Kind == TargetKind.All || Kind == TargetKind.AssistantText && role == Role.Assistant || Kind == TargetKind.UserText && role == Role.User;
    internal enum TargetKind { All, Results, Errors, AssistantText, UserText, Tools }
}

/// <summary>Controls per-block thresholds or utilization-triggered batch reduction.</summary>
public sealed record OffloadConditions
{
    /// <summary>Reduce only blocks larger than this estimated token count.</summary>
    public long? Threshold { get; init; }
    /// <summary>Reduce matching message batches at or above this context ratio; provider overflow also triggers the policy.</summary>
    public double? Utilization { get; init; }
    /// <summary>Preserve this many latest matching messages, rounding counts down; a value between zero and one preserves that fraction, rounded up.</summary>
    public double PreserveRecent { get; init; }
}

/// <summary>Creates immutable policies that can be shared across agents.</summary>
public static class Offload
{
    /// <summary>Replaces oversized display content with a textual preview.</summary>
    public static OffloadStrategy Truncate(OffloadTarget? target = null, TruncateOptions? options = null) => new(OffloadKind.Truncate, target ?? OffloadTarget.All, options ?? new(), new(), new());
    /// <summary>Replaces matching blocks with a drop marker, or removes complete matching message batches under utilization conditions.</summary>
    public static OffloadStrategy Drop(OffloadTarget? target = null) => new(OffloadKind.Drop, target ?? OffloadTarget.All, new(), new(), new());
    /// <summary>Replaces matching content with an auxiliary model's factual summary.</summary>
    public static OffloadStrategy Summarize(OffloadTarget? target = null, SummarizeOptions? options = null) => new(OffloadKind.Summarize, target ?? OffloadTarget.All, new(), options ?? new(), new());
}

internal enum OffloadKind { Truncate, Drop, Summarize }
