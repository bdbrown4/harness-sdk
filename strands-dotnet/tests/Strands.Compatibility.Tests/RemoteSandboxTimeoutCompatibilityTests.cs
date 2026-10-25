using System.Diagnostics;
using Strands.Harness;
using Core = Strands.Agents.Sandbox;

namespace Strands.Compatibility.Tests;

public sealed class RemoteSandboxTimeoutCompatibilityTests
{
    [Fact]
    public async Task ActualClientTimeoutPreservesCauseAndDeferredPipeCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-adapter-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "pid.txt");
        SandboxTimeoutException? caught = null;
        try
        {
            var selected = new Core.SshSandbox(new()
            {
                Host = "fixture",
                WorkingDirectory = "/",
                Process = new()
                {
                    ExecutablePath = Path.Combine(AppContext.BaseDirectory, "SandboxProcessFixture", "Strands.Sandbox.ProcessFixture" + (Path.DirectorySeparatorChar == '\\' ? ".exe" : "")),
                    CleanupTimeout = TimeSpan.FromSeconds(1),
                    Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = "orphanHang", ["STRANDS_SANDBOX_FIXTURE_PID_FILE"] = marker },
                },
            });
            caught = await Assert.ThrowsAsync<SandboxTimeoutException>(async () => await new SandboxAdapter(selected).ExecuteAsync("opaque", new() { Timeout = TimeSpan.FromSeconds(8) }));
            Assert.IsType<Core.SandboxTimeoutException>(caught.InnerException);
            Assert.Contains("orphan:", caught.PartialResult.StandardOutput);
#if NETFRAMEWORK
            _ = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(caught.Data["Strands.CleanupTask"]);
#endif
        }
        finally
        {
            if (File.Exists(marker))
            {
                var pid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
                try { using var child = Process.GetProcessById(pid); if (!child.HasExited) child.Kill(); }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, true);
        }
        if (caught.Data["Strands.CleanupTask"] is Task<IReadOnlyList<Exception>> cleanup)
        {
            Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(10))));
            _ = await cleanup;
        }
    }
}
