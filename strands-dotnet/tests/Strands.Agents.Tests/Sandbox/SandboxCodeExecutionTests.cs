using System.Runtime.CompilerServices;
using System.Text;
using Strands.Agents.Files;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxCodeExecutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("python 3")]
    [InlineData("python/3")]
    [InlineData("python\\3")]
    [InlineData("python;echo")]
    [InlineData("python\n")]
    [InlineData("python雪")]
    public void InvalidInterpreterNeverEntersTheSelectedBackend(string? language)
    {
        var sandbox = new SourceExecutionBackend();
        Assert.ThrowsAny<ArgumentException>(() => sandbox.ExecuteCodeStreamingAsync("source", language!));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Theory]
    [InlineData("python3.12")]
    [InlineData("PYTHON-3_12")]
    [InlineData(".")]
    public async Task SourceValidInterpreterTokensAndEmptySourceRemainAllowed(string language)
    {
        var sandbox = new SourceExecutionBackend();
        await sandbox.ExecuteCodeAsync("", language);
        Assert.Equal(language, sandbox.Command);
        Assert.Empty(sandbox.Options!.StandardInput!);
    }

    [Fact]
    public void NullSourceFailsBeforeBackendEntry()
    {
        var sandbox = new SourceExecutionBackend();
        var error = Assert.Throws<ArgumentNullException>(() => sandbox.ExecuteCodeStreamingAsync(null!, "python3"));
        Assert.Equal("code", error.ParamName);
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Theory]
    [InlineData("stdin-empty")]
    [InlineData("stdin-data")]
    [InlineData("zero-timeout")]
    [InlineData("negative-timeout")]
    [InlineData("timer-overflow")]
    [InlineData("zero-capture")]
    [InlineData("negative-capture")]
    [InlineData("null-environment")]
    [InlineData("invalid-environment-name")]
    [InlineData("null-environment-value")]
    public void InvalidCodeOptionsFailBeforeAnyBackendCallback(string condition)
    {
        var options = condition switch
        {
            "stdin-empty" => new Agents.Sandbox.ExecuteOptions { StandardInput = [] },
            "stdin-data" => new Agents.Sandbox.ExecuteOptions { StandardInput = [1] },
            "zero-timeout" => new Agents.Sandbox.ExecuteOptions { Timeout = TimeSpan.Zero },
            "negative-timeout" => new Agents.Sandbox.ExecuteOptions { Timeout = TimeSpan.FromSeconds(-1) },
            "timer-overflow" => new Agents.Sandbox.ExecuteOptions { Timeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1) },
            "zero-capture" => new Agents.Sandbox.ExecuteOptions { MaximumOutputBytes = 0 },
            "negative-capture" => new Agents.Sandbox.ExecuteOptions { MaximumOutputBytes = -1 },
            "null-environment" => new Agents.Sandbox.ExecuteOptions { Environment = null! },
            "invalid-environment-name" => new Agents.Sandbox.ExecuteOptions { Environment = new Dictionary<string, string> { ["VALUE-INVALID"] = "x" } },
            _ => new Agents.Sandbox.ExecuteOptions { Environment = new Dictionary<string, string> { ["VALUE"] = null! } },
        };
        var sandbox = new SourceExecutionBackend();
        Assert.ThrowsAny<ArgumentException>(() => sandbox.ExecuteCodeStreamingAsync("source", "python3", options));
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Fact]
    public void AlreadyCanceledCodeCallPreservesExactTokenWithoutBackendEntry()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var sandbox = new SourceExecutionBackend();
        var error = Assert.Throws<OperationCanceledException>(() => sandbox.ExecuteCodeStreamingAsync("source", "python3", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, sandbox.CommandCalls);
    }

    [Fact]
    public async Task RawCodeStreamPreservesChunkOrderAndTerminalObject()
    {
        var first = new Agents.Sandbox.StreamChunk("first雪", Agents.Sandbox.StreamType.Stdout);
        var second = new Agents.Sandbox.StreamChunk("second", Agents.Sandbox.StreamType.Stderr);
        var terminal = new Agents.Sandbox.ExecutionResult(7, "first雪", "second");
        var sandbox = new SourceExecutionBackend { Events = [first, second, terminal] };
        var received = new List<Agents.Sandbox.SandboxStreamEvent>();
        await foreach (var item in sandbox.ExecuteCodeStreamingAsync("source", "python3")) received.Add(item);
        Assert.Equal(3, received.Count);
        Assert.Same(first, received[0]);
        Assert.Same(second, received[1]);
        Assert.Same(terminal, received[2]);
        Assert.True(sandbox.StreamDisposed);
    }

    [Fact]
    public async Task AggregateHonorsTheCodeOverrideWithoutFallingBackToCommands()
    {
        using var caller = new CancellationTokenSource();
        var stream = new ControlledSourceIterator();
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        Assert.Same(stream.Result, await sandbox.ExecuteCodeAsync("source", "python3", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, stream.EnumeratorToken);
        Assert.Equal(0, sandbox.CommandCalls);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTerminalRemainsPrimaryEvenWhenCleanupFails(bool cleanupFails)
    {
        var cleanup = new IOException("cleanup after empty stream");
        var stream = new ControlledSourceIterator { Empty = true, DisposeFailure = cleanupFails ? cleanup : null };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.ExecuteCodeAsync("source", "python3"));
        Assert.Contains("without a terminal result", error.Message, StringComparison.Ordinal);
        Assert.True(stream.Disposed);
        if (cleanupFails) Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task FirstTerminalStopsFurtherReadsAndCleanupFailurePreventsSuccess()
    {
        var cleanup = new IOException("terminal cleanup failed");
        var stream = new ControlledSourceIterator { DisposeFailure = cleanup };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        Assert.Same(cleanup, await Record.ExceptionAsync(async () => await sandbox.ExecuteCodeAsync("source", "python3")));
        Assert.Equal(1, stream.Moves);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task CurrentGetterFailureRetainsPrimaryAndItsIndependentCleanup()
    {
        var primary = new IOException("current getter failure");
        var cleanup = new IOException("current getter cleanup");
        var stream = new ControlledSourceIterator { CurrentFailure = primary, DisposeFailure = cleanup };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        Assert.Same(primary, await Record.ExceptionAsync(async () => await sandbox.ExecuteCodeAsync("source", "python3")));
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupThrowingThePrimaryAgainCannotEraseItsOriginalMoveStack(bool cancellation)
    {
        using var caller = new CancellationTokenSource();
        Exception primary = cancellation ? new OperationCanceledException("same cancellation", caller.Token) : new Agents.Sandbox.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(124, "partial", ""));
        var stream = new ControlledSourceIterator { MoveFailure = primary, DisposeFailure = primary };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var observed = await Record.ExceptionAsync(async () => await sandbox.ExecuteCodeAsync("source", "python3"));
        Assert.Same(primary, observed);
        Assert.Contains(nameof(ControlledSourceIterator.MoveNextAsync), observed!.StackTrace, StringComparison.Ordinal);
        Assert.Null(observed.Data["Strands.CleanupErrors"]);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateRetainsMoveFailureWhenIteratorCleanupAlsoFails(bool cancellation)
    {
        using var caller = new CancellationTokenSource();
        Exception primary = cancellation
            ? new OperationCanceledException("selected caller cancellation", null, caller.Token)
            : new Agents.Sandbox.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(124, "partial", "error"));
        var cleanup = new IOException("independent iterator cleanup");
        if (!cancellation) primary.Data["Strands.CleanupErrors"] = new[] { cleanup };
        primary.Data["selected diagnostic"] = "retained";
        var stream = new ControlledSourceIterator
        {
            MoveFailure = primary,
            DisposeFailure = cleanup,
            BeforeMove = cancellation ? () => caller.Cancel() : null,
        };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var observed = await Record.ExceptionAsync(async () => await sandbox.ExecuteCodeAsync("source", "python3", cancellationToken: caller.Token));
        Assert.Same(primary, observed);
        Assert.Equal("retained", observed!.Data["selected diagnostic"]);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(observed.Data["Strands.CleanupErrors"])));
        Assert.Contains(nameof(SourceExecutionBackend.ThrowFromSelectedBackend), observed.StackTrace, StringComparison.Ordinal);
        if (cancellation) Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(observed).CancellationToken);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task AggregateWaitsForPhysicalIteratorReleaseAfterItsTerminalResult()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ControlledSourceIterator { DisposeRelease = release.Task };
        var sandbox = new SourceExecutionBackend { CodeStream = stream };
        var operation = sandbox.ExecuteCodeAsync("source", "python3").AsTask();
        try
        {
            Assert.Same(stream.DisposeEntered.Task, await Task.WhenAny(stream.DisposeEntered.Task, Task.Delay(TimeSpan.FromSeconds(8))));
            Assert.False(operation.IsCompleted);
            release.TrySetResult(true);
            Assert.Same(stream.Result, await operation);
            Assert.True(stream.Disposed);
        }
        finally { release.TrySetResult(true); await operation; }
    }

    [Fact]
    public async Task SourceUsesSelectedBackendWithDetachedInputEnvironmentAndExactToken()
    {
        using var cancellation = new CancellationTokenSource();
        var environment = new Dictionary<string, string> { ["VALUE"] = "original" };
        var sandbox = new SourceExecutionBackend();
        const string code = "print('雪'); # ' \" $HOME\n\0tail";
        var options = new Agents.Sandbox.ExecuteOptions
        {
            Environment = environment,
            WorkingDirectory = "/selected",
            Timeout = TimeSpan.FromSeconds(7),
            MaximumOutputBytes = 4096,
        };
        var operation = sandbox.ExecuteCodeStreamingAsync(code, "python3.12", options, cancellation.Token);
        environment["VALUE"] = "changed";
        await using var iterator = operation.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("python3.12", sandbox.Command);
        Assert.Equal(Encoding.UTF8.GetBytes(code), sandbox.Options!.StandardInput);
        Assert.Equal("original", sandbox.Options.Environment["VALUE"]);
        Assert.NotSame(options, sandbox.Options);
        Assert.NotSame(environment, sandbox.Options.Environment);
        Assert.Equal("/selected", sandbox.Options.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(7), sandbox.Options.Timeout);
        Assert.Equal(4096, sandbox.Options.MaximumOutputBytes);
        Assert.Equal(cancellation.Token, sandbox.Token);
    }
}

internal sealed class SourceExecutionBackend : Agents.Sandbox.Sandbox, IAsyncDisposable
{
    internal int CommandCalls;
    internal int DisposeCalls;
    internal bool StreamDisposed;
    internal IReadOnlyList<Agents.Sandbox.SandboxStreamEvent>? Events;
    internal string? Command;
    internal Agents.Sandbox.ExecuteOptions? Options;
    internal CancellationToken Token;
    internal string? CodeSource;
    internal Agents.Sandbox.ExecutionResult Result = new(0, "out", "err");
    internal Exception? Failure;
    internal Action? BeforeFailure;
    internal IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent>? CodeStream;

    public override IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        CodeSource = code;
        return CodeStream ?? base.ExecuteCodeStreamingAsync(code, language, options, cancellationToken);
    }

    public override IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent> ExecuteStreamingAsync(string command, Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        CommandCalls++;
        Command = command;
        Options = options;
        Token = cancellationToken;
        return Stream(cancellationToken);
    }

    private async IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        try
        {
            if (Failure is { } error) { BeforeFailure?.Invoke(); ThrowFromSelectedBackend(error); }
            if (Events is { } events) { foreach (var item in events) yield return item; }
            else yield return Result;
        }
        finally { StreamDisposed = true; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowFromSelectedBackend(Exception error) => throw error;

    public ValueTask DisposeAsync() { DisposeCalls++; return default; }

    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ControlledSourceIterator : IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent>, IAsyncEnumerator<Agents.Sandbox.SandboxStreamEvent>
{
    internal Exception? MoveFailure;
    internal Exception? DisposeFailure;
    internal Action? BeforeMove;
    internal Task? DisposeRelease;
    internal Task? MoveRelease;
    internal bool Disposed;
    internal bool Empty;
    internal int Moves;
    internal Exception? CurrentFailure;
    internal CancellationToken EnumeratorToken;
    internal readonly Agents.Sandbox.ExecutionResult Result = new(0, "terminal", "");
    internal readonly TaskCompletionSource<bool> DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<bool> MoveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Agents.Sandbox.SandboxStreamEvent Current
    {
        get { if (CurrentFailure is { } error) SourceExecutionBackend.ThrowFromSelectedBackend(error); return Result; }
    }
    public IAsyncEnumerator<Agents.Sandbox.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) { EnumeratorToken = cancellationToken; return this; }
    public async ValueTask<bool> MoveNextAsync()
    {
        Moves++;
        MoveEntered.TrySetResult(true);
        if (MoveRelease is { } release) await release.ConfigureAwait(false);
        BeforeMove?.Invoke();
        if (MoveFailure is { } error) SourceExecutionBackend.ThrowFromSelectedBackend(error);
        return !Empty;
    }
    public async ValueTask DisposeAsync()
    {
        DisposeEntered.TrySetResult(true);
        if (DisposeRelease is { } release) await release.ConfigureAwait(false);
        Disposed = true;
        if (DisposeFailure is { } error) SourceExecutionBackend.ThrowFromSelectedBackend(error);
    }
}
