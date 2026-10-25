namespace Strands.Agents.Sandbox;

/// <summary>Directory-walk failure handling for the source-backed editor profiles.</summary>
public enum SandboxDirectoryErrorPolicy
{
    /// <summary>Skip ordinary filesystem I/O and permission errors, matching the Python walk policy.</summary>
    SkipIoFailures,
    /// <summary>Skip non-cancellation walk errors, matching the TypeScript walk policy within native causal cleanup guarantees.</summary>
    SkipNonCancellationFailures,
}

/// <summary>Application-selected metadata and directory-walk policy for the sandbox file editor.</summary>
public sealed record SandboxFileEditorOptions
{
    /// <summary>The model-facing name. Use fileEditor to select the TypeScript spelling.</summary>
    public string Name { get; init; } = "file_editor";
    /// <summary>An optional replacement for the shared description.</summary>
    public string? Description { get; init; }
    /// <summary>The explicitly selected numeric and optional-input profile. The default preserves native integer behavior.</summary>
    public SandboxFileEditorInputProfile InputProfile { get; init; } = SandboxFileEditorInputProfile.Native;
    /// <summary>The directory-walk error profile. Cancellation and real cleanup diagnostics always propagate.</summary>
    public SandboxDirectoryErrorPolicy DirectoryErrorPolicy { get; init; } = SandboxDirectoryErrorPolicy.SkipIoFailures;
}
