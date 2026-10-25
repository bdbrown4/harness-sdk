namespace Strands.Harness.Tools;

/// <summary>Application configuration for the optional host Bash tool.</summary>
public sealed record BashConfig
{
    /// <summary>The model-facing tool name.</summary>
    public string Name { get; init; } = "bash";
    /// <summary>A custom description; null selects the built-in description.</summary>
    public string? Description { get; init; }
    /// <summary>An absolute Bash executable path; null resolves bash through the process path.</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>The initial absolute working directory; null uses the application directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Environment overrides copied when the tool is created.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>The combined UTF8 output limit for each command, defaulting to one MiB.</summary>
    public int MaximumOutputBytes { get; init; } = 1_048_576;
    /// <summary>The maximum time spent joining a stopped process and its pipes.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A persistent Bash interpreter or its transport failed.</summary>
public class BashSessionException : Exception
{
    /// <summary>Creates a session failure with an optional original cause.</summary>
    public BashSessionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A Bash command exceeded its execution deadline.</summary>
public sealed class BashTimeoutException : BashSessionException
{
    /// <summary>Creates a timeout failure.</summary>
    public BashTimeoutException(string message) : base(message) { }
    internal BashTimeoutException(string message, Exception cause) : base(message, cause) { }
}
