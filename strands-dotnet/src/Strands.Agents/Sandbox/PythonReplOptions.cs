namespace Strands.Agents.Sandbox;

/// <summary>Application-selected name, description and interpreter for a fresh-process Python tool.</summary>
public sealed record PythonReplOptions
{
    /// <summary>The model-facing name, defaulting to python_repl.</summary>
    public string Name { get; init; } = "python_repl";
    /// <summary>An optional replacement for the shared tool description.</summary>
    public string? Description { get; init; }
    /// <summary>The selected interpreter token, defaulting to python3.</summary>
    public string Language { get; init; } = "python3";
}
