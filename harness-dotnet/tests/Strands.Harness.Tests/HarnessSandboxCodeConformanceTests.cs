using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Strands.Agents;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Harness.Tests;

public sealed class HarnessSandboxCodeConformanceTests
{
    [Theory]
    [InlineData("null-code")]
    [InlineData("null-language")]
    [InlineData("empty-language")]
    [InlineData("non-ascii-language")]
    [InlineData("language-arguments")]
    [InlineData("language-path")]
    [InlineData("stdin")]
    [InlineData("zero-duration")]
    [InlineData("negative-duration")]
    [InlineData("excessive-duration")]
    [InlineData("zero-output")]
    [InlineData("negative-output")]
    [InlineData("null-environment")]
    [InlineData("bad-environment-name")]
    [InlineData("empty-environment-name")]
    [InlineData("nul-environment-value")]
    [InlineData("null-environment-value")]
    public async Task InvalidCodeInputCannotEnterTheSelectedCapability(string invalid)
    {
        var selected = new CodeFiles();
        await using var agent = await Create(selected);
        string code = "opaque source", language = "python3";
        Core.ExecuteOptions options = new();
        switch (invalid)
        {
            case "null-code": code = null!; break;
            case "null-language": language = null!; break;
            case "empty-language": language = ""; break;
            case "non-ascii-language": language = "pythön"; break;
            case "language-arguments": language = "python3 -c"; break;
            case "language-path": language = "/usr/bin/python3"; break;
            case "stdin": options = options with { StandardInput = [] }; break;
            case "zero-duration": options = options with { Timeout = TimeSpan.Zero }; break;
            case "negative-duration": options = options with { Timeout = TimeSpan.FromMilliseconds(-1) }; break;
            case "excessive-duration": options = options with { Timeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1) }; break;
            case "zero-output": options = options with { MaximumOutputBytes = 0 }; break;
            case "negative-output": options = options with { MaximumOutputBytes = -1 }; break;
            case "null-environment": options = options with { Environment = null! }; break;
            case "bad-environment-name": options = options with { Environment = new Dictionary<string, string> { ["BAD-NAME"] = "v" } }; break;
            case "empty-environment-name": options = options with { Environment = new Dictionary<string, string> { [""] = "v" } }; break;
            case "nul-environment-value": options = options with { Environment = new Dictionary<string, string> { ["KEY"] = "v\0" } }; break;
            case "null-environment-value": options = options with { Environment = new Dictionary<string, string> { ["KEY"] = null! } }; break;
        }
        var error = Record.Exception(() => agent.Sandbox.ExecuteCodeStreamingAsync(code, language, options));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, selected.CodeCalls);
        Assert.Equal(0, selected.FileCalls);
    }

    [Fact]
    public async Task PrecancelledCodeDoesNotEnterTheBorrowedBackendAndKeepsItsToken()
    {
        var selected = new CodeFiles();
        await using var agent = await Create(selected);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => agent.Sandbox.ExecuteCodeStreamingAsync("opaque", "python3", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, selected.CodeCalls);
    }

    [Fact]
    public async Task CodeOnlySelectionDoesNotGrantCommandsOrBinaryFilesystemAuthority()
    {
        var selected = new CodeFiles();
        await using var agent = await Create(selected);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await agent.Sandbox.ExecuteAsync("opaque"));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await agent.Sandbox.ReadBytesAsync("/selected/file"));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await agent.Sandbox.WriteBytesAsync("/selected/file", [1]));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await agent.Sandbox.DeleteFileAsync("/selected/file"));
        Assert.False(agent.FileSystem is IFileSystem);
        Assert.Equal(0, selected.CodeCalls);
        Assert.Equal(0, selected.FileCalls);
        Assert.Equal("selected text", await agent.Sandbox.ReadTextAsync("/selected/file"));
        Assert.Equal("file", Assert.Single(await agent.Sandbox.ListFilesAsync("/selected")).Name);
        Assert.Equal(2, selected.FileCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task LegacyCommandResultAndCaptureSettingsStayOnTheSelectedCapability(int exitCode)
    {
        var selected = new Commands { Result = new("stdout", "stderr", exitCode, true) };
        await using var agent = await Create(selected);
        using var caller = new CancellationTokenSource();
        var events = new List<Core.SandboxStreamEvent>();
        await foreach (var item in agent.Sandbox.ExecuteStreamingAsync("opaque command", new() { Timeout = TimeSpan.FromSeconds(7), MaximumOutputBytes = 123 }, caller.Token)) events.Add(item);
        Assert.Collection(events,
            item => Assert.Equal(new Core.StreamChunk("stdout", Core.StreamType.Stdout), item),
            item => Assert.Equal(new Core.StreamChunk("stderr", Core.StreamType.Stderr), item),
            item => Assert.Equal(new Core.ExecutionResult(exitCode, "stdout", "stderr") { OutputTruncated = true }, item));
        Assert.Equal("opaque command", selected.Command);
        Assert.Equal(caller.Token, selected.Token);
        Assert.Equal(TimeSpan.FromSeconds(7), selected.Options!.Timeout);
        Assert.Equal(123, selected.Options.MaxOutputBytes);
    }

    [Fact]
    public async Task LegacyCommandTimeoutRetainsTheSelectedCauseAndCleanupDiagnostics()
    {
        var cause = new IOException("transport cause");
        var cleanup = new IOException("cleanup");
        var cleanupTask = Task.FromResult<IReadOnlyList<Exception>>(new[] { cleanup });
        var original = new SandboxTimeoutException(TimeSpan.FromSeconds(2), new("partial", "err", 124), cause);
        original.Data["Strands.CleanupTask"] = cleanupTask;
        original.Data["Strands.CleanupErrors"] = new[] { cleanup };
        var selected = new Commands { Failure = original };
        await using var agent = await Create(selected);
        var converted = await Assert.ThrowsAsync<Core.SandboxTimeoutException>(async () => await agent.Sandbox.ExecuteAsync("opaque"));
        Assert.Same(original, converted.InnerException);
        Assert.Same(cause, converted.InnerException!.InnerException);
        Assert.Same(cleanupTask, converted.Data["Strands.CleanupTask"]);
        Assert.Same(cleanup, Assert.Single((IEnumerable<Exception>)converted.Data["Strands.CleanupErrors"]!));
        Assert.Equal("partial", converted.PartialResult.Stdout);
        Assert.Equal(124, converted.PartialResult.ExitCode);
    }

    [Theory]
    [InlineData("stdin")]
    [InlineData("cwd")]
    [InlineData("environment")]
    public async Task UnrepresentableCommandOptionsAreRejectedBeforeSelectedCallbacks(string property)
    {
        var selected = new Commands();
        await using var agent = await Create(selected);
        var options = property switch
        {
            "stdin" => new Core.ExecuteOptions { StandardInput = [] },
            "cwd" => new Core.ExecuteOptions { WorkingDirectory = "/selected" },
            _ => new Core.ExecuteOptions { Environment = new Dictionary<string, string> { ["KEY"] = "value" } },
        };
        Assert.Throws<NotSupportedException>(() => agent.Sandbox.ExecuteStreamingAsync("opaque", options));
        Assert.Equal(0, selected.CommandCalls);
    }

    [Theory]
    [InlineData("null-command")]
    [InlineData("nul-command")]
    [InlineData("duration")]
    [InlineData("output")]
    [InlineData("environment-name")]
    [InlineData("environment-value")]
    public async Task InvalidCommandInputNeverEntersTheSelectedCallback(string invalid)
    {
        var selected = new Commands();
        await using var agent = await Create(selected);
        var command = invalid == "null-command" ? null! : invalid == "nul-command" ? "opaque\0" : "opaque";
        var options = invalid switch
        {
            "duration" => new Core.ExecuteOptions { Timeout = TimeSpan.Zero },
            "output" => new Core.ExecuteOptions { MaximumOutputBytes = 0 },
            "environment-name" => new Core.ExecuteOptions { Environment = new Dictionary<string, string> { ["BAD-NAME"] = "v" } },
            "environment-value" => new Core.ExecuteOptions { Environment = new Dictionary<string, string> { ["KEY"] = "v\0" } },
            _ => new Core.ExecuteOptions(),
        };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => agent.Sandbox.ExecuteStreamingAsync(command, options)));
        Assert.Equal(0, selected.CommandCalls);
    }

    [Fact]
    public async Task PrecancelledCommandPreservesTheCallerTokenWithoutEnteringTheBackend()
    {
        var selected = new Commands();
        await using var agent = await Create(selected);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => agent.Sandbox.ExecuteStreamingAsync("opaque", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, selected.CommandCalls);
    }

    [Fact]
    public async Task BinaryWritesDetachBeforeAnAwaitAndReadsRemainIndependent()
    {
        var selected = new BinaryFiles { HoldWrite = true };
        await using var agent = await Create(selected);
        byte[] input = [0, 255, 10];
        var writing = agent.Sandbox.WriteBytesAsync("/selected/file", input).AsTask();
        try
        {
            await Ready(selected.WriteEntered.Task);
            input[0] = 99;
            Assert.NotSame(input, selected.EnteredBytes);
            Assert.Equal(new byte[] { 0, 255, 10 }, selected.EnteredBytes);
            Assert.False(writing.IsCompleted);
        }
        finally { selected.ReleaseWrite.TrySetResult(true); await writing; }
        var first = await agent.Sandbox.ReadBytesAsync("/selected/file");
        first[0] = 42;
        Assert.Equal(new byte[] { 0, 255, 10 }, await agent.Sandbox.ReadBytesAsync("/selected/file"));
        await agent.Sandbox.DeleteFileAsync("/selected/file");
        Assert.Empty(selected.Bytes);
        Assert.All(selected.BytePaths, path => Assert.Equal("/selected/file", path));
        Assert.Equal(0, selected.FileCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveFailureKeepsItsIdentityWhenIteratorCleanupAlsoFails(bool cancellation)
    {
        using var caller = new CancellationTokenSource();
        Exception primary = cancellation ? new OperationCanceledException("selected", null, caller.Token) : new IOException("selected");
        var cleanup = new IOException("dispose");
        var selected = new CodeFiles { MoveFailure = primary, DisposeFailure = cleanup };
        await using var agent = await Create(selected);
        var caught = await Record.ExceptionAsync(async () => await agent.Sandbox.ExecuteCodeAsync("opaque", "python3", cancellationToken: caller.Token));
        Assert.Same(primary, caught);
        Assert.Same(cleanup, Assert.Single((IEnumerable<Exception>)caught!.Data["Strands.CleanupErrors"]!));
        Assert.Equal(1, selected.IteratorDisposals);
        Assert.Equal(caller.Token, selected.Token);
    }

    [Fact]
    public async Task SamePrimaryAndCleanupExceptionCannotBecomeASelfReference()
    {
        var failure = new IOException("same");
        var selected = new CodeFiles { MoveFailure = failure, DisposeFailure = failure };
        await using var agent = await Create(selected);
        Assert.Same(failure, await Record.ExceptionAsync(async () => await agent.Sandbox.ExecuteCodeAsync("opaque", "python3")));
        Assert.Null(failure.Data["Strands.CleanupErrors"]);
    }

    [Fact]
    public async Task TerminalResultCannotHideItsIteratorDisposalFailure()
    {
        var cleanup = new IOException("dispose after terminal");
        var selected = new CodeFiles { DisposeFailure = cleanup };
        await using var agent = await Create(selected);
        Assert.Same(cleanup, await Record.ExceptionAsync(async () => await agent.Sandbox.ExecuteCodeAsync("opaque", "python3")));
        Assert.Equal(1, selected.IteratorDisposals);
    }

    [Fact]
    public async Task MissingTerminalResultIsARealFailureAndStillDisposesTheIterator()
    {
        var selected = new CodeFiles { Empty = true };
        await using var agent = await Create(selected);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.Sandbox.ExecuteCodeAsync("opaque", "python3"));
        Assert.Equal(1, selected.IteratorDisposals);
    }

    [Fact]
    public async Task CancellationRemainsPendingUntilPhysicalIteratorCleanupAndDoesNotOwnCallerOrBackend()
    {
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException("selected", null, caller.Token);
        var selected = new CodeFiles { MoveFailure = primary, BeforeMove = caller.Cancel, HoldDispose = true };
        await using var agent = await Create(selected);
        var operation = agent.Sandbox.ExecuteCodeAsync("opaque", "python3", cancellationToken: caller.Token).AsTask();
        try { await Ready(selected.DisposeEntered.Task); Assert.False(operation.IsCompleted); }
        finally { selected.ReleaseDispose.TrySetResult(true); Assert.Same(primary, await Record.ExceptionAsync(async () => await operation)); }
        await agent.DisposeAsync();
        caller.Cancel(); // Agent never owns this CTS.
        Assert.Equal(0, selected.DisposeCalls);
        Assert.Equal(1, selected.IteratorDisposals);
    }

    [Fact]
    public async Task InheritedSelectedAuthorityAndExplicitResetKeepTheOldMaterializedFilesystemProfile()
    {
        var selected = new CodeFiles();
        await using var parent = await Create(selected);
        await using var inherited = parent.CreateChild();
        var reboundCore = new NoCodeCore();
        await using var rebound = parent.CreateChild(options => options with { Sandbox = reboundCore });
        await using var reset = parent.CreateChild(options => options with { Sandbox = null });
        Assert.Same(parent.Sandbox, inherited.Sandbox);
        Assert.Same(reboundCore, rebound.Sandbox);
        Assert.Same(parent.FileSystem, rebound.FileSystem); // Existing Harness materialization is intentionally independent.
        Assert.IsType<Core.NotASandboxLocalEnvironment>(reset.Sandbox); // Object only; never execute it.
        Assert.Same(parent.FileSystem, reset.FileSystem);
        Assert.NotSame(parent.Sandbox, reset.Sandbox);
        Assert.Equal(0, selected.CodeCalls);
        Assert.Equal(0, selected.FileCalls);
    }

    [Fact]
    public async Task BorrowedSelectionSurvivesFailedInitializationAndParentChildDisposal()
    {
        var selected = new CodeFiles();
        var original = new IOException("initialize");
        var options = Options(selected) with { Plugins = [new FailingPlugin(original)] };
        Assert.Same(original, await Record.ExceptionAsync(async () => await Harness.CreateAsync(options)));
        Assert.Equal(0, selected.DisposeCalls);
        var parent = await Create(selected);
        var child = parent.CreateChild();
        await child.DisposeAsync(); await parent.DisposeAsync();
        Assert.Equal(0, selected.DisposeCalls);
        Assert.Equal(0, selected.CodeCalls);
    }

    [Fact]
    public async Task LocalPolicyIsFrozenLazilyWithoutAProcessAndNeverUsesTheShellExecutableAsInterpreter()
    {
        var environment = new Dictionary<string, string> { ["CONFIGURED"] = "original" };
        var directory = Path.GetFullPath(Path.GetTempPath());
        var selected = new LocalFileSandbox(shellOptions: new() { ExecutablePath = Path.Combine(directory, "never-invoked-shell.exe"), WorkingDirectory = directory, Environment = environment, MaxDuration = TimeSpan.FromSeconds(17), MaxOutputBytes = 555 });
        environment["CONFIGURED"] = "mutated";
        var lazy = Assert.IsType<Lazy<Core.NotASandboxLocalEnvironment>>(typeof(LocalFileSandbox).GetField("_codeEnvironment", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selected));
        Assert.False(lazy.IsValueCreated);
        await using var agent = await Harness.CreateAsync(Options(selected));
        Assert.False(lazy.IsValueCreated);
        var local = lazy.Value; // Constructor/configuration only: never request/enumerate a stream.
        Assert.Same(local, lazy.Value);
        var process = Assert.IsType<Core.SandboxProcessOptions>(typeof(Core.NotASandboxLocalEnvironment).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(local));
        Assert.Null(process.ExecutablePath);
        Assert.Equal(TimeSpan.FromSeconds(17), process.MaximumDuration);
        Assert.Equal(555, process.MaximumOutputBytes);
        Assert.Equal("original", process.Environment["CONFIGURED"]);
        Assert.Equal(directory, typeof(Core.NotASandboxLocalEnvironment).GetField("_workingDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(local));
    }

    private static HarnessOptions Options(ISandbox selected) => new()
    {
        Sandbox = selected,
        AgentOptions = new() { Model = new NoModel() },
        BuiltinTools = [],
        BuiltinPlugins = [],
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        BackgroundTasks = null,
    };
    private static async Task<Agent> Create(ISandbox selected)
    {
        var agent = await Harness.CreateAsync(Options(selected));
        Assert.IsNotType<Core.NotASandboxLocalEnvironment>(agent.Sandbox); // No implicit host execution in any conformance case.
        return agent;
    }
    private static async Task Ready(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model or real process in these fake conformance tests.");
    }
    private sealed class FailingPlugin(Exception failure) : IPlugin
    {
        public string Name => "fixture-failure";
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => new(Task.FromException(failure));
    }
    private class Files : ISandbox, IReadOnlyFileSystem, IDisposable
    {
        internal int FileCalls, DisposeCalls;
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); FileCalls++; return new("selected text"); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); FileCalls++; return default; }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); FileCalls++; return new(new[] { new FileEntry("file", false) }); }
        public void Dispose() => DisposeCalls++;
    }
    private sealed class Commands : Files, ICommandSandbox
    {
        internal SandboxExecutionResult Result = new("", "", 0);
        internal Exception? Failure;
        internal int CommandCalls;
        internal string? Command;
        internal ShellExecutionOptions? Options;
        internal CancellationToken Token;
        public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default)
        {
            CommandCalls++; Command = command; Options = options; Token = cancellationToken;
            return Failure is { } error ? new(Task.FromException<SandboxExecutionResult>(error)) : new(Result);
        }
    }
    private sealed class BinaryFiles : Files, IFileSystem
    {
        internal bool HoldWrite;
        internal byte[]? EnteredBytes;
        internal byte[] Bytes = [];
        internal List<string> BytePaths { get; } = [];
        internal TaskCompletionSource<bool> WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); BytePaths.Add(path); return new((byte[])Bytes.Clone()); }
        public async ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            BytePaths.Add(path); EnteredBytes = data; WriteEntered.TrySetResult(true);
            if (HoldWrite) await ReleaseWrite.Task;
            Bytes = (byte[])data.Clone();
        }
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); BytePaths.Add(path); Bytes = []; return default; }
    }
    private sealed class CodeFiles : Files, ICodeSandbox
    {
        internal int CodeCalls, IteratorDisposals;
        internal Exception? MoveFailure, DisposeFailure;
        internal bool Empty, HoldDispose;
        internal Action? BeforeMove;
        internal CancellationToken Token;
        internal TaskCompletionSource<bool> DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
        { CodeCalls++; Token = cancellationToken; return new Sequence(this); }
        private sealed class Sequence(CodeFiles owner) : IAsyncEnumerable<Core.SandboxStreamEvent>, IAsyncEnumerator<Core.SandboxStreamEvent>
        {
            private bool _completed;
            public Core.SandboxStreamEvent Current => new Core.ExecutionResult(0, "selected", "");
            public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync()
            {
                owner.BeforeMove?.Invoke();
                if (owner.MoveFailure is { } error) return new(Task.FromException<bool>(error));
                var available = !owner.Empty && !_completed; _completed = true; return new(available);
            }
            public async ValueTask DisposeAsync()
            {
                owner.IteratorDisposals++; owner.DisposeEntered.TrySetResult(true);
                if (owner.HoldDispose) await owner.ReleaseDispose.Task;
                if (owner.DisposeFailure is { } error) throw error;
            }
        }
    }
    private sealed class NoCodeCore : Core.Sandbox
    {
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process.");
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No I/O.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No I/O.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No I/O.");
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No I/O.");
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No I/O.");
    }
}
