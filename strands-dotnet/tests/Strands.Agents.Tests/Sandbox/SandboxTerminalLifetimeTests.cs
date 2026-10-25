using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxTerminalLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCompletionWaitsForPhysicalIteratorDispose(bool executeCode)
    {
        var iterator = new TerminalIterator();
        var sandbox = new TerminalSandbox(iterator);
        using var caller = new CancellationTokenSource();
        var operation = Invoke(sandbox, executeCode, caller.Token).AsTask();
        Core.ExecutionResult? result = null;
        Exception? operationError = null;
        Exception? disposalError = null;
        try
        {
            Assert.True(iterator.Entered.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
            Assert.Equal(caller.Token, iterator.EnumerationToken);
            Assert.Equal(caller.Token, sandbox.InvocationToken);
        }
        finally
        {
            iterator.Release.TrySetResult(true);
            if (iterator.Disposal is { } disposal) disposalError = await Record.ExceptionAsync(() => disposal);
            operationError = await Record.ExceptionAsync(async () => result = await operation);
        }
        Assert.Null(disposalError);
        Assert.Null(operationError);
        Assert.Same(iterator.Result, result);
        Assert.Equal(1, iterator.DisposeCalls);
        Assert.Equal(1, iterator.MoveCalls);
        Assert.Equal(0, sandbox.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalIteratorCleanupFailureRemainsVisible(bool executeCode)
    {
        var cleanup = new IOException("terminal iterator cleanup failed");
        var iterator = new TerminalIterator { Cleanup = cleanup };
        var sandbox = new TerminalSandbox(iterator);
        var operation = Invoke(sandbox, executeCode, default).AsTask();
        Exception? disposalError = null;
        try
        {
            Assert.True(iterator.Entered.Task.IsCompleted);
            iterator.Release.TrySetResult(true);
            var caught = await Record.ExceptionAsync(async () => await operation);
            Assert.Same(cleanup, caught);
        }
        finally
        {
            iterator.Release.TrySetResult(true);
            if (iterator.Disposal is { } disposal) disposalError = await Record.ExceptionAsync(() => disposal);
            await Record.ExceptionAsync(async () => await operation);
        }
        Assert.Same(cleanup, disposalError);
        Assert.Equal(1, iterator.DisposeCalls);
        Assert.Equal(1, iterator.MoveCalls);
        Assert.Equal(0, sandbox.Disposals);
    }

    private static ValueTask<Core.ExecutionResult> Invoke(TerminalSandbox sandbox, bool executeCode, CancellationToken cancellationToken) =>
        executeCode ? sandbox.ExecuteCodeAsync("fake source", "fake-language", cancellationToken: cancellationToken) : sandbox.ExecuteAsync("fake command", cancellationToken: cancellationToken);

    private sealed class TerminalIterator : IAsyncEnumerable<Core.SandboxStreamEvent>, IAsyncEnumerator<Core.SandboxStreamEvent>
    {
        internal readonly Core.ExecutionResult Result = new(7, "captured stdout", "captured stderr");
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Cleanup;
        internal Task? Disposal;
        internal CancellationToken EnumerationToken;
        internal int MoveCalls;
        internal int DisposeCalls;
        public Core.SandboxStreamEvent Current => Result;
        public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) { EnumerationToken = cancellationToken; return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            MoveCalls++;
            if (MoveCalls != 1) throw new InvalidOperationException("No MoveNext belongs after the terminal event.");
            return new(true);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Disposal = CloseAsync();
            return new(Disposal);
        }
        private async Task CloseAsync()
        {
            Entered.TrySetResult(true);
            await Release.Task;
            if (Cleanup is not null) throw Cleanup;
        }
    }

    private sealed class TerminalSandbox(TerminalIterator iterator) : Core.Sandbox, IDisposable
    {
        internal CancellationToken InvocationToken;
        internal int Disposals;
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) { InvocationToken = cancellationToken; return iterator; }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) { InvocationToken = cancellationToken; return iterator; }
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal lifetime tests.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal lifetime tests.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal lifetime tests.");
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal lifetime tests.");
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal lifetime tests.");
        public void Dispose() => Disposals++;
    }
}
