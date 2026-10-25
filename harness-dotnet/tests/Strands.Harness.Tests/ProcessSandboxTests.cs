using System.Diagnostics;
using System.Text;

namespace Strands.Harness.Tests;

public sealed class ProcessSandboxTests
{
    private static bool Windows => OperatingSystem.IsWindows();

    [Theory]
    [InlineData(32, 2048, 32)]
    [InlineData(4096, 64, 64)]
    public async Task PerCommandCaptureRequestsAreClampedToTheCallerPolicy(int requested, int policy, int expected)
    {
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = policy });
        var command = Windows ? "[Console]::Write('x' * 8192)" : "head -c 8192 /dev/zero | tr '\\000' x";
        var result = await sandbox.ExecuteAsync(command, new() { MaxOutputBytes = requested });
        Assert.Equal(expected, Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.True(result.OutputTruncated);
    }

    [Fact]
    public async Task RealShellCapturesBothStreamsUnicodeAndNonzeroExit()
    {
        ICommandSandbox sandbox = new LocalFileSandbox();
        var command = Windows ? "[Console]::Write('stdout £漢'); [Console]::Error.Write('stderr'); exit 7" : "printf 'stdout £漢'; printf stderr >&2; exit 7";
        var result = await sandbox.ExecuteAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("stdout £漢", result.StandardOutput);
        Assert.Equal("stderr", result.StandardError);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task FreshShellsRespectWorkingDirectoryEnvironmentAndSpaces()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var markerName = "cwd-" + Guid.NewGuid().ToString("N") + ".txt";
            var marker = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(directory, markerName), marker);
            var key = "STRANDS_SHELL_" + Guid.NewGuid().ToString("N");
            ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new()
            {
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string> { [key] = "initial \"quoted\" value" },
            });
            var read = Windows ? "[Console]::Write($env:" + key + " + '|' + (Get-Location).Path)" : "printf '%s|%s' \"$" + key + "\" \"$PWD\"";
            var mutate = Windows ? "; $env:" + key + "='changed'; Set-Location $env:SystemRoot" : "; export " + key + "=changed; cd /";
            var first = await sandbox.ExecuteAsync(read + mutate);
            var second = await sandbox.ExecuteAsync(read);

            const string prefix = "initial \"quoted\" value|";
            Assert.StartsWith(prefix, first.StandardOutput, StringComparison.Ordinal);
            var reportedDirectory = first.StandardOutput.Substring(prefix.Length);
            Assert.True(Path.IsPathRooted(reportedDirectory));
            Assert.Equal(marker, await File.ReadAllTextAsync(Path.Combine(reportedDirectory, markerName)));
            Assert.Equal(first.StandardOutput, second.StandardOutput);
            Assert.Null(Environment.GetEnvironmentVariable(key));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OutputLimitDrainsBothPipesWithoutExceedingCombinedCaptureBudget()
    {
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 2048 });
        var command = Windows
            ? "$b=[Text.Encoding]::UTF8.GetBytes(('£漢' * 65536)); $a=[Console]::OpenStandardOutput().WriteAsync($b,0,$b.Length); $c=[Console]::OpenStandardError().WriteAsync($b,0,$b.Length); [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($a,$c)); exit 3"
            : "(i=0; while [ \"$i\" -lt 4096 ]; do printf '£漢£漢£漢£漢'; i=$((i+1)); done) & (i=0; while [ \"$i\" -lt 4096 ]; do printf '£漢£漢£漢£漢' >&2; i=$((i+1)); done) & wait; exit 3";
        var result = await sandbox.ExecuteAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardOutput) + Encoding.UTF8.GetByteCount(result.StandardError), 1, 2048);
        Assert.DoesNotContain("�", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealDeadlinePreservesPartialOutputAndTimeoutExitCode()
    {
        ICommandSandbox sandbox = new LocalFileSandbox();
        var command = Windows ? "[Console]::Write('partial'); [Console]::Error.Write('warn'); Start-Sleep -Seconds 60" : "printf partial; printf warn >&2; sleep 60";
        var error = await Assert.ThrowsAsync<SandboxTimeoutException>(() => sandbox.ExecuteAsync(command, new() { Timeout = TimeSpan.FromSeconds(10) }).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal("partial", error.PartialResult.StandardOutput);
        Assert.Equal("warn", error.PartialResult.StandardError);
        Assert.Equal(124, error.PartialResult.ExitCode);
    }

    [Fact]
    public async Task CancellationTerminatesALiveDescendantAndCompletesPromptly()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "child pid.txt");
        var childId = 0;
        using var source = new CancellationTokenSource();
        Task<SandboxExecutionResult>? execution = null;
        try
        {
            ICommandSandbox sandbox = new LocalFileSandbox();
            execution = sandbox.ExecuteAsync(ChildCommand(marker), new() { Timeout = TimeSpan.FromSeconds(30) }, source.Token).AsTask();
            childId = await ReadPidAsync(marker);
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitForExitAsync(childId);
        }
        finally
        {
            source.Cancel();
            if (execution is not null) { try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
            TryKill(childId);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AlreadyCancelledAndInvalidDeadlinesDoNotExecuteCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "strands-never-" + Guid.NewGuid().ToString("N"));
        ICommandSandbox sandbox = new LocalFileSandbox();
        var command = Windows ? "[IO.File]::WriteAllText('" + path.Replace("'", "''", StringComparison.Ordinal) + "', 'executed')" : "printf executed > " + Quote(path);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.ExecuteAsync(command, cancellationToken: source.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sandbox.ExecuteAsync(command, new() { Timeout = TimeSpan.Zero }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sandbox.ExecuteAsync(command, new() { Timeout = TimeSpan.FromSeconds(121) }).AsTask());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MissingCompatibleShellFailsBeforeCommandExecution()
    {
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "missing-shell-" + Guid.NewGuid().ToString("N")) });
        await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ExecuteAsync("echo never").AsTask());
    }

    [Fact]
    public async Task TruncatedUtf8DoesNotReturnAHalfCharacter()
    {
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 1 });
        var result = await sandbox.ExecuteAsync(Windows ? "[Console]::Write('£')" : "printf '£'");
        Assert.True(result.OutputTruncated);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task RootExitWithInheritedPipesHasABoundedDeadlineAndPreservesOutput()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "orphan pid.txt");
        var pid = 0;
        Task<SandboxExecutionResult>? execution = null;
        try
        {
            ICommandSandbox sandbox = new LocalFileSandbox();
            execution = sandbox.ExecuteAsync(OrphanCommand(marker), new() { Timeout = TimeSpan.FromSeconds(8) }).AsTask();
            pid = await ReadPidAsync(marker);
            try
            {
                var result = await execution.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("root-exit", result.StandardOutput);
            }
            catch (SandboxTimeoutException exception)
            {
                Assert.Equal("root-exit", exception.PartialResult.StandardOutput);
                Assert.Equal(124, exception.PartialResult.ExitCode);
            }
        }
        finally
        {
            TryKill(pid);
            if (execution is not null) { try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); } catch (SandboxTimeoutException) { } }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "strands shell " + Guid.NewGuid().ToString("N"));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string ChildCommand(string marker)
    {
        var pending = marker + ".pending";
        if (!Windows) return "/bin/sh -c " + Quote("printf '%s' \"$$\" > " + Quote(pending) + "; mv " + Quote(pending) + " " + Quote(marker) + "; while :; do sleep 1; done") + " & wait";
        // Publish only after closing the writer, so readiness readers never race an exclusive file handle.
        var script = "[IO.File]::WriteAllText('" + pending.Replace("'", "''", StringComparison.Ordinal) + "', [string]$PID); [IO.File]::Move('" + pending.Replace("'", "''", StringComparison.Ordinal) + "', '" + marker.Replace("'", "''", StringComparison.Ordinal) + "'); while ($true) { Start-Sleep -Milliseconds 100 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return "$child=Start-Process -WindowStyle Hidden -FilePath ([Environment]::SystemDirectory + '\\WindowsPowerShell\\v1.0\\powershell.exe') -ArgumentList '-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded + "' -PassThru; while ($true) { Start-Sleep -Milliseconds 100 }";
    }
    private static string OrphanCommand(string marker)
    {
        if (!Windows)
        {
            var pending = marker + ".pending";
            return "/bin/sh -c " + Quote("printf '%s' \"$$\" > " + Quote(pending) + "; mv " + Quote(pending) + " " + Quote(marker) + "; while :; do sleep 1; done") + " & while [ ! -s " + Quote(marker) + " ]; do sleep 0.01; done; printf root-exit; exit 0";
        }
        var launch = ChildCommand(marker).Replace("-WindowStyle Hidden", "-NoNewWindow", StringComparison.Ordinal);
        var commandEnd = launch.IndexOf("; while", StringComparison.Ordinal);
        // NoNewWindow inherits the already hidden root's redirected handles, exercising pipe ownership.
        return launch.Substring(0, commandEnd) + "; while(-not [IO.File]::Exists('" + marker.Replace("'", "''", StringComparison.Ordinal) + "')){Start-Sleep -Milliseconds 10}; [Console]::Write('root-exit'); exit 0";
    }
    private static async Task<int> ReadPidAsync(string marker)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker), out var pid) && pid > 0) return pid;
            await Task.Delay(20);
        }
        throw new TimeoutException("The real child did not report its PID.");
    }
    private static async Task WaitForExitAsync(int pid)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            try { using var process = Process.GetProcessById(pid); if (process.HasExited) return; }
            catch (ArgumentException) { return; }
            await Task.Delay(20);
        }
        throw new TimeoutException("The real child remained alive after cancellation.");
    }
    private static void TryKill(int pid)
    {
        if (pid <= 0) return;
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (ArgumentException) { }
    }
}
