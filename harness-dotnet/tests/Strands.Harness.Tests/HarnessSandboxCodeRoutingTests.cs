using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Harness.Tests;

public sealed class HarnessSandboxCodeRoutingTests
{
    [Fact]
    public async Task ConfiguredCoreAdapterKeepsIdentityAndUnboundPythonDefaultsAndExactToken()
    {
        var core = new CoreSelection();
        await using var agent = await Harness.CreateAsync(Options(new SandboxAdapter(core)));
        Assert.Same(core, agent.Sandbox); // Mandatory before any unbound call: no ambient interpreter on RED.
        using var caller = new CancellationTokenSource();
        using var result = JsonDocument.Parse(await InvokePython(agent, caller.Token));
        Assert.Equal("selected", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(7, result.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("fake-code", core.Code);
        Assert.Equal("python3", core.Language);
        Assert.Equal(TimeSpan.FromSeconds(120), core.Options!.Timeout);
        Assert.Equal(caller.Token, core.Token);
        Assert.Equal(0, core.Commands);
    }

    [Fact]
    public async Task CustomCodeCapabilityReceivesDetachedOptionsWithoutCommandFallback()
    {
        var selected = new CodeSelection();
        await using var agent = await Harness.CreateAsync(Options(selected));
        AssertSelectedBridge(agent); // Mandatory before requesting source execution.
        using var caller = new CancellationTokenSource();
        var environment = new Dictionary<string, string> { ["ENTERED"] = "original" };
        var options = new Core.ExecuteOptions { WorkingDirectory = "/selected", Environment = environment, Timeout = TimeSpan.FromSeconds(9), MaximumOutputBytes = 321 };
        var stream = agent.Sandbox.ExecuteCodeStreamingAsync("fake-code", "python3.12", options, caller.Token);
        environment["ENTERED"] = "mutated";
        var events = new List<Core.SandboxStreamEvent>();
        await foreach (var item in stream.WithCancellation(caller.Token)) events.Add(item);
        Assert.Equal("fake-code", selected.Code);
        Assert.Equal("python3.12", selected.Language);
        Assert.Equal(caller.Token, selected.Token);
        Assert.NotSame(options, selected.Options);
        Assert.Equal("original", selected.Options!.Environment["ENTERED"]);
        Assert.Equal("/selected", selected.Options.WorkingDirectory);
        Assert.Equal(321, selected.Options.MaximumOutputBytes);
        Assert.Equal(TimeSpan.FromSeconds(9), selected.Options.Timeout);
        Assert.Null(selected.Options.StandardInput); // Code owns stdin, never pass it as separate input.
        Assert.IsType<Core.StreamChunk>(events[0]);
        Assert.IsType<Core.ExecutionResult>(events[1]);
        Assert.Equal(1, selected.CodeCalls);
        Assert.Equal(0, selected.Commands);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await agent.Sandbox.ExecuteCodeAsync("fake-code", "python3", new() { StandardInput = [42] }));
        Assert.Equal(1, selected.CodeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextOnlyAndLegacyCommandOnlySelectionsRejectCodeWithoutHostOrCommandExecution(bool commandCapable)
    {
        var selected = commandCapable ? new CommandSelection() : new FileSelection();
        await using var agent = await Harness.CreateAsync(Options(selected));
        AssertSelectedBridge(agent); // Baseline must stop here instead of executing local Python.
        var failure = await Assert.ThrowsAsync<Core.PythonReplException>(async () => await InvokePython(agent));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(0, selected.Commands);
        Assert.Empty(selected.Reads);
        Assert.Empty(selected.Lists);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task ExplicitCoreSandboxWinsConstructorSdkFilesAndChildRebinding()
    {
        var core = new CoreSelection { SkillTree = true };
        var selected = new CodeSelection();
        await using var agent = await Harness.CreateAsync(Options(selected, core) with { Skills = new() { Enabled = true } });
        Assert.Same(core, agent.Sandbox);
        Assert.Same(core, agent.FileSystem);
        Assert.Contains("./.agent/skills", core.Lists);
        Assert.Empty(selected.Lists);
        Assert.Contains(agent.Tools, tool => tool.Spec.Name == "skills");
        var childCore = new CoreSelection { SkillTree = true };
        await using var child = agent.CreateChild(options => options with { Sandbox = childCore });
        Assert.Same(childCore, child.Sandbox);
        Assert.Same(childCore, child.FileSystem);
        await child.InitializeAsync();
        Assert.Contains("./.agent/skills", childCore.Lists);
        Assert.Equal(0, selected.CodeCalls);
        Assert.Equal(0, core.CodeCalls);
    }

    [Fact]
    public async Task ExplicitFilesystemWinsWhileBuiltinWritesStayOnSelectedHarnessFiles()
    {
        var selected = new CodeSelection();
        var core = new CoreSelection();
        var explicitFiles = new FileSelection();
        await using var agent = await Harness.CreateAsync(Options(selected, core, explicitFiles) with { BuiltinTools = ["write"] });
        Assert.Same(core, agent.Sandbox);
        Assert.Same(explicitFiles, agent.FileSystem);
        var tool = Assert.Single(agent.Tools);
        Assert.Equal("write", tool.Spec.Name);
        var input = JsonSerializer.SerializeToElement(new { path = "/selected/file.txt", content = "selected text" });
        await tool.InvokeAsync(input, new(agent, new("write-call", "write", input), new Dictionary<string, object?>()));
        Assert.Equal(("/selected/file.txt", "selected text"), Assert.Single(selected.Writes));
        Assert.Empty(explicitFiles.Writes);
        Assert.Empty(core.Reads);
        Assert.Equal(0, core.CodeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeTimeoutAndCancellationPreserveExactCauseTokenAndCleanup(bool cancel)
    {
        using var caller = new CancellationTokenSource();
        var cleanup = new IOException("independent cleanup");
        Exception original = cancel ? new OperationCanceledException("selected cancellation", null, caller.Token)
            : new Core.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(124, "partial", "error"));
        original.Data["Strands.CleanupErrors"] = new[] { cleanup };
        var selected = new CodeSelection { Failure = original, BeforeMove = cancel ? caller.Cancel : null };
        await using var agent = await Harness.CreateAsync(Options(selected));
        AssertSelectedBridge(agent); // Never let the baseline's local default reach Python.
        var caught = await Record.ExceptionAsync(async () => await InvokePython(agent, caller.Token));
        Assert.Same(original, caught);
        Assert.Same(cleanup, Assert.Single((IEnumerable<Exception>)caught!.Data["Strands.CleanupErrors"]!));
        Assert.Equal(caller.Token, selected.Token);
        if (cancel) Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(caught).CancellationToken);
        else Assert.Contains("\"exit_code\":124", caught.Message, StringComparison.Ordinal);
        Assert.Equal(1, selected.IteratorDisposals);
    }

    [Fact]
    public async Task TerminalCodeCompletionPhysicallyJoinsDisposeAndDoesNotOwnSelection()
    {
        var selected = new CodeSelection { HoldDisposal = true };
        await using var agent = await Harness.CreateAsync(Options(selected));
        AssertSelectedBridge(agent); // Mandatory before source execution even in this lifetime test.
        var operation = agent.Sandbox.ExecuteCodeAsync("fake-code", "python3").AsTask();
        try
        {
            Assert.Same(selected.DisposeEntered.Task, await Task.WhenAny(selected.DisposeEntered.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            await selected.DisposeEntered.Task;
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            selected.ReleaseDisposal.TrySetResult(true);
            await operation;
        }
        Assert.Equal(1, selected.IteratorDisposals);
        await agent.DisposeAsync();
        Assert.Equal(0, selected.DisposeCalls);
    }

    [Fact]
    public async Task LocalCodeCapabilityIsAdvertisedWithoutExecutingOrChangingShellConfiguration()
    {
        var environment = new Dictionary<string, string> { ["ENTERED"] = "original" };
        var directory = Path.GetFullPath(Path.GetTempPath());
        var executable = Path.Combine(directory, "never-invoked-shell.exe");
        var selected = new LocalFileSandbox(shellOptions: new() { WorkingDirectory = directory, ExecutablePath = executable, Environment = environment, MaxDuration = TimeSpan.FromSeconds(13), MaxOutputBytes = 777 });
        environment["ENTERED"] = "mutated";
        Assert.IsAssignableFrom<ICodeSandbox>(selected); // Type-only RED: no code method invoked.
        var policy = Assert.IsType<LocalShellOptions>(typeof(LocalFileSandbox).GetField("_shellOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selected));
        Assert.Equal("original", policy.Environment["ENTERED"]);
        Assert.Equal(executable, policy.ExecutablePath);
        Assert.Equal(directory, policy.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(13), policy.MaxDuration);
        Assert.Equal(777, policy.MaxOutputBytes);
        await using var agent = await Harness.CreateAsync(Options(selected));
        AssertSelectedBridge(agent);
        Assert.Same(agent.Sandbox, agent.Sandbox); // Never enumerate a local stream in desktop tests.
    }

    private static HarnessOptions Options(ISandbox selected, Core.Sandbox? core = null, IReadOnlyFileSystem? files = null) => new()
    {
        Sandbox = selected,
        AgentOptions = new() { Model = new NoModel(), Sandbox = core, FileSystem = files },
        BuiltinTools = [],
        BuiltinPlugins = [],
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        BackgroundTasks = null,
    };
    private static void AssertSelectedBridge(Agent agent) => Assert.IsNotType<Core.NotASandboxLocalEnvironment>(agent.Sandbox);
    private static ValueTask<string> InvokePython(Agent agent, CancellationToken token = default)
    {
        AssertSelectedBridge(agent); // Central second fence; no caller may accidentally skip the safety guard.
        var tool = Core.PythonRepl.Create();
        var input = JsonSerializer.SerializeToElement(new { code = "fake-code" });
        return tool.InvokeAsync(input, new(agent, new("code-call", tool.Spec.Name, input), new Dictionary<string, object?>()), token);
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("These fake tests must never invoke a model or process.");
    }
    private class FileSelection : ISandbox, IReadOnlyFileSystem, IDisposable
    {
        internal List<string> Reads { get; } = [];
        internal List<string> Lists { get; } = [];
        internal List<(string Path, string Content)> Writes { get; } = [];
        internal int Commands;
        internal int DisposeCalls;
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Reads.Add(path); throw new FileNotFoundException("No fake file.", path); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Writes.Add((path, content)); return default; }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Lists.Add(path); throw new DirectoryNotFoundException("No fake directory."); }
        public void Dispose() => DisposeCalls++;
    }
    private sealed class CommandSelection : FileSelection, ICommandSandbox
    {
        public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default) { Commands++; throw new InvalidOperationException("Code must not fall back to legacy command execution."); }
    }
    private sealed class CodeSelection : FileSelection, ICodeSandbox
    {
        internal string? Code, Language;
        internal Core.ExecuteOptions? Options;
        internal CancellationToken Token;
        internal int CodeCalls, IteratorDisposals;
        internal Exception? Failure;
        internal Action? BeforeMove;
        internal bool HoldDisposal;
        internal TaskCompletionSource<bool> DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
        {
            CodeCalls++; Code = code; Language = language; Options = options; Token = cancellationToken;
            return new CodeSequence(this);
        }
        private sealed class CodeSequence(CodeSelection owner) : IAsyncEnumerable<Core.SandboxStreamEvent>, IAsyncEnumerator<Core.SandboxStreamEvent>
        {
            private int _index;
            public Core.SandboxStreamEvent Current { get; private set; } = null!;
            public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync()
            {
                owner.BeforeMove?.Invoke();
                if (owner.Failure is { } failure) return new(Task.FromException<bool>(failure));
                Current = _index++ == 0 ? new Core.StreamChunk("selected", Core.StreamType.Stdout) : new Core.ExecutionResult(7, "selected", "");
                return new(_index <= 2);
            }
            public async ValueTask DisposeAsync()
            {
                owner.IteratorDisposals++; owner.DisposeEntered.TrySetResult(true);
                if (owner.HoldDisposal) await owner.ReleaseDisposal.Task.ConfigureAwait(false);
            }
        }
    }
    private sealed class CoreSelection : Core.Sandbox
    {
        internal bool SkillTree;
        internal List<string> Lists { get; } = [];
        internal List<string> Reads { get; } = [];
        internal int Commands, CodeCalls;
        internal string? Code, Language;
        internal Core.ExecuteOptions? Options;
        internal CancellationToken Token;
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) { Commands++; throw new InvalidOperationException("No command execution in this fake."); }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
        {
            CodeCalls++; Code = code; Language = language; Options = options; Token = cancellationToken;
            return Complete();
            static async IAsyncEnumerable<Core.SandboxStreamEvent> Complete() { await Task.CompletedTask; yield return new Core.ExecutionResult(7, "selected", ""); }
        }
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(path);
            return SkillTree && path == "./.agent/skills/routing/SKILL.md" ? new("---\nname: routing\ndescription: Selected core skill\n---\nSelected instructions.") : throw new FileNotFoundException("No fake file.", path);
        }
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Lists.Add(path);
            IReadOnlyList<FileEntry> entries = SkillTree ? path switch { "./.agent/skills" => [new("routing", true)], "./.agent/skills/routing" => [new("SKILL.md", false)], _ => throw new DirectoryNotFoundException("No fake directory.") } : throw new DirectoryNotFoundException("No fake directory.");
            return new(entries);
        }
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No byte read expected.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No byte write expected.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No delete expected.");
    }
}
