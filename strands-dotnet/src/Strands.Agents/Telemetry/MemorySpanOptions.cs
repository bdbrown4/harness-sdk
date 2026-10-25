using System.Diagnostics;
using Strands.Agents.Memory;

namespace Strands.Agents.Telemetry;

/// <summary>Inputs for a manual memory search activity. Memory payloads are recorded verbatim.</summary>
/// <param name="Query">The search query.</param>
/// <param name="StoreNames">The requested store names, copied when the activity starts.</param>
public sealed record StartMemorySearchSpanOptions(string Query, IReadOnlyList<string> StoreNames)
{
    /// <summary>An optional result limit recorded without applying backend policy.</summary>
    public int? MaxSearchResults { get; init; }
    /// <summary>The source helper policy. Defaults to TypeScript.</summary>
    public MemoryTelemetryProfile Profile { get; init; }
    /// <summary>An explicit parent, supported by the Python profile.</summary>
    public TraceSpan? ParentSpan { get; init; }
    /// <summary>Python call attributes applied after operation attributes. Arrays are copied.</summary>
    public IReadOnlyDictionary<string, object?>? CustomTraceAttributes { get; init; }
    /// <summary>Python forwarded attributes. Only string, Boolean, and numeric scalar values are retained.</summary>
    public IReadOnlyDictionary<string, object?>? ExtraAttributes { get; init; }
}

/// <summary>Results for a manual memory search activity.</summary>
public sealed record EndMemorySearchSpanOptions
{
    /// <summary>The retrieved entries. Null records an empty result collection.</summary>
    public IReadOnlyList<MemoryEntry>? Entries { get; init; }
    /// <summary>The number of isolated store failures. A nonzero count alone does not mark the activity as failed.</summary>
    public int StoreFailureCount { get; init; }
    /// <summary>An operation failure that marks the activity as failed and suppresses the result payload.</summary>
    public Exception? Error { get; init; }
}

/// <summary>Inputs for a manual memory write activity. Memory payloads are recorded verbatim.</summary>
/// <param name="Content">The fact written to memory.</param>
/// <param name="StoreNames">The requested store names, copied when the activity starts.</param>
public sealed record StartMemoryAddSpanOptions(string Content, IReadOnlyList<string> StoreNames)
{
    /// <summary>Whether to start an independent root rather than inherit a parent.</summary>
    public bool ForceRoot { get; init; }
    /// <summary>The source helper policy. Defaults to TypeScript.</summary>
    public MemoryTelemetryProfile Profile { get; init; }
    /// <summary>An explicit parent, supported by the Python profile and ignored when ForceRoot is true.</summary>
    public TraceSpan? ParentSpan { get; init; }
    /// <summary>Python call attributes applied after operation attributes. Arrays are copied.</summary>
    public IReadOnlyDictionary<string, object?>? CustomTraceAttributes { get; init; }
    /// <summary>Python forwarded attributes. Only string, Boolean, and numeric scalar values are retained.</summary>
    public IReadOnlyDictionary<string, object?>? ExtraAttributes { get; init; }
}

/// <summary>Results for a manual memory write activity.</summary>
public sealed record EndMemoryAddSpanOptions
{
    /// <summary>The number of failed write destinations.</summary>
    public int StoreFailureCount { get; init; }
    /// <summary>An operation failure that marks the activity as failed.</summary>
    public Exception? Error { get; init; }
}

/// <summary>Inputs for a manual memory injection activity.</summary>
public sealed record StartMemoryInjectSpanOptions
{
    /// <summary>The optional entry limit. An absent limit is not emitted as zero.</summary>
    public int? MaxEntries { get; init; }
    /// <summary>The source helper policy. Defaults to TypeScript.</summary>
    public MemoryTelemetryProfile Profile { get; init; }
    /// <summary>An explicit parent, supported by the Python profile.</summary>
    public TraceSpan? ParentSpan { get; init; }
    /// <summary>Python call attributes applied after operation attributes. Arrays are copied.</summary>
    public IReadOnlyDictionary<string, object?>? CustomTraceAttributes { get; init; }
    /// <summary>Python forwarded attributes. Only string, Boolean, and numeric scalar values are retained.</summary>
    public IReadOnlyDictionary<string, object?>? ExtraAttributes { get; init; }
}

/// <summary>Results for a manual fail-open memory injection activity.</summary>
/// <param name="Injected">Whether the renderer produced injection content.</param>
public sealed record EndMemoryInjectSpanOptions(bool Injected)
{
    /// <summary>The number of retrieved entries represented by the renderer.</summary>
    public int EntryCount { get; init; }
    /// <summary>Whether rendering failed. The activity still completes successfully.</summary>
    public bool FormatError { get; init; }
}

/// <summary>Inputs for a manual detached memory extraction activity.</summary>
/// <param name="StoreName">The extraction destination.</param>
/// <param name="MessageCount">The message count after content filtering.</param>
public sealed record StartMemoryExtractSpanOptions(string StoreName, int MessageCount)
{
    /// <summary>The number of messages entirely removed by content filtering.</summary>
    public int FilteredCount { get; init; }
    /// <summary>The extractor name, omitted for direct message storage.</summary>
    public string? Extractor { get; init; }
    /// <summary>An optional valid scheduling context attached as a link and correlation attributes.</summary>
    public ActivityContext? AgentSpanContext { get; init; }
    /// <summary>The source helper policy. Defaults to TypeScript.</summary>
    public MemoryTelemetryProfile Profile { get; init; }
    /// <summary>Python forwarded attributes. Only string, Boolean, and numeric scalar values are retained.</summary>
    public IReadOnlyDictionary<string, object?>? ExtraAttributes { get; init; }
}

/// <summary>Results for a manual detached memory extraction activity.</summary>
public sealed record EndMemoryExtractSpanOptions
{
    /// <summary>The saved entry count, omitted when unknown.</summary>
    public int? EntryCount { get; init; }
    /// <summary>An operation failure that marks the activity as failed.</summary>
    public Exception? Error { get; init; }
}
