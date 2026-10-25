using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiConsoleFeatureTests
{
    [Fact]
    public async Task RunConsumesWholeAsyncInputsAndCompletesNormallyAtEof()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new CompletingInput([
            new BidiMessage([new TextBlock("hello")]),
            new BidiMessage([new TextBlock("  航海 Δ  ")])]);
        var stopObserved = false;
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { Assert.True(input.Disposed); stopObserved = true; return default; });
        Task? run = null;
        try
        {
            run = agent.RunAsync(input, []);
            await SignalOrRun(input.Disposing.Task, run);
            Assert.False(run.IsCompleted); Assert.False(input.Disposed);
            input.ReleaseDisposal.TrySetResult(true);
            await Within(Completion(run));
            Assert.Equal(new[] { "hello", "  航海 Δ  " }, model.Inputs.Select(Text));
            Assert.True(input.Disposed); Assert.Equal(1, input.Disposals);
            Assert.True(stopObserved); Assert.True(model.ReaderExited.Task.IsCompleted);
            Assert.Equal(1, model.Stops); Assert.Equal(0, model.Disposals); Assert.Empty(host.Messages);
        }
        finally
        {
            input.ReleaseDisposal.TrySetResult(true);
            await agent.StopAsync(); await Join(run);
        }
    }

    [Fact]
    public async Task LineCapabilityPreservesWholeUserTextAndEndsAtNull()
    {
        var lines = new Queue<string?>(["", "hello", " \t界 Δ ", null]);
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var collecting = Collect(BidiConsole.Input(token =>
        {
            Assert.Equal(cancellation.Token, token); reads++;
            return new ValueTask<string?>(lines.Dequeue());
        }, cancellation.Token), cancellation.Token);
        try
        {
            var values = await Within(collecting);
            Assert.Equal(new[] { "hello", " \t界 Δ " }, values.Select(Text));
            Assert.Equal(4, reads); Assert.Empty(lines);
        }
        finally { cancellation.Cancel(); await Join(collecting); }
    }

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.Z)]
    public async Task KeyboardInputHandlesEditingUnicodeAndEndOfInput(ConsoleKey eof)
    {
        var keys = new Queue<ConsoleKeyInfo>([
            Character('a'), Character('b'), new('\b', ConsoleKey.Backspace, false, false, false),
            Character('界'), new('\r', ConsoleKey.Enter, false, false, false),
            Character(' '), Character('\t'), new('\r', ConsoleKey.Enter, false, false, false),
            new(eof == ConsoleKey.D ? '\u0004' : '\u001a', eof, false, false, true)]);
        var reads = 0; var available = false; var writer = new BorrowedWriter();
        using var cancellation = new CancellationTokenSource();
        var collecting = Collect(BidiConsole.InputFromKeyboard(() => false, () => available = keys.Count != 0,
            () => { Assert.True(available); available = false; reads++; return keys.Dequeue(); }, writer, cancellation.Token), cancellation.Token);
        try
        {
            var values = await Within(collecting);
            Assert.Equal(new[] { "a界", " \t" }, values.Select(Text));
            Assert.Equal(9, reads); Assert.Empty(keys); Assert.False(writer.Disposed);
            Assert.Contains("界", writer.ToString());
        }
        finally { cancellation.Cancel(); await Join(collecting); }
    }

    [Fact]
    public async Task InteractiveInputRejectsRedirectedStdinBeforeReadingAKey()
    {
        var calls = 0; var writer = new BorrowedWriter();
        var input = BidiConsole.InputFromKeyboard(() => true, () => { calls++; return true; },
            () => { calls++; return Character('x'); }, writer);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(input));
        Assert.Equal(0, calls); Assert.Equal(string.Empty, writer.ToString()); Assert.False(writer.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputRendersEnabledRolesAndLeavesTheWriterBorrowed(bool extra)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var writer = new BorrowedWriter();
        var output = BidiConsole.Output(writer, new() { ShowReasoning = extra, ShowTools = extra });
        await output.StartAsync(agent);
        try
        {
            foreach (var value in OutputEvents()) await output.WriteAsync(value);
        }
        finally { await output.StopAsync(); }
        var first = writer.ToString();
        Assert.Contains("assistant: answer", first);
        Assert.Contains("user (transcript): question", first);
        Assert.Contains("assistant (transcript): spoken", first);
        Assert.Equal(extra, first.Contains("reasoning: thought"));
        Assert.Equal(extra, first.Contains("tools: lookup"));
        Assert.DoesNotContain("AA==", first); Assert.False(writer.Disposed); Assert.True(writer.Flushes > 0);

        await output.StartAsync(agent);
        try
        {
            await output.WriteAsync(new BidiTranscriptStartEvent(Role.User, "shared"));
            await output.WriteAsync(new BidiTranscriptDeltaEvent("newer", Role.User, "shared"));
            // Stop must flush a pending textual line without requiring its end event.
        }
        finally { await output.StopAsync(); }
        Assert.Contains("user (transcript): newer", writer.ToString().Substring(first.Length));
        writer.Write("still open"); Assert.False(writer.Disposed);
    }

    [Fact]
    public async Task LongRunningOutputSuppressesRecentFinalDuplicatesWithoutRememberingEveryPastIdentifier()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var writer = new BorrowedWriter(); var output = BidiConsole.Output(writer);
        await output.StartAsync(agent);
        try
        {
            for (var index = 0; index < 256; index++)
            {
                var id = "utterance-" + index; var text = "line-" + index;
                await output.WriteAsync(new BidiTextDeltaEvent(text, id));
                await output.WriteAsync(new BidiTextStopEvent(id));
                await output.WriteAsync(new BidiTextBlockEvent(text, id));
            }
            Assert.Equal(256, writer.ToString().Split([writer.NewLine], StringSplitOptions.RemoveEmptyEntries).Length);
            await output.WriteAsync(new BidiTextBlockEvent("recent duplicate", "utterance-255"));
            await output.WriteAsync(new BidiTextBlockEvent("old replay", "utterance-0"));
            Assert.DoesNotContain("recent duplicate", writer.ToString());
            Assert.Contains("assistant: old replay", writer.ToString());
        }
        finally { await output.StopAsync(); }
        Assert.False(writer.Disposed);
    }

    [Fact]
    public async Task OutputAwaitsBorrowedWritesAndFlushesBeforeStopCompletes()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var writer = new GatedWriter(); var output = BidiConsole.Output(writer);
        Task? writing = null; Task? stopping = null;
        await output.StartAsync(agent);
        try
        {
            await output.WriteAsync(new BidiTextDeltaEvent("awaited", "line"));
            writing = output.WriteAsync(new BidiTextStopEvent("line")).AsTask();
            await SignalOrRun(writer.Writing.Task, writing);
            stopping = output.StopAsync().AsTask();
            Assert.False(writing.IsCompleted); Assert.False(stopping.IsCompleted);
            writer.ReleaseWrite.TrySetResult(true);
            await SignalOrRun(writer.Flushing.Task, writing);
            Assert.False(writing.IsCompleted); Assert.False(stopping.IsCompleted);
            writer.ReleaseFlush.TrySetResult(true);
            await Within(Completion(writing)); await Within(Completion(stopping));
            Assert.Equal("assistant: awaited" + writer.NewLine, writer.ToString()); Assert.False(writer.Disposed);
        }
        finally
        {
            writer.ReleaseWrite.TrySetResult(true); writer.ReleaseFlush.TrySetResult(true);
            await Join(writing); await Join(stopping); await output.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndActualStopJoinTheAsyncInputBeforeReturning(bool callerCancellation)
    {
        var entered = Signal(); var releaseRead = Signal(); var disposing = Signal(); var releaseDispose = Signal();
        var exited = false; var stopObserved = false;
        using var cancellation = new CancellationTokenSource();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { Assert.True(exited); stopObserved = true; return default; });
        Task? run = null; Task? stopping = null;
        try
        {
            run = agent.RunAsync(Inputs(), [], cancellationToken: cancellation.Token);
            await SignalOrRun(entered.Task, run);
            if (callerCancellation) cancellation.Cancel(); else stopping = agent.StopAsync();
            await Within(model.StopEntered.Task);
            Assert.False(run.IsCompleted); if (stopping is not null) Assert.False(stopping.IsCompleted);
            releaseRead.TrySetResult(true); await Within(disposing.Task);
            Assert.False(run.IsCompleted); Assert.False(exited);
            releaseDispose.TrySetResult(true);
            if (callerCancellation)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run!);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else { await run; await stopping!; }
            Assert.True(exited); Assert.True(stopObserved); Assert.Empty(model.Inputs);
            Assert.True(model.ReaderExited.Task.IsCompleted); Assert.Equal(0, model.Disposals);
        }
        finally
        {
            releaseRead.TrySetResult(true); releaseDispose.TrySetResult(true); cancellation.Cancel();
            var cleanup = agent.StopAsync();
            await Join(run); await Join(stopping); await Join(cleanup);
        }

        async IAsyncEnumerable<IBidiInput> Inputs([EnumeratorCancellation] CancellationToken token = default)
        {
            entered.TrySetResult(true);
            try
            {
                await releaseRead.Task; token.ThrowIfCancellationRequested();
                yield return new BidiMessage([new TextBlock("late input must not be sent")]);
            }
            finally
            {
                disposing.TrySetResult(true); await releaseDispose.Task; exited = true;
            }
        }
    }

    [Fact]
    public async Task InputFailureRetainsItsPrimaryAndDisposalFailure()
    {
        var failure = new IOException("input failure"); var cleanup = new IOException("input disposal");
        var input = new FailingInput(failure, cleanup);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        Task? run = null;
        try
        {
            run = agent.RunAsync(input, []);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run!));
            Assert.Contains(cleanup, Assert.IsAssignableFrom<IEnumerable<Exception>>(failure.Data["Strands.CleanupErrors"]));
            Assert.True(input.Disposed); Assert.True(model.ReaderExited.Task.IsCompleted); Assert.Equal(1, model.Stops);
        }
        finally { await agent.StopAsync(); await Join(run); }
    }

    [Fact]
    public async Task IdleKeyboardCancellationReleasesTheExclusiveClaim()
    {
        var entered = Signal(); using var cancellation = new CancellationTokenSource();
        var input = BidiConsole.InputFromKeyboard(() => false, () => { entered.TrySetResult(true); return false; },
            () => throw new InvalidOperationException("No key was available."), TextWriter.Null, cancellation.Token);
        var iterator = input.GetAsyncEnumerator(cancellation.Token); Task<bool>? pending = null;
        try
        {
            pending = MoveNextAsync(); await SignalOrRun(entered.Task, pending);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { cancellation.Cancel(); await Join(pending); await iterator.DisposeAsync(); }
        var next = BidiConsole.InputFromKeyboard(() => false, () => true,
            () => new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true), TextWriter.Null);
        Assert.Empty(await Within(Collect(next)));

        async Task<bool> MoveNextAsync() => await iterator.MoveNextAsync();
    }

    [Fact]
    public async Task ExistingNullAndDefaultStreamCallsKeepTheirInstanceBinding()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ConsoleModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await Assert.ThrowsAsync<ArgumentNullException>(() => agent.RunAsync(null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => agent.RunAsync(default!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => agent.RunAsync(inputs: null!, outputs: []));
        Assert.Equal(0, model.Starts); Assert.Equal(0, model.Stops);
    }

    private static IEnumerable<IAgentStreamEvent> OutputEvents()
    {
        yield return new BidiTextStartEvent("shared");
        yield return new BidiTextDeltaEvent("answer", "shared");
        yield return new BidiTextStopEvent("shared");
        yield return new BidiTranscriptStartEvent(Role.User, "user");
        yield return new BidiTranscriptDeltaEvent("question", Role.User, "user");
        yield return new BidiTranscriptStopEvent(Role.User, "user");
        yield return new BidiTranscriptStartEvent(Role.Assistant, "assistant");
        yield return new BidiTranscriptDeltaEvent("spoken", Role.Assistant, "assistant");
        yield return new BidiTranscriptStopEvent(Role.Assistant, "assistant");
        yield return new BidiReasoningStartEvent("reason");
        yield return new BidiReasoningDeltaEvent("thought", "reason");
        yield return new BidiReasoningStopEvent("reason");
        yield return new BidiToolUseBlocksEvent([new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]);
        yield return new BidiAudioDeltaEvent("AA==", "audio", 24000, 1, "pcm");
    }

    private static string Text(IBidiInput input) => Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<BidiMessage>(input).Content)).Text;
    private static ConsoleKeyInfo Character(char value) => new(value, (ConsoleKey)0, false, false, false);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<List<IBidiInput>> Collect(IAsyncEnumerable<IBidiInput> input, CancellationToken token = default)
    {
        var result = new List<IBidiInput>(); await using var iterator = input.GetAsyncEnumerator(token);
        while (await iterator.MoveNextAsync()) result.Add(iterator.Current);
        return result;
    }
    private static async Task SignalOrRun(Task signal, Task run)
    {
        var first = await Within(Task.WhenAny(signal, run));
        if (ReferenceEquals(first, run)) { await run; Assert.True(signal.IsCompleted, "Run completed before admitting the input capability."); }
        await Within(Completion(signal));
    }
    private static async Task<bool> Completion(Task task) { await task; return true; }
    private static async Task Join(Task? task) { if (task is not null) try { await task; } catch { } }

    private sealed class GatedWriter : StringWriter
    {
        internal TaskCompletionSource<bool> Writing { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseWrite { get; } = Signal();
        internal TaskCompletionSource<bool> Flushing { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseFlush { get; } = Signal();
        internal bool Disposed;
        public override async Task WriteLineAsync(string? value)
        {
            Writing.TrySetResult(true); await ReleaseWrite.Task; await base.WriteLineAsync(value);
        }
        public override async Task FlushAsync()
        {
            Flushing.TrySetResult(true); await ReleaseFlush.Task; await base.FlushAsync();
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BorrowedWriter : StringWriter
    {
        internal bool Disposed; internal int Flushes;
        public override Task FlushAsync() { Flushes++; return base.FlushAsync(); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CompletingInput(IReadOnlyList<IBidiInput> messages) : IAsyncEnumerable<IBidiInput>, IAsyncEnumerator<IBidiInput>
    {
        private int _index = -1;
        internal TaskCompletionSource<bool> Disposing { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseDisposal { get; } = Signal();
        internal bool Disposed; internal int Disposals;
        public IBidiInput Current => messages[_index];
        public IAsyncEnumerator<IBidiInput> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(++_index < messages.Count);
        public ValueTask DisposeAsync() => new(DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            Disposals++; Disposing.TrySetResult(true);
            await ReleaseDisposal.Task; Disposed = true;
        }
    }

    private sealed class FailingInput(Exception failure, Exception cleanup) : IAsyncEnumerable<IBidiInput>, IAsyncEnumerator<IBidiInput>
    {
        internal bool Disposed;
        public IBidiInput Current => throw new InvalidOperationException("No input completed.");
        public IAsyncEnumerator<IBidiInput> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(Task.FromException<bool>(failure));
        public ValueTask DisposeAsync() { Disposed = true; return new(Task.FromException(cleanup)); }
    }

    private sealed class ConsoleModel : IBidiModel
    {
        internal List<IBidiInput> Inputs { get; } = [];
        internal TaskCompletionSource<bool> StopEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ReaderExited { get; } = Signal();
        internal int Starts; internal int Stops; internal int Disposals;
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) { Inputs.Add(input); return Task.CompletedTask; }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { yield return new BidiConnectionStartEvent("console", "fixture"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { ReaderExited.TrySetResult(true); }
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; StopEntered.TrySetResult(true); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
