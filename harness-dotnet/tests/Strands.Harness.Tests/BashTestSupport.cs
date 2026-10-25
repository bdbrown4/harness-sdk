using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Strands.Agents;
using Strands.Harness.Tools;
using Xunit.Abstractions;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    private readonly ITestOutputHelper _bashOutput;
    public BashTests(ITestOutputHelper output) => _bashOutput = output;

    // The separately owned Process view pins the SDK-owned OS root, never the SDK's borrowed handle.
    private static Process BindOwnedBashRoot(BashFixture fixture, Agent? agent = null)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessions = (ConditionalWeakTable<Agent, BashSession>)typeof(BashTool).GetField("_sessions", flags)!.GetValue(fixture.Tool)!;
        Assert.True(sessions.TryGetValue(agent ?? fixture.Agent, out var session));
        var generation = typeof(BashSession).GetField("_generation", flags)!.GetValue(session)!;
        Assert.NotNull(generation);
        var facade = generation.GetType().GetField("_process", flags)!.GetValue(generation)!;
        var id = (int)facade.GetType().GetProperty("Id", flags)!.GetValue(facade)!;
        var process = Process.GetProcessById(id);
        try { _ = process.SafeHandle; return process; }
        catch { process.Dispose(); throw; }
    }

    private void AssertOwnedAssociationExited(Process root, IReadOnlyList<Process> observed, string phase)
    {
        Exception? physicalFailure = null;
        try { BashFixture.AssertExited([root]); }
        catch (Exception error) { physicalFailure = error; throw; }
        finally
        {
            try
            {
                _bashOutput.WriteLine(JsonSerializer.Serialize(new
                {
                    Phase = phase,
                    OwnedRoot = ProcessObservation(root),
                    SameImageObserved = observed.Select(ProcessObservation).ToArray()
                }));
            }
            catch (Exception diagnostic) when (physicalFailure is not null) { physicalFailure.Data["BashTest.Observation"] = diagnostic; }
        }
    }

    private void RecordExitMembership(Process root, IEnumerable<Process> observed)
    {
        // Same-image membership is retained as evidence; it is not itself SDK root/descendant ownership.
        _bashOutput.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = "explicit-exit-owned-root-and-observed-membership",
            OwnedRoot = ProcessObservation(root),
            SameImageObserved = observed.Select(ProcessObservation).ToArray(),
        }));
    }

    private static object ProcessObservation(Process process)
    {
        try { return new { Pid = process.Id, Handle = process.SafeHandle.DangerousGetHandle().ToInt64(), PhysicalExit = process.WaitForExit(0), ExitCode = process.HasExited ? process.ExitCode : (int?)null }; }
        catch (Exception error) { return new { Pid = process.Id, ObservationError = error.GetType().FullName, error.Message }; }
    }

    private static async Task WithFixture(Func<BashFixture, Task> body, Func<string, BashConfig>? configure = null)
    {
        var fixture = new BashFixture(configure);
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup)
            {
                if (primary is null) primary = cleanup;
                else if (!ReferenceEquals(primary, cleanup)) primary.Data["BashTest.Cleanup"] = cleanup;
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string ShellPath(string value) => ShellQuote(value.Replace('\\', '/'));
    private static JsonElement ResultData(ToolResultBlock result)
    {
        Assert.Equal(ToolResultStatus.Success, result.Status);
        return Assert.IsType<JsonElement>(result.StructuredContent);
    }
    private static string Output(ToolResultBlock result) => ResultData(result).GetProperty("output").GetString()!;
    private static async Task Settled(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12))));
        await task;
    }
    private static async Task<Exception> Failed(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12))));
        return await Assert.ThrowsAnyAsync<Exception>(async () => await task);
    }

    private sealed class BashFixture : IAsyncDisposable
    {
        private readonly List<Agent> _agents = [];
        private readonly List<Task> _accepted = [];
        private readonly List<string> _releases = [];
        private readonly List<Process> _observed = [];
        private readonly HashSet<int> _initial;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "strands-bash-expanded-" + Guid.NewGuid().ToString("N"));
        internal string Executable { get; }
        internal BashTool Tool { get; }
        internal Agent Agent { get; }
        internal BashFixture(Func<string, BashConfig>? configure)
        {
            Executable = Environment.GetEnvironmentVariable("STRANDS_BASH_TEST_EXECUTABLE") ?? "";
            if (!Path.IsPathRooted(Executable) || !File.Exists(Executable))
                throw new InvalidOperationException("Expanded Bash tests require an explicitly selected absolute interpreter on the owned runner.");
            Directory.CreateDirectory(Root);
            _initial = SelectedProcesses().Select(process => { using (process) return process.Id; }).ToHashSet();
            Tool = Bash.Create(configure?.Invoke(Root) ?? new() { ExecutablePath = Executable, WorkingDirectory = Root });
            Agent = AddAgent();
        }
        internal Agent AddAgent(AgentOptions? options = null)
        {
            var agent = options is null ? NewAgent() : new Agent(options);
            _agents.Add(agent);
            return agent;
        }
        internal Task<ToolResultBlock> Start(string command, Agent? agent = null, double? timeout = null, CancellationToken caller = default)
            => StartInput(new Dictionary<string, object?> { ["mode"] = "execute", ["command"] = command, ["timeout"] = timeout ?? 10 }, agent, caller);
        internal Task<ToolResultBlock> StartInput(object input, Agent? agent = null, CancellationToken caller = default)
        {
            var task = InvokeTrackedAsync(input, agent ?? Agent, caller);
            _accepted.Add(task);
            return task;
        }
        internal T Track<T>(T task) where T : Task { _accepted.Add(task); return task; }
        private async Task<ToolResultBlock> InvokeTrackedAsync(object input, Agent agent, CancellationToken caller)
        {
            using var limit = caller.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = caller.CanBeCanceled ? caller : limit!.Token;
            var json = input is JsonElement element ? element : JsonSerializer.SerializeToElement(input);
            return await Tool.InvokeResultAsync(json, new(agent, new("expanded-bash", Tool.Spec.Name, json), new Dictionary<string, object?>()), token);
        }
        internal async Task<string> Run(string command, Agent? agent = null, double? timeout = null)
            => Output(await Start(command, agent, timeout));
        internal Task<ToolResultBlock> Restart(Agent? agent = null) => StartInput(new { mode = "restart" }, agent);
        internal (string Ready, string Release, string Command) Gate(string suffix = "gate")
        {
            var ready = Path.Combine(Root, suffix + ".ready");
            var release = Path.Combine(Root, suffix + ".release");
            _releases.Add(release);
            return (ready, release, "builtin printf ready > " + ShellPath(ready) + "; while [ ! -f " + ShellPath(release) + " ]; do sleep 0.02; done");
        }
        internal static async Task Ready(string path)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(path) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.True(File.Exists(path), "The accepted Bash command did not reach its owned file barrier.");
        }
        internal static void Release(string path) => File.WriteAllText(path, "release");
        internal Process[] ObserveNewInterpreters()
        {
            foreach (var process in SelectedProcesses())
            {
                if (_initial.Contains(process.Id) || _observed.Any(existing => existing.Id == process.Id)) { process.Dispose(); continue; }
                _ = process.SafeHandle; // Retain the physical process identity across exit/PID reuse.
                _observed.Add(process);
            }
            Assert.NotEmpty(_observed);
            return _observed.ToArray();
        }
        private IEnumerable<Process> SelectedProcesses()
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
            {
                bool matching;
                var handleRetained = false;
                try
                {
                    _ = process.SafeHandle; // Retain identity before querying path; enumeration may already be stale.
                    handleRetained = true;
                    matching = ExecutablePath(process) is string image && string.Equals(Path.GetFullPath(image), Path.GetFullPath(Executable), StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException) when (!handleRetained || ExitedWhileObserving(process)) { process.Dispose(); continue; }
                catch { process.Dispose(); throw; }
                if (matching) yield return process; else process.Dispose();
            }
        }
        private static bool ExitedWhileObserving(Process process)
        {
            try { return process.WaitForExit(0); }
            catch { return false; } // Keep the original observation error unless strict physical exit was established.
        }
        private static string? ExecutablePath(Process process)
        {
            if (Path.DirectorySeparatorChar != '\\') return process.MainModule?.FileName;
            // A 32-bit test process may observe the selected 64-bit interpreter. Avoid module enumeration.
            var buffer = new StringBuilder(32768);
            var length = buffer.Capacity;
            if (!QueryFullProcessImageName(process.SafeHandle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return buffer.ToString();
        }
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder executable, ref int length);
        internal static void AssertExited(IEnumerable<Process> processes)
        {
            foreach (var process in processes) Assert.True(process.WaitForExit(0), "An observed interpreter was still alive after the public physical close completed.");
        }
        public async ValueTask DisposeAsync()
        {
            Exception? primary = null;
            foreach (var path in _releases) try { Release(path); } catch (Exception error) { Record(error); }
            try { await Settled(Tool.DisposeAsync().AsTask()); } catch (Exception error) { Record(error); }
            foreach (var task in _accepted)
            {
                try
                {
                    Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12))));
                    try { await task; } catch { /* Individual tests assert their accepted failure; still physically join it. */ }
                }
                catch (Exception error) { Record(error); }
            }
            foreach (var agent in _agents) try { await Settled(agent.DisposeAsync().AsTask()); } catch (Exception error) { Record(error); }
            foreach (var process in _observed) process.Dispose();
            if (_accepted.All(task => task.IsCompleted)) try { Directory.Delete(Root, recursive: true); } catch (Exception error) { Record(error); }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
            void Record(Exception error)
            {
                if (primary is null) primary = error;
                else if (!ReferenceEquals(primary, error)) primary.Data["BashTest.AdditionalCleanup"] = error;
            }
        }
    }
}
