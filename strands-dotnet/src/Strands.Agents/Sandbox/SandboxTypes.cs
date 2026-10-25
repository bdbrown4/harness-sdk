using System.Collections;

namespace Strands.Agents.Sandbox;

/// <summary>The pipe producing a command output chunk.</summary>
public enum StreamType
{
    /// <summary>Standard output.</summary>
    Stdout,
    /// <summary>Standard error.</summary>
    Stderr,
}

/// <summary>An incremental command update or its terminal result.</summary>
public abstract record SandboxStreamEvent;
/// <summary>Incrementally decoded UTF-8 command output.</summary>
public sealed record StreamChunk(string Data, StreamType StreamType) : SandboxStreamEvent;
/// <summary>A completed command. Nonzero exit codes remain ordinary results.</summary>
public sealed record ExecutionResult(int ExitCode, string Stdout, string Stderr) : SandboxStreamEvent
{
    /// <summary>Whether the configured combined capture bound discarded bytes.</summary>
    public bool OutputTruncated { get; init; }
    private static readonly IReadOnlyList<OutputFile> EmptyOutputFiles = Array.AsReadOnly(Array.Empty<OutputFile>());
    private readonly IReadOnlyList<OutputFile> _outputFiles = EmptyOutputFiles;
    /// <summary>Detached ordered artifact membership, empty for ordinary shell results.</summary>
    public IReadOnlyList<OutputFile> OutputFiles
    {
        get => _outputFiles;
        init
        {
            Guard.NotNull(value, nameof(OutputFiles));
            var copy = value.ToArray();
            if (copy.Any(file => file is null)) throw new ArgumentException("Output files cannot contain null entries.", nameof(OutputFiles));
            _outputFiles = copy.Length == 0 ? EmptyOutputFiles : Array.AsReadOnly(copy);
        }
    }
}

/// <summary>One command's target environment and requested bounds.</summary>
public sealed record ExecuteOptions
{
    /// <summary>An optional positive wall-clock deadline. Null leaves the command subject only to the application process policy.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>The target working directory, overriding the sandbox default.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Target environment overrides, validated as POSIX variable names.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>A requested combined output limit, clamped to the application process policy.</summary>
    public int? MaximumOutputBytes { get; init; }
    /// <summary>Owned per-call standard input bytes; implementations take a detached copy before starting the client.</summary>
    public byte[]? StandardInput { get; init; }
}

/// <summary>Application authority and limits for the local SSH or Docker client process.</summary>
public sealed record SandboxProcessOptions
{
    /// <summary>An absolute client executable override. Null uses the implementation's fixed ssh or docker command through the host PATH.</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>Optional hard maximum duration. Null permits commands without a deadline.</summary>
    public TimeSpan? MaximumDuration { get; init; }
    /// <summary>The hard combined capture limit, defaulting to 32 MiB. File operations reject truncated transport output.</summary>
    public int MaximumOutputBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum bounded local cleanup wait, defaulting to five seconds.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Host client environment overrides. These are application configuration and are separate from the command's remote environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>A command exceeded its deadline. Partial output remains available.</summary>
public sealed class SandboxTimeoutException(TimeSpan timeout, ExecutionResult partialResult, Exception? cause = null) : TimeoutException("Sandbox execution exceeded its deadline.", cause)
{
    private string _diagnostics = string.Empty;
    internal void AppendDiagnostic(string message)
    {
        string observed;
        do { observed = Volatile.Read(ref _diagnostics); }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _diagnostics, observed + "\n" + message, observed), observed));
    }
    /// <inheritdoc/>
    public override string Message => base.Message + Volatile.Read(ref _diagnostics);
    private readonly IDictionary _data = new Hashtable();
    /// <summary>The configured command deadline.</summary>
    public TimeSpan Timeout { get; } = timeout;
    /// <summary>Output captured before local client cleanup.</summary>
    public ExecutionResult PartialResult { get; } = partialResult;
    /// <inheritdoc/>
    public override IDictionary Data => _data;
}

/// <summary>A native client transport or bounded-cleanup failure. Deferred cleanup, when required, is exposed as a nonfaulting Task&lt;IReadOnlyList&lt;Exception&gt;&gt; under Data["Strands.CleanupTask"].</summary>
/// <remarks>Cleanup diagnostics are process-local and do not promise binary exception serialization.</remarks>
public sealed class SandboxExecutionException(string message, Exception? cause = null) : IOException(message, cause)
{
    private readonly IDictionary _data = new Hashtable();
    /// <inheritdoc/>
    public override IDictionary Data => _data;
}

/// <summary>Caller cancellation stopped the local client. Remote orphan cleanup is transport-dependent.</summary>
public sealed class SandboxAbortException(CancellationToken cancellationToken, ExecutionResult partialResult, Exception? cause = null) : OperationCanceledException("Sandbox execution was cancelled.", cause, cancellationToken)
{
    private readonly IDictionary _data = new Hashtable();
    /// <summary>Output captured before local client cleanup.</summary>
    public ExecutionResult PartialResult { get; } = partialResult;
    /// <inheritdoc/>
    public override IDictionary Data => _data;
}
