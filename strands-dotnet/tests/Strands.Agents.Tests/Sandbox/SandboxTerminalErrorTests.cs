using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxTerminalErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProducerPrimarySurvivesJoinedCleanup(bool executeCode) => CheckAsync(executeCode, FailureKind.Producer);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingTerminalDiagnosticSurvivesJoinedCleanup(bool executeCode) => CheckAsync(executeCode, FailureKind.MissingTerminal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RequestedCancellationPrimarySurvivesJoinedCleanup(bool executeCode) => CheckAsync(executeCode, FailureKind.RequestedCancellation);

    private static async Task CheckAsync(bool executeCode, FailureKind kind)
    {
        using var caller = new CancellationTokenSource();
        Exception? primary = kind == FailureKind.RequestedCancellation ? new OperationCanceledException("caller cancellation", caller.Token)
            : kind == FailureKind.Producer ? new IOException("producer failed") : (Exception?)null;
        var cleanup = new IOException("iterator cleanup failed");
        var iterator = new ErrorIterator(kind == FailureKind.MissingTerminal, cleanup);
        var sandbox = new ErrorSandbox(iterator);
        var options = new Core.ExecuteOptions { WorkingDirectory = "fake-working-directory", MaximumOutputBytes = 17 };
        var operation = (executeCode ? sandbox.ExecuteCodeAsync("fake source", "fake-language", options, caller.Token)
            : sandbox.ExecuteAsync("fake command", options, caller.Token)).AsTask();
        Exception? caught = null;
        Exception? disposalError = null;
        try
        {
            Assert.Equal(1, iterator.MoveCalls);
            Assert.Equal(caller.Token, iterator.EnumerationToken);
            Assert.Equal(caller.Token, sandbox.InvocationToken);
            Assert.Same(options, sandbox.Options);
            Assert.Equal(executeCode ? "fake source" : "fake command", sandbox.Input);
            if (kind != FailureKind.MissingTerminal)
            {
                Assert.False(operation.IsCompleted);
                if (kind == FailureKind.RequestedCancellation) caller.Cancel();
                iterator.Move.TrySetException(primary!);
            }
            Assert.Same(iterator.Entered.Task, await Task.WhenAny(iterator.Entered.Task, Task.Delay(TimeSpan.FromSeconds(8))));
            Assert.False(operation.IsCompleted);
            iterator.Release.TrySetResult(true);
            caught = await Record.ExceptionAsync(async () => await operation);
            if (kind == FailureKind.MissingTerminal)
            {
                var missing = Assert.IsType<InvalidOperationException>(caught);
                Assert.Equal(executeCode ? "Sandbox source execution ended without a terminal result." : "Sandbox execution ended without a terminal result.", missing.Message);
            }
            else
            {
                Assert.Same(primary, caught);
                if (kind == FailureKind.RequestedCancellation) Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(caught).CancellationToken);
            }
            var attached = Assert.IsAssignableFrom<IEnumerable<Exception>>(caught!.Data["Strands.CleanupErrors"]);
            Assert.Same(cleanup, Assert.Single(attached));
        }
        finally
        {
            // Open every owned gate before joining either the operation or physical cleanup.
            if (primary is not null) iterator.Move.TrySetException(primary);
            iterator.Release.TrySetResult(true);
            if (iterator.Disposal is { } disposal) disposalError = await Record.ExceptionAsync(() => disposal);
            await Record.ExceptionAsync(async () => await operation);
            if (iterator.Disposal is { } finalDisposal) disposalError = await Record.ExceptionAsync(() => finalDisposal);
        }
        Assert.Same(cleanup, disposalError);
        Assert.Equal(1, iterator.MoveCalls);
        Assert.Equal(0, iterator.CurrentCalls);
        Assert.Equal(1, iterator.DisposeCalls);
        Assert.Equal(0, sandbox.Disposals);
    }

    private enum FailureKind { Producer, MissingTerminal, RequestedCancellation }

    private sealed class ErrorIterator(bool missingTerminal, Exception cleanup) : IAsyncEnumerable<Core.SandboxStreamEvent>, IAsyncEnumerator<Core.SandboxStreamEvent>
    {
        internal readonly TaskCompletionSource<bool> Move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Disposal;
        internal CancellationToken EnumerationToken;
        internal int MoveCalls;
        internal int CurrentCalls;
        internal int DisposeCalls;
        public Core.SandboxStreamEvent Current { get { CurrentCalls++; throw new InvalidOperationException("No Current belongs to a failed or empty enumeration."); } }
        public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) { EnumerationToken = cancellationToken; return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            MoveCalls++;
            if (MoveCalls != 1) throw new InvalidOperationException("No additional MoveNext belongs to a failed or empty enumeration.");
            return missingTerminal ? new(false) : new(Move.Task);
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
            throw cleanup;
        }
    }

    private sealed class ErrorSandbox(ErrorIterator iterator) : Core.Sandbox, IDisposable
    {
        internal CancellationToken InvocationToken;
        internal Core.ExecuteOptions? Options;
        internal string? Input;
        internal int Disposals;
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) { Input = command; Options = options; InvocationToken = cancellationToken; return iterator; }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) { Input = code; Options = options; InvocationToken = cancellationToken; return iterator; }
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal error tests.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal error tests.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal error tests.");
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal error tests.");
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem operation belongs to terminal error tests.");
        public void Dispose() => Disposals++;
    }
}
