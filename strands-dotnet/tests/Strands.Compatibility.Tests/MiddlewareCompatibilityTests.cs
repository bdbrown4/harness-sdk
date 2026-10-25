using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Middleware;

namespace Strands.Compatibility.Tests;

public sealed class MiddlewareCompatibilityTests
{
    [Fact]
    public async Task ModelPhasesPreserveStreamingAndSourceOrderOnThePortableAsset()
    {
        var context = Context(); var order = new List<string>(); var registry = new MiddlewareRegistry();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => { order.Add("output-a"); return new(result with { Message = Message.Assistant(result.Message.Text + "a") }); });
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Wrap);
        registry.RegisterInput(MiddlewareStages.InvokeModel, (input, _) => { order.Add("input"); return new(input with { SystemPrompt = "transformed" }); });
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => { order.Add("output-b"); return new(result with { Message = Message.Assistant(result.Message.Text + "b") }); });
        var events = new List<IMiddlewareEvent<InvokeModelResult>>();
        await foreach (var item in registry.InvokeAsync(MiddlewareStages.InvokeModel, context, Terminal)) events.Add(item);

        Assert.Equal(new[] { "input", "wrap-before", "terminal", "wrap-after", "output-b", "output-a" }, order);
        Assert.IsType<MiddlewareStreamEvent<InvokeModelResult>>(events[0]);
        Assert.Equal("answerba", Assert.IsType<MiddlewareResultEvent<InvokeModelResult>>(events[1]).Result.Message.Text);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Wrap(InvokeModelContext input, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { order.Add("wrap-before"); await foreach (var item in next(input)) yield return item; order.Add("wrap-after"); }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; Assert.Equal("transformed", input.SystemPrompt); order.Add("terminal");
            yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent()));
            yield return Complete();
        }
    }

    [Fact]
    public async Task ToolShortCircuitAndOutputKeepCallerStateAndExecutorCancellation()
    {
        var initial = Context(); var state = new Dictionary<string, object?> { ["caller"] = 1 }; using var cancellation = new CancellationTokenSource();
        using var document = JsonDocument.Parse("{\"value\":7}");
        var call = new ToolUseBlock("call-id", "unregistered", document.RootElement);
        var context = new ExecuteToolContext(initial.Agent, null, call, state, cancellation.Token);
        document.Dispose();
        var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.ExecuteTool, Cached);
        registry.RegisterOutput(MiddlewareStages.ExecuteTool, (result, _) => new(new ExecuteToolResult(result.Result with { Status = ToolResultStatus.Error })));
        ExecuteToolResult? final = null;
        await foreach (var item in registry.InvokeAsync(MiddlewareStages.ExecuteTool, context, Never, cancellation.Token))
            if (item is MiddlewareResultEvent<ExecuteToolResult> result) final = result.Result;
        Assert.Equal(ToolResultStatus.Error, final!.Result.Status); Assert.Equal("7", final.Result.Content); Assert.Single(state);

        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Cached(ExecuteToolContext input, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; Assert.Same(state, input.InvocationState); Assert.Equal(cancellation.Token, cancellationToken); Assert.Equal(cancellation.Token, input.CancellationToken);
            yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(input.ToolUse.ToolUseId, input.ToolUse.Input.GetProperty("value").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Never(ExecuteToolContext input, CancellationToken token) => throw new InvalidOperationException("The cache must bypass the terminal.");
    }

    [Fact]
    public async Task ExplicitRetryRetainsTheOriginalExceptionAndInvokesOnlyTheRequestedAttempts()
    {
        var primary = new IOException("retry once"); var attempts = 0; var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Retry);
        await Drain(registry.InvokeAsync(MiddlewareStages.InvokeModel, Context(), Terminal));
        Assert.Equal(2, attempts);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Retry(InvokeModelContext input, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using (var failed = next(input).GetAsyncEnumerator(cancellationToken))
                Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => failed.MoveNextAsync().AsTask()));
            await foreach (var item in next(input)) yield return item;
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext input, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; if (++attempts == 1) throw primary; yield return Complete(); }
    }

    [Fact]
    public async Task AbandoningAWrapperClosesItsForgottenInnerIterator()
    {
        var disposed = false; var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Forget);
        await using (var iterator = registry.InvokeAsync(MiddlewareStages.InvokeModel, Context(), Terminal).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.True(disposed);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Forget(InvokeModelContext input, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { var inner = next(input).GetAsyncEnumerator(cancellationToken); Assert.True(await inner.MoveNextAsync()); yield return inner.Current; }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            try { yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent())); yield return Complete(); }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task CurrentErrorsRemainPrimaryWhenPortableEnumeratorCleanupAlsoFails()
    {
        var primary = new IOException("current"); var cleanup = new IOException("cleanup"); var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, (_, _, _) => new BrokenCurrent(primary, cleanup));
        var actual = await Assert.ThrowsAsync<IOException>(() => Drain(registry.InvokeAsync(MiddlewareStages.InvokeModel, Context(), Done)));
        Assert.Same(primary, actual); Assert.Contains(cleanup, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task CancellationAndMissingCompletionNeverProduceASuccessfulStage()
    {
        var registry = new MiddlewareRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(registry.InvokeAsync(MiddlewareStages.InvokeModel, Context(), Missing)));
        using var cancellation = new CancellationTokenSource();
        registry.RegisterInput(MiddlewareStages.InvokeModel, (context, token) => { cancellation.Cancel(); return new(context); });
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(registry.InvokeAsync(MiddlewareStages.InvokeModel, Context(), Done, cancellation.Token)));
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Missing(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent())); }
    }

    private static InvokeModelContext Context()
    { var model = new UnusedModel(); return new(new Agent(new() { Model = model }), model, new(new[] { Message.User("question") }), new Dictionary<string, object?>()); }
    private static MiddlewareResultEvent<InvokeModelResult> Complete() => new(new(Message.Assistant("answer"), StopReasons.EndTurn, new()));
    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Done(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask; yield return Complete(); }
    private static async Task Drain(IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> source) { await foreach (var item in source) { } }
    private sealed class UnusedModel : IModel
    { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No provider call is expected."); }
    private sealed class BrokenCurrent(Exception primary, Exception cleanup) : IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>>, IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>>
    {
        public IMiddlewareEvent<InvokeModelResult> Current => throw primary;
        public IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(true);
        public ValueTask DisposeAsync() => throw cleanup;
    }
}
