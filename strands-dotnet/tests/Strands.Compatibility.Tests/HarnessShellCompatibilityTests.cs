using System.Diagnostics;
using System.Text;
using Strands.Harness;

namespace Strands.Compatibility.Tests;

public sealed class HarnessShellCompatibilityTests
{
    private static bool Windows => Path.DirectorySeparatorChar == '\\';

    [Fact]
    public async Task RealShellCapturesUnicodeStreamsAndExitCode()
    {
        ICommandSandbox sandbox = new LocalFileSandbox();
        var command = Windows ? "[Console]::Write('legacy £漢'); [Console]::Error.Write('warning'); exit 7" : "printf 'legacy £漢'; printf warning >&2; exit 7";
        var result = await Bounded(sandbox.ExecuteAsync(command).AsTask());

        Assert.Equal("legacy £漢", result.StandardOutput);
        Assert.Equal("warning", result.StandardError);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RealShellDeadlineIncludesPartialOutputAndTerminatesExecution()
    {
        ICommandSandbox sandbox = new LocalFileSandbox();
        var command = Windows ? "[Console]::Write('partial'); [Console]::Error.Write('warn'); Start-Sleep -Seconds 60" : "printf partial; printf warn >&2; sleep 60";
        var error = await Assert.ThrowsAsync<SandboxTimeoutException>(() => Bounded(sandbox.ExecuteAsync(command, new ShellExecutionOptions { Timeout = TimeSpan.FromSeconds(10) }).AsTask()));

        Assert.Equal(124, error.PartialResult.ExitCode);
        Assert.Equal("partial", error.PartialResult.StandardOutput);
        Assert.Equal("warn", error.PartialResult.StandardError);
    }

    [Fact]
    public async Task CancellationKillsARealChildProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy shell " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "child pid.txt");
        var pid = 0;
        using var source = new CancellationTokenSource();
        Task<SandboxExecutionResult>? execution = null;
        try
        {
            ICommandSandbox sandbox = new LocalFileSandbox();
            execution = sandbox.ExecuteAsync(ChildCommand(marker), new ShellExecutionOptions { Timeout = TimeSpan.FromSeconds(30) }, source.Token).AsTask();
            pid = await ReadPidAsync(marker);
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(execution));
            await WaitForExitAsync(pid);
        }
        finally
        {
            source.Cancel();
            if (execution is not null) { try { await Bounded(execution); } catch (OperationCanceledException) { } }
            Kill(pid);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealShellEnforcesCombinedOutputLimitAndFreshWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy shell " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var markerName = "cwd-" + Guid.NewGuid().ToString("N") + ".txt";
            var marker = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, markerName), marker);
            var key = "STRANDS_SHELL_" + Guid.NewGuid().ToString("N");
            ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new LocalShellOptions
            {
                WorkingDirectory = directory,
                MaxOutputBytes = 2048,
                Environment = new Dictionary<string, string> { [key] = "initial \"quoted\"" },
            });
            var read = Windows ? "[Console]::Write($env:" + key + " + '|' + (Get-Location).Path)" : "printf '%s|%s' \"$" + key + "\" \"$PWD\"";
            var mutate = Windows ? "; $env:" + key + "='changed'; Set-Location $env:SystemRoot" : "; export " + key + "=changed; cd /";
            var first = await Bounded(sandbox.ExecuteAsync(read + mutate).AsTask());
            var second = await Bounded(sandbox.ExecuteAsync(read).AsTask());
            const string prefix = "initial \"quoted\"|";
            Assert.StartsWith(prefix, first.StandardOutput, StringComparison.Ordinal);
            var reportedDirectory = first.StandardOutput.Substring(prefix.Length);
            Assert.True(Path.IsPathRooted(reportedDirectory));
            Assert.Equal(marker, File.ReadAllText(Path.Combine(reportedDirectory, markerName)));
            Assert.Equal(first.StandardOutput, second.StandardOutput);
            var flood = Windows ? "$b=[Text.Encoding]::UTF8.GetBytes(('£漢' * 65536)); $a=[Console]::OpenStandardOutput().WriteAsync($b,0,$b.Length); $c=[Console]::OpenStandardError().WriteAsync($b,0,$b.Length); [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($a,$c)); exit 3"
                : "(i=0; while [ \"$i\" -lt 4096 ]; do printf '£漢£漢£漢£漢'; i=$((i+1)); done) & (i=0; while [ \"$i\" -lt 4096 ]; do printf '£漢£漢£漢£漢' >&2; i=$((i+1)); done) & wait; exit 3";
            var result = await Bounded(sandbox.ExecuteAsync(flood).AsTask());
            Assert.Equal(3, result.ExitCode);
            Assert.True(result.OutputTruncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardOutput) + Encoding.UTF8.GetByteCount(result.StandardError), 1, 2048);
            Assert.DoesNotContain("�", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

#if NETFRAMEWORK
    [Fact]
    public async Task LegacyShellDoesNotInheritUnrelatedPipeWriters()
    {
        using var writer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        var gateName = "strands-inheritance-" + Guid.NewGuid().ToString("N");
        using var gate = new System.IO.Pipes.NamedPipeServerStream(gateName, System.IO.Pipes.PipeDirection.Out, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        using var source = new CancellationTokenSource();
        var connection = gate.WaitForConnectionAsync(source.Token);
        Task<SandboxExecutionResult>? execution = null;
        var writerClosed = false;
        Exception? failure = null;
        try
        {
            ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new LocalShellOptions
            {
                ExecutablePath = Path.Combine(AppContext.BaseDirectory, "SandboxProcessFixture", "Strands.Sandbox.ProcessFixture.exe"),
                Environment = new Dictionary<string, string>
                {
                    ["STRANDS_SANDBOX_FIXTURE_MODE"] = "external-release-gate",
                    ["STRANDS_SANDBOX_FIXTURE_GATE"] = gateName,
                },
            });
            execution = sandbox.ExecuteAsync("echo never", new ShellExecutionOptions { Timeout = TimeSpan.FromSeconds(30) }, source.Token).AsTask();
            await Bounded(AdmittedAsync());
            Assert.False(execution.IsCompleted, "The admitted shell must still be waiting at the fixture-owned release gate.");
            writer.DisposeLocalCopyOfClientHandle();
            writerClosed = true;
            var retained = PeekNamedPipe(writer.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero);
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Assert.False(retained, "The admitted shell inherited an unrelated pipe writer and can delay another sandbox's EOF.");
            Assert.Equal(109, error);
            Assert.Equal(-1, writer.ReadByte());
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            try
            {
                if (!writerClosed) writer.DisposeLocalCopyOfClientHandle();
                if (gate.IsConnected)
                {
                    await gate.WriteAsync(new byte[] { 71 }, 0, 1);
                    await gate.FlushAsync();
                }
                else source.Cancel();
            }
            catch (Exception error) { cleanup.Add(error); source.Cancel(); }
            if (execution is not null)
            {
                try { Assert.Equal(0, (await execution).ExitCode); }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { }
                catch (Exception error) { cleanup.Add(error); }
            }
            source.Cancel();
            try { await connection; }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
            {
                if (failure is not null) failure.Data["Strands.CleanupErrors"] = cleanup;
                else
                {
                    if (cleanup.Count > 1) cleanup[0].Data["Strands.CleanupErrors"] = cleanup.Skip(1).ToArray();
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
                }
            }
        }

        async Task<bool> AdmittedAsync() { await connection; return true; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr bytesRead, out uint available, IntPtr remaining);

    [Fact]
    public async Task LegacyWorkerExitBeforeHandshakeReturnsItsRealExitInsteadOfTimingOut()
    {
        // The owned worker exits before reading stdin or connecting to the startup gate.
        ICommandSandbox sandbox = new LocalFileSandbox(shellOptions: new LocalShellOptions
        {
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "SandboxProcessFixture", "Strands.Sandbox.ProcessFixture.exe"),
            Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = "reject-shell-start" },
        });
        using var source = new CancellationTokenSource();
        var execution = sandbox.ExecuteAsync("echo never", new ShellExecutionOptions { Timeout = TimeSpan.FromSeconds(20) }, source.Token).AsTask();
        try
        {
            Assert.Same(execution, await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(5))));
            var result = await execution;
            Assert.Equal(23, result.ExitCode);
            Assert.Equal("strands-sandbox-fixture: reject-shell-start", result.StandardError);
        }
        finally
        {
            source.Cancel();
            try { await Bounded(execution); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task LegacyJobClosesDescendantsAfterRootExitBeforeDrainingInheritedPipes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy shell " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "orphan pid.txt");
        var pid = 0;
        try
        {
            var launch = ChildCommand(marker).Replace("-WindowStyle Hidden", "-NoNewWindow");
            var command = launch.Substring(0, launch.IndexOf("; while", StringComparison.Ordinal)) + "; while(-not [IO.File]::Exists('" + marker.Replace("'", "''") + "')){Start-Sleep -Milliseconds 10}; [Console]::Write('root-exit'); exit 0";
            ICommandSandbox sandbox = new LocalFileSandbox();
            var execution = sandbox.ExecuteAsync(command, new ShellExecutionOptions { Timeout = TimeSpan.FromSeconds(15) }).AsTask();
            pid = await ReadPidAsync(marker);
            var result = await Bounded(execution);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("root-exit", result.StandardOutput);
            await WaitForExitAsync(pid);
        }
        finally { Kill(pid); Directory.Delete(directory, recursive: true); }
    }
#endif

    private static async Task<T> Bounded<T>(Task<T> operation)
    {
        if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(20))) != operation) throw new TimeoutException("Shell compatibility operation exceeded the test deadline.");
        return await operation;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string ChildCommand(string marker)
    {
        var pending = marker + ".pending";
        if (!Windows) return "/bin/sh -c " + Quote("printf '%s' \"$$\" > " + Quote(pending) + "; mv " + Quote(pending) + " " + Quote(marker) + "; while :; do sleep 1; done") + " & wait";
        // Publish only after closing the writer, so readiness readers never race an exclusive file handle.
        var script = "[IO.File]::WriteAllText('" + pending.Replace("'", "''") + "', [string]$PID); [IO.File]::Move('" + pending.Replace("'", "''") + "', '" + marker.Replace("'", "''") + "'); while ($true) { Start-Sleep -Milliseconds 100 }";
        return "$child=Start-Process -WindowStyle Hidden -FilePath ([Environment]::SystemDirectory + '\\WindowsPowerShell\\v1.0\\powershell.exe') -ArgumentList '-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) + "' -PassThru; while ($true) { Start-Sleep -Milliseconds 100 }";
    }
    private static async Task<int> ReadPidAsync(string marker)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var pid) && pid > 0) return pid;
            await Task.Delay(20);
        }
        throw new TimeoutException("The real descendant did not report readiness.");
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
        throw new TimeoutException("A descendant survived shell cancellation.");
    }
    private static void Kill(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
#if NET8_0_OR_GREATER
                process.Kill(entireProcessTree: true);
#else
                process.Kill();
#endif
            }
        }
        catch (ArgumentException) { }
    }
}
