using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
#if NETSTANDARD2_0
using Process = Strands.Agents.Processes.OwnedChildProcess;
#endif

namespace Strands.Harness.Internal;

internal static class HostShellRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

    internal static LocalShellOptions SnapshotOptions(LocalShellOptions options)
    {
        Guard.Positive(options.MaxOutputBytes, nameof(options.MaxOutputBytes));
        if (options.MaxDuration <= TimeSpan.Zero || options.MaxDuration.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options), "MaxDuration must be positive and at most Int32.MaxValue milliseconds.");
        Guard.NotNull(options.Environment, nameof(options.Environment));
        if (options.WorkingDirectory is not null && !Guard.IsFullyQualifiedLocalPath(options.WorkingDirectory)) throw new ArgumentException("WorkingDirectory must be an absolute host path.", nameof(options));
        if (options.ExecutablePath is not null && !Guard.IsFullyQualifiedLocalPath(options.ExecutablePath)) throw new ArgumentException("ExecutablePath must be an absolute compatible shell path.", nameof(options));
        var environment = new Dictionary<string, string>(IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var pair in options.Environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.IndexOfAny(['=', '\0']) >= 0) throw new ArgumentException("Environment names must be nonempty and contain neither '=' nor NUL.", nameof(options));
            Guard.NotNull(pair.Value, nameof(options.Environment));
            if (pair.Value.IndexOf('\0') >= 0) throw new ArgumentException("Environment values cannot contain NUL.", nameof(options));
            environment.Add(pair.Key, pair.Value);
        }
        return options with { Environment = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(environment) };
    }

    internal static async ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions options, LocalShellOptions policy, CancellationToken cancellationToken)
    {
        Guard.NotNull(command, nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        if (command.IndexOf('\0') >= 0) throw new ArgumentException("A shell command cannot contain NUL.", nameof(command));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > policy.MaxDuration) throw new ArgumentOutOfRangeException(nameof(options), $"Timeout must be positive and no greater than the configured {policy.MaxDuration.TotalSeconds} seconds.");
        if (options.MaxOutputBytes is { } requested) Guard.Positive(requested, nameof(options.MaxOutputBytes));
#if NETSTANDARD2_0
        if (!IsWindows) throw new PlatformNotSupportedException("The .NET Standard shell adapter supports Windows job objects only. Use a modern .NET asset or supply ICommandSandbox for this platform.");
        using var job = new LegacyWindowsJob();
        using var gate = new ProcessReleaseGate();
        var gateName = gate.Name;
#else
        string? gateName = null;
#endif
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
#if NETSTANDARD2_0
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
#endif
        using var process = new Process { StartInfo = BuildStartInfo(command, policy, gateName), EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onExit = (_, _) => CompleteExit(process, exited);
        process.Exited += onExit;
        var output = new BoundedOutput(Math.Min(options.MaxOutputBytes ?? policy.MaxOutputBytes, policy.MaxOutputBytes));
        Task pumps = Task.CompletedTask;
        Exception? failure = null;
        var started = false;
        var exitCode = 1;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("The shell process did not start.");
            started = true;
            process.StandardInput.Close();
            pumps = Task.WhenAll(PumpAsync(process.StandardOutput.BaseStream, output, false, deadline.Token), PumpAsync(process.StandardError.BaseStream, output, true, deadline.Token));
            if (process.HasExited) CompleteExit(process, exited);
#if NETSTANDARD2_0
            if (!exited.Task.IsCompleted)
            {
                try { job.Assign(process.BorrowedProcessHandle); }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { CompleteExit(process, exited); }
            }
            if (!exited.Task.IsCompleted)
            {
                var release = gate.ReleaseAsync(startupCancellation.Token);
                Observe(release);
                var startup = Task.WhenAny(release, exited.Task);
                await WaitAsync(startup, deadline.Token).ConfigureAwait(false);
                if (await startup.ConfigureAwait(false) == release) await release.ConfigureAwait(false);
                else
                {
                    // A failed bootstrap may never connect. Return its real exit and stderr promptly.
                    // Cancel the overlapped wait before closing its native handle on .NET Framework.
                    startupCancellation.Cancel();
                    gate.Dispose();
                }
            }
#endif
            var firstCompletion = Task.WhenAny(exited.Task, pumps);
            await WaitAsync(firstCompletion, deadline.Token).ConfigureAwait(false);
            if (await firstCompletion.ConfigureAwait(false) == pumps) await pumps.ConfigureAwait(false);
            await WaitAsync(exited.Task, deadline.Token).ConfigureAwait(false);
            exitCode = await exited.Task.ConfigureAwait(false);
#if NETSTANDARD2_0
            // Descendants can retain pipe writers after the root exits. Close their job before EOF waits.
            job.Dispose();
#endif
            await WaitAsync(pumps, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (started)
            {
#if NETSTANDARD2_0
                job.Dispose();
                TryKillRoot(process, failure);
#else
                if (!exited.Task.IsCompleted) TryKillTree(process, failure);
#endif
                deadline.Cancel();
                if (failure is not null)
                {
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                }
                var cleanup = Task.WhenAll(exited.Task, pumps);
                if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) == cleanup)
                {
                    try { await cleanup.ConfigureAwait(false); }
                    catch (Exception exception) when (failure is not null) { failure.Data["Strands.ShellCleanupError"] = exception; }
                }
                else
                {
                    Observe(cleanup);
                    failure ??= new IOException("Shell cleanup did not finish within three seconds.");
                    failure.Data["Strands.ShellCleanupError"] = "Process or output drain remained pending after bounded cleanup.";
                }
            }
            process.Exited -= onExit;
        }
        if (failure is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is OperationCanceledException) throw new SandboxTimeoutException(options.Timeout, output.Result(124));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return output.Result(exitCode);
    }

    private static ProcessStartInfo BuildStartInfo(string command, LocalShellOptions options, string? gateName)
    {
        var executable = options.ExecutablePath ?? (IsWindows ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh");
        if (!File.Exists(executable)) throw new FileNotFoundException("The compatible shell executable was not found.", executable);
        var workingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The shell working directory does not exist: " + workingDirectory);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = workingDirectory,
        };
        if (IsWindows)
        {
            info.Arguments = "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellWrapper(command, gateName)));
            if (info.Arguments.Length + executable.Length + 3 > 32767) throw new ArgumentException("The encoded command exceeds the Windows command-line limit.", nameof(command));
        }
        else
        {
#if NET8_0_OR_GREATER
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
#else
            throw new PlatformNotSupportedException("A modern .NET asset or custom ICommandSandbox is required for Unix shell execution.");
#endif
        }
        foreach (var pair in options.Environment) info.EnvironmentVariables[pair.Key] = pair.Value;
        return info;
    }

    private static string PowerShellWrapper(string command, string? gateName)
    {
        var commandData = Convert.ToBase64String(StrictUtf8.GetBytes(command));
        var gate = gateName is null ? string.Empty :
            "$g=New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + gateName + "', [System.IO.Pipes.PipeDirection]::In); " +
            "try { $g.Connect(10000); $b=New-Object byte[] 1; $r=$g.ReadAsync($b,0,1); if(-not $r.Wait(10000) -or $r.Result -ne 1 -or $b[0] -ne 71){exit 125} } finally {$g.Dispose()}; ";
        return "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "$OutputEncoding=[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false); " +
            "try { " + gate + "$global:LASTEXITCODE=0; $s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + commandData + "')); " +
            "& ([ScriptBlock]::Create($s)); $ok=$?; $code=$global:LASTEXITCODE; if(-not $ok -and $code -eq 0){exit 1}; exit $code } " +
            "catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
    }

    private static async Task PumpAsync(Stream stream, BoundedOutput output, bool error, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (count == 0) return;
                output.Append(buffer, count, error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
    private static async Task WaitAsync(Task operation, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
        await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) Observe(operation);
        cancellationToken.ThrowIfCancellationRequested();
        await operation.ConfigureAwait(false);
    }
    private static void CompleteExit(Process process, TaskCompletionSource<int> completion)
    {
        try { completion.TrySetResult(process.ExitCode); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
#if NETSTANDARD2_0
    private static void TryKillRoot(Process process, Exception? failure)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (Exception exception) when (failure is not null) { failure.Data["Strands.ShellTerminationError"] = exception; }
    }
#endif
#if NET8_0_OR_GREATER
    private static void TryKillTree(Process process, Exception? failure)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Exception exception) when (failure is not null) { failure.Data["Strands.ShellTerminationError"] = exception; }
    }
#endif
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class BoundedOutput(int limit)
    {
        private readonly object _gate = new();
        private readonly MemoryStream _stdout = new();
        private readonly MemoryStream _stderr = new();
        private bool _stdoutTruncated, _stderrTruncated;
        internal void Append(byte[] bytes, int count, bool error)
        {
            lock (_gate)
            {
                var accepted = Math.Min(count, limit - (int)(_stdout.Length + _stderr.Length));
                (error ? _stderr : _stdout).Write(bytes, 0, accepted);
                if (accepted != count) { if (error) _stderrTruncated = true; else _stdoutTruncated = true; }
            }
        }
        internal SandboxExecutionResult Result(int exitCode)
        {
            lock (_gate) return new(Decode(_stdout.ToArray(), _stdoutTruncated), Decode(_stderr.ToArray(), _stderrTruncated), exitCode, _stdoutTruncated || _stderrTruncated);
        }
        private static string Decode(byte[] bytes, bool truncated)
        {
            var chars = new char[Utf8.GetMaxCharCount(bytes.Length)];
            var count = Utf8.GetDecoder().GetChars(bytes, 0, bytes.Length, chars, 0, flush: !truncated);
            return new string(chars, 0, count);
        }
    }
}
