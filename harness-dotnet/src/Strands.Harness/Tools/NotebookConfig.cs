namespace Strands.Harness.Tools;

/// <summary>Notebook input naming, null and range conventions.</summary>
public enum NotebookBehavior
{
    /// <summary>Camel-case keys and optional non-null values.</summary>
    TypeScript = 0,
    /// <summary>Snake-case keys, nullable optional values and exactly two range entries.</summary>
    Python = 1
}

/// <summary>Configuration for an optional application-state notebook tool.</summary>
public sealed record NotebookConfig
{
    /// <summary>The registered tool name.</summary>
    public string Name { get; init; } = "notebook";
    /// <summary>A custom description; null selects the source-profile description.</summary>
    public string? Description { get; init; }
    /// <summary>The maximum UTF8 bytes in one notebook; defaults to one MiB.</summary>
    public int MaximumNotebookSizeBytes { get; init; } = 1_048_576;
    /// <summary>The input profile. TypeScript is the default.</summary>
    public NotebookBehavior Behavior { get; init; }
}
