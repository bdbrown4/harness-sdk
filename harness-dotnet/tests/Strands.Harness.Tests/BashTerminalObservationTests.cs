#if !NET48
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public Task TerminalObservationDoesNotHoldTheGenerationGateWhileWaitingForTheProcessMonitor() => ObserveTerminalProcessMonitorAsync(false);

    [Fact]
    public Task StopCanDisposeTheProcessWhileTerminalObservationWaitsForItsMonitor() => ObserveTerminalProcessMonitorAsync(true);

    private async Task ObserveTerminalProcessMonitorAsync(bool stopDuringObservation)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var executable = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE")!;
        Assert.True(Path.IsPathRooted(executable) && File.Exists(executable));
        var generation = new BashSession.Generation(new() { ExecutablePath = executable, WorkingDirectory = Environment.CurrentDirectory });
        Process? root = null;
        Thread? query = null;
        Exception? primary = null, queryError = null;
        bool? terminal = null;
        var started = new ManualResetEventSlim();
        try
        {
            Assert.Equal("armed", (await generation.RunAsync("builtin printf armed", CancellationToken.None)).Output);
            var facade = typeof(BashSession.Generation).GetField("_process", fields)!.GetValue(generation)!;
            var process = (Process)facade.GetType().GetField("_process", fields)!.GetValue(facade)!;
            var gate = typeof(BashSession.Generation).GetField("_gate", fields)!.GetValue(generation)!;
            var failure = typeof(BashSession.Generation).GetField("_failure", fields)!;
            process.EnableRaisingEvents = false;
            root = Process.GetProcessById(process.Id);
            _ = root.SafeHandle;
            Assert.False(root.HasExited);
            await process.StandardInput.WriteLineAsync("exit");
            await process.StandardInput.FlushAsync();
            Assert.True(root.WaitForExit(5000), "The exact owned idle process must physically join.");
            await ((Task)typeof(BashSession.Generation).GetField("_pumps", fields)!.GetValue(generation)!).WaitAsync(TimeSpan.FromSeconds(5));
            lock (gate)
            {
                Assert.NotNull(failure.GetValue(generation));
                failure.SetValue(generation, null);
            }
            query = new Thread(() =>
            {
                started.Set();
                try { terminal = generation.IsTerminal; }
                catch (Exception error) { queryError = error; }
            }) { IsBackground = true, Name = "Bash-terminal-observation-control" };
            lock (process)
            {
                query.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "The actual terminal query must start.");
                Assert.True(SpinWait.SpinUntil(() => (query.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)),
                    "The query must physically wait for the retained Process monitor.");
                var available = Monitor.TryEnter(gate);
                if (available) Monitor.Exit(gate);
                _bashOutput.WriteLine("ACTUAL_BLOCKED_QUERY ProcessMonitorOwned=true GenerationGateAvailable=" + available + "; stop=" + stopDuringObservation);
                Assert.True(available, "The real terminal query retained Generation._gate while waiting for Process's monitor.");
                if (stopDuringObservation)
                {
                    process.EnableRaisingEvents = true;
                    Assert.True(process.WaitForExit(0));
                    var stopped = generation.StopAsync();
                    Assert.True(stopped.IsCompleted, "The observer cannot prevent a physically complete stop.");
                    stopped.GetAwaiter().GetResult();
                }
            }
            Assert.True(query.Join(5000), "The query must join after the exact Process monitor is released.");
            Assert.Null(queryError);
            Assert.True(terminal);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            // Every monitor has left scope before joining the original query and interpreter.
            if (query is not null && (query.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                try { Assert.True(query.Join(5000), "The original query must physically join during cleanup."); } catch (Exception error) { Record(error); }
            try { await generation.StopAsync(); } catch (Exception error) { Record(error); }
            if (root is not null)
            {
                try { Assert.True(root.WaitForExit(0)); } catch (Exception error) { Record(error); }
                finally { root.Dispose(); }
            }
            started.Dispose();
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        void Record(Exception error)
        {
            if (primary is null) primary = error;
            else BashSession.AttachCleanup(primary, error);
        }
    }
}
#endif
