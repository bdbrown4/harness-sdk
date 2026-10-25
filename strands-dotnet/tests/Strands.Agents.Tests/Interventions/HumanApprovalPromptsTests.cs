using System.Text.Json;
using Strands.Agents.Interventions;

namespace Strands.Agents.Tests.Interventions;

public sealed class HumanApprovalPromptsTests
{
    [Theory]
    [InlineData(false, "(y/n)")]
    [InlineData(true, "(y/n/t)")]
    public async Task StdioWritesTheConfiguredSuffixTrimsInputAndLeavesBorrowedStreamsOpen(bool trust, string suffix)
    {
        var reader = new BorrowedReader("  YeS  \n"); var writer = new BorrowedWriter();
        var ask = HumanApprovalPrompts.Stdio(trust, reader, writer);
        Assert.Equal("YeS", (await ask("Approve?", default))?.GetString());
        Assert.Equal("Approve? " + suffix + ": ", writer.ToString());
        Assert.False(reader.Disposed); Assert.False(writer.Disposed);
        reader.Dispose(); writer.Dispose();
    }

    [Fact]
    public async Task CancelledReadKeepsTheInputSlotUntilDrainedBeforeASecondPrompt()
    {
        using var reader = new ControlledReader(); using var writer = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var ask = HumanApprovalPrompts.Stdio(reader: reader, writer: writer);
        var first = ask("first", cancellation.Token).AsTask();
        await Within(reader.FirstStarted.Task);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.Equal(cancellation.Token, error.CancellationToken);
        var second = ask("second", default).AsTask();
        Assert.False(second.IsCompleted); Assert.Equal("first (y/n): ", writer.ToString());
        reader.First.TrySetResult("discarded"); await Within(reader.SecondStarted.Task); reader.Second.TrySetResult("yes");
        Assert.Equal("yes", (await second)?.GetString()); Assert.Equal("first (y/n): second (y/n): ", writer.ToString());
    }

    [Fact]
    public async Task EndOfInputIsMissingWhilePreCancellationDoesNotWriteOrRead()
    {
        using var reader = new StringReader(""); using var writer = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var ask = HumanApprovalPrompts.Stdio(reader: reader, writer: writer);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask("cancelled", cancellation.Token).AsTask());
        Assert.Equal("", writer.ToString()); Assert.Null(await ask("eof", default));
    }

    [Fact]
    public async Task AReaderWithASynchronousReadLineAsyncDoesNotBlockCancellation()
    {
        using var reader = new BlockingReader(); using var writer = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var ask = HumanApprovalPrompts.Stdio(reader: reader, writer: writer);
        var response = ask("approve", cancellation.Token).AsTask();
        try
        {
            await Within(reader.Started.Task); cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response); Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { reader.Release.Set(); await Within(reader.Finished.Task); }
    }

    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class BorrowedReader(string text) : StringReader(text)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BorrowedWriter : StringWriter
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ControlledReader : TextReader
    {
        internal TaskCompletionSource<bool> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public override Task<string?> ReadLineAsync()
        {
            var count = Interlocked.Increment(ref _reads);
            if (count == 1) { FirstStarted.TrySetResult(true); return First.Task; }
            if (count == 2) { SecondStarted.TrySetResult(true); return Second.Task; }
            throw new InvalidOperationException("Unexpected extra read.");
        }
    }

    private sealed class BlockingReader : TextReader
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public override Task<string?> ReadLineAsync()
        {
            Started.TrySetResult(true); Release.Wait(); Finished.TrySetResult(true); return Task.FromResult<string?>("discarded");
        }
        protected override void Dispose(bool disposing) { Release.Dispose(); base.Dispose(disposing); }
    }
}
