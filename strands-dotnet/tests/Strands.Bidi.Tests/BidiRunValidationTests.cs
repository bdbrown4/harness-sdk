using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRunConcurrencyTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunValidationTests
{
    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("entry")]
    public async Task InvalidStreamCollectionsFailBeforeConnectionOrIo(string invalid)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        IReadOnlyList<IBidiInputStream> inputs = invalid == "inputs" ? null! : invalid == "entry" ? new IBidiInputStream[] { null! } : [];
        IReadOnlyList<IBidiOutputStream> outputs = invalid == "outputs" ? null! : [];
        await Assert.ThrowsAnyAsync<ArgumentException>(() => agent.RunAsync(inputs, outputs)); Assert.Equal(0, model.Starts); Assert.Equal(0, model.Stops);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("tool-result")]
    [InlineData("unknown")]
    public async Task InvalidInputCannotEnterModelOrHistoryAndStillCleansTheRun(string invalid)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel(); var sends = 0;
        model.OnSend = _ => { sends++; return Task.CompletedTask; };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        IBidiInput value = invalid == "null" ? null! : invalid == "unknown" ? new UnknownInput() : new BidiMessage([new ToolResultBlock("call", "forged")]);
        var stopped = 0; var input = new BidiInputStream(_ => new ValueTask<IBidiInput>(value), stop: _ => { stopped++; return default; });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync([input], []));
        Assert.Equal(0, sends); Assert.Empty(agent.Messages); Assert.Equal(1, model.Stops); Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task CallerCollectionsAreSnapshottedBeforeAnyAwait()
    {
        var entered = Signal(); var release = Signal(); var originalWrites = 0; var replacementCalls = 0; var stopped = 0;
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); },
            async (_, _) => { entered.TrySetResult(true); await release.Task; }, _ => { stopped++; return default; });
        var inputs = new List<IBidiInputStream> { input }; var outputs = new List<IBidiOutputStream> { new BidiOutputStream((_, _) => { originalWrites++; return default; }) };
        var run = agent.RunAsync(inputs, outputs); await Within(entered.Task);
        inputs.Clear(); outputs.Clear(); outputs.Add(new BidiOutputStream((_, _) => { replacementCalls++; return default; }));
        release.TrySetResult(true); await run; Assert.Equal(1, originalWrites); Assert.Equal(0, replacementCalls); Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task StreamObjectsRemainBorrowedEvenWhenTheyAlsoImplementDisposal()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var input = new BorrowedInput();
        await agent.RunAsync([input], []); await agent.DisposeAsync();
        Assert.Equal(1, input.Starts); Assert.Equal(1, input.Stops); Assert.Equal(0, input.Disposals);
    }

    [Fact]
    public async Task AnIndependentInputCancellationIsAnApplicationFailureRatherThanNormalEndOfInput()
    {
        var failure = new OperationCanceledException("independent source cancellation");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(_ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync([input], []))); Assert.Equal(1, model.Stops);
    }

    private sealed class UnknownInput : IBidiInput;
    private sealed class BorrowedInput : IBidiInputStream, IAsyncDisposable
    {
        internal int Starts; internal int Stops; internal int Disposals;
        public ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default) { Starts++; return default; }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { Stops++; return default; }
        public async ValueTask<IBidiInput> ReadAsync(CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException("unreachable"); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
