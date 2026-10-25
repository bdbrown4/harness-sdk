using System.Globalization;

namespace Strands.Harness;

/// <summary>Optional command execution capability. Implementations own process authorization and must honor cancellation and execution bounds.</summary>
public interface ICommandSandbox : ISandbox
{
    /// <summary>Runs a command in a fresh shell and returns bounded standard output, standard error, and its exit code.</summary>
    /// <param name="command">The command interpreted by the implementation's shell.</param>
    /// <param name="options">The requested deadline, defaulting to 120 seconds.</param>
    /// <param name="cancellationToken">Cancellation for process execution and cleanup.</param>
    ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Per-command execution bounds.</summary>
public sealed record ShellExecutionOptions
{
    /// <summary>The wall-clock deadline, defaulting to 120 seconds. The implementation may impose a lower configured maximum.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>A requested combined stdout/stderr capture limit. Null uses the sandbox policy; implementations must clamp requests to their configured hard limit.</summary>
    public int? MaxOutputBytes { get; init; }
}

/// <summary>Captured command output. A nonzero exit code represents a completed command whose shell reported failure.</summary>
/// <param name="StandardOutput">Captured UTF-8 standard output.</param>
/// <param name="StandardError">Captured UTF-8 standard error.</param>
/// <param name="ExitCode">The shell's exit code; 124 is used for deadline expiry.</param>
/// <param name="OutputTruncated">Whether the configured combined output limit discarded bytes.</param>
public sealed record SandboxExecutionResult(string StandardOutput, string StandardError, int ExitCode, bool OutputTruncated = false);

/// <summary>A command exceeded its deadline. Captured partial output remains available.</summary>
public sealed class SandboxTimeoutException : TimeoutException
{
    private readonly System.Collections.IDictionary _data = new System.Collections.Hashtable();
    /// <summary>Creates a timeout containing the partial command result, using exit code 124.</summary>
    public SandboxTimeoutException(TimeSpan timeout, SandboxExecutionResult partialResult)
        : this(timeout, partialResult, null) { }
    /// <summary>Creates a timeout retaining its selected transport cause and process-local cleanup diagnostics.</summary>
    public SandboxTimeoutException(TimeSpan timeout, SandboxExecutionResult partialResult, Exception? cause)
        : base("Execution timed out after " + timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds", cause)
    {
        Guard.NotNull(partialResult, nameof(partialResult));
        Timeout = timeout;
        PartialResult = partialResult with { ExitCode = 124 };
    }
    /// <summary>The requested execution deadline.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Output captured before cleanup, with timeout exit code 124.</summary>
    public SandboxExecutionResult PartialResult { get; }
    /// <inheritdoc/>
    public override System.Collections.IDictionary Data => _data;
}

/// <summary>Host shell policy for LocalFileSandbox. It provides bounds, not filesystem, network, or process isolation.</summary>
public sealed record LocalShellOptions
{
    /// <summary>The maximum accepted command duration, defaulting to two minutes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>The combined stdout/stderr byte capture limit, defaulting to 1 MiB. Excess output is drained and discarded.</summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024;
    /// <summary>An absolute working directory for every fresh shell; null uses the host process's current working directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>An absolute compatible shell executable override. Defaults to system Windows PowerShell on Windows and /bin/sh on modern Unix.</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>Environment overrides applied to each shell, copied when the sandbox is constructed.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}
