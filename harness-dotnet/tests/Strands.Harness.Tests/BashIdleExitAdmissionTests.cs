using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Strands.Harness.Tools;
using Xunit;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public async Task APhysicallyExitedIdleGenerationIsReplacedBeforeItsCallbacksPublishFailure()
    {
        var executable = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE")!;
        Assert.True(Path.IsPathRooted(executable) && File.Exists(executable));
        var session = new BashSession(new BashConfig
        {
            ExecutablePath = executable,
            WorkingDirectory = Environment.CurrentDirectory
        }, TimeSpan.FromSeconds(10), _ => { });
        Process? root = null;
        Task<(string Output, string Error)>? next = null;
        Exception? primary = null;
        try
        {
            Assert.Equal("armed", (await session.ExecuteAsync("STRANDS_IDLE_VALUE=old; builtin printf armed", null, CancellationToken.None)).Output);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var generation = typeof(BashSession).GetField("_generation", fields)!.GetValue(session)!;
            var gate = generation.GetType().GetField("_gate", fields)!.GetValue(generation)!;
            var process = generation.GetType().GetField("_process", fields)!.GetValue(generation)!;
            var pid = (int)process.GetType().GetProperty("Id", fields)!.GetValue(process)!;
            root = Process.GetProcessById(pid);
            _ = root.SafeHandle;
            Assert.False(root.HasExited);
            var retained = (Process)process.GetType().GetField("_process", fields)!.GetValue(process)!;
            var inputPipe = process.GetType().GetProperty("StandardInput", fields)!.GetValue(process)!;
            var input = (Stream)inputPipe.GetType().GetProperty("BaseStream", fields)!.GetValue(inputPipe)!;
            var exit = System.Text.Encoding.UTF8.GetBytes("exit\n");
            await input.WriteAsync(exit, 0, exit.Length);
            await input.FlushAsync();
            Assert.True(root.WaitForExit(5000), "The exact owned idle root must physically exit before the next admission.");
            await ((Task)generation.GetType().GetField("_pumps", fields)!.GetValue(generation)!);
            lock (retained) Assert.True(retained.HasExited);
            // EOF readers and the original exit publisher have finished before staging an uncached terminal observation.
            lock (gate)
            {
                generation.GetType().GetField("_failure", fields)!.SetValue(generation, null);
                Assert.Null(generation.GetType().GetField("_failure", fields)!.GetValue(generation));
            }
            _bashOutput.WriteLine("Exact owned root and original publishers joined; the next admission must inspect physical exit without a cached failure.");
            next = session.ExecuteAsync("builtin printf '%s' \"${STRANDS_IDLE_VALUE-fresh}\"", null, CancellationToken.None);
            Assert.Equal("fresh", (await next).Output);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await session.CloseAsync(); } catch (Exception error) { Record(error); }
            if (next is not null) try { await next; } catch (Exception error) { Record(error); }
            if (root is not null)
            {
                try { Assert.True(root.WaitForExit(0), "The old retained root must remain physically joined."); }
                catch (Exception error) { Record(error); }
                finally { root.Dispose(); }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        void Record(Exception error)
        {
            if (primary is null) primary = error;
            else BashSession.AttachCleanup(primary, error);
        }
    }
}
