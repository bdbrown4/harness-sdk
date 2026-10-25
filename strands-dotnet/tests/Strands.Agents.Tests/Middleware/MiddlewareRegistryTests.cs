using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Middleware;

public sealed class MiddlewareRegistryTests
{
    private static readonly MiddlewareStage<InvokeModelContext, InvokeModelResult> Stage = MiddlewareStages.InvokeModel;

    [Fact]
    public async Task PhasesOverrideRegistrationOrderAndBothWrapAndOutputUnwindInsideOut()
    {
        var registry = new MiddlewareRegistry();
        var order = new List<string>();
        registry.RegisterOutput(Stage, (result, _) => { order.Add("output-a"); return new(result with { StopReason = result.StopReason + "a" }); });
        registry.RegisterWrap(Stage, Wrap("wrap-a"));
        registry.RegisterInput(Stage, (context, _) => { order.Add("input-a"); return new(context with { SystemPrompt = context.SystemPrompt + "a" }); });
        registry.RegisterOutput(Stage, (result, _) => { order.Add("output-b"); return new(result with { StopReason = result.StopReason + "b" }); });
        registry.RegisterWrap(Stage, Wrap("wrap-b"));
        registry.RegisterInput(Stage, (context, _) => { order.Add("input-b"); return new(context with { SystemPrompt = context.SystemPrompt + "b" }); });

        var completed = await Collect(registry.InvokeAsync(Stage, Context() with { SystemPrompt = "" }, Terminal));

        Assert.Equal(new[] { "input-a", "input-b", "wrap-a-before", "wrap-b-before", "terminal-ab", "wrap-b-after", "wrap-a-after", "output-b", "output-a" }, order);
        Assert.Equal("end_turnba", completed.Result.StopReason);

        MiddlewareHandler<InvokeModelContext, InvokeModelResult> Wrap(string name) => (context, next, _) => Forward(name, context, next);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Forward(string name, InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next)
        {
            await Task.CompletedTask;
            order.Add(name + "-before");
            await foreach (var item in next(context)) yield return item;
            order.Add(name + "-after");
        }

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); order.Add("terminal-" + context.SystemPrompt);
            yield return Complete();
        }
    }

    [Fact]
    public async Task ShortCircuitSkipsTerminalAndOutputTransformsOnlyTheAuthoritativeResult()
    {
        var registry = new MiddlewareRegistry();
        var terminalCalls = 0;
        registry.RegisterWrap(Stage, Cached);
        registry.RegisterOutput(Stage, (result, _) => new(result with { Message = Message.Assistant("rewritten"), Usage = new(3, 4) }));

        var completed = await Collect(registry.InvokeAsync(Stage, Context(), Terminal));

        Assert.Equal(0, terminalCalls);
        Assert.Single(completed.Events);
        Assert.Equal("rewritten", completed.Result.Message.Text);
        Assert.Equal(new Usage(3, 4), completed.Result.Usage);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent()));
            yield return Complete("cached");
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; terminalCalls++; yield return Complete(); }
    }

    [Fact]
    public async Task ExplicitRetryReentersNextWithoutRerunningOuterInputOrOutput()
    {
        var registry = new MiddlewareRegistry();
        var inputs = 0; var outputs = 0; var attempts = 0;
        var transient = new IOException("transient");
        registry.RegisterInput(Stage, (context, _) => { inputs++; return new(context); });
        registry.RegisterOutput(Stage, (result, _) => { outputs++; return new(result); });
        registry.RegisterWrap(Stage, Retry);

        Assert.Equal("recovered", (await Collect(registry.InvokeAsync(Stage, Context(), Terminal))).Result.Message.Text);
        Assert.Equal(2, attempts); Assert.Equal(1, inputs); Assert.Equal(1, outputs);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Retry(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using (var first = next(context).GetAsyncEnumerator(cancellationToken))
            { Assert.Same(transient, await Assert.ThrowsAsync<IOException>(() => first.MoveNextAsync().AsTask())); }
            await foreach (var item in next(context)) yield return item;
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; if (++attempts == 1) throw transient; yield return Complete("recovered"); }
    }

    [Fact]
    public async Task AComposedInvocationSnapshotsRegistrationsAndEachHandleRemovesOnlyItsOwnOccurrence()
    {
        var registry = new MiddlewareRegistry(); var calls = 0;
        ValueTask<InvokeModelContext> Handler(InvokeModelContext context, CancellationToken _) { calls++; return new(context); }
        var first = registry.RegisterInput(Stage, Handler);
        using var second = registry.RegisterInput(Stage, Handler);
        var snapshot = registry.InvokeAsync(Stage, Context(), Done);
        first.Dispose(); first.Dispose();

        await Collect(snapshot); Assert.Equal(2, calls);
        await Collect(registry.InvokeAsync(Stage, Context(), Done)); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ChildLocalRegistrationsRemainIndependentOfItsApplicationParent()
    {
        var application = new MiddlewareRegistry(); var first = new MiddlewareRegistry(application); var second = new MiddlewareRegistry(application);
        var calls = new List<string>();
        using var shared = application.RegisterInput(Stage, (context, _) => { calls.Add("application"); return new(context); });
        using var local = first.RegisterInput(Stage, (context, _) => { calls.Add("first"); return new(context); });
        await Collect(first.InvokeAsync(Stage, Context(), Done));
        await Collect(second.InvokeAsync(Stage, Context(), Done));
        local.Dispose();
        await Collect(first.InvokeAsync(Stage, Context(), Done));
        Assert.Equal(new[] { "application", "first", "application", "application" }, calls);
    }

    [Fact]
    public async Task ConsumerDisposalUnwindsEveryLayerAndClosesAnInnerEnumeratorAbandonedByMiddleware()
    {
        var registry = new MiddlewareRegistry(); var order = new List<string>();
        registry.RegisterWrap(Stage, Abandoning);
        await using (var iterator = registry.InvokeAsync(Stage, Context(), Terminal).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(new[] { "middleware-finally", "terminal-finally" }, order);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Abandoning(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var forgotten = next(context).GetAsyncEnumerator(cancellationToken);
            try { Assert.True(await forgotten.MoveNextAsync()); yield return forgotten.Current; }
            finally { order.Add("middleware-finally"); }
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            try { yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent())); yield return Complete(); }
            finally { order.Add("terminal-finally"); }
        }
    }

    [Fact]
    public async Task PrimaryFailureRetainsItsIdentityWhenAForgottenInnerIteratorFailsDuringCleanup()
    {
        var registry = new MiddlewareRegistry();
        var primary = new IOException("policy failed"); var cleanup = new IOException("cleanup failed");
        registry.RegisterWrap(Stage, Failure);

        var observed = await Assert.ThrowsAsync<IOException>(() => Collect(registry.InvokeAsync(Stage, Context(), Terminal)));

        Assert.Same(primary, observed);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(observed.Data["Strands.CleanupErrors"]));

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Failure(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var forgotten = next(context).GetAsyncEnumerator(cancellationToken);
            Assert.True(await forgotten.MoveNextAsync());
            yield return forgotten.Current;
            throw primary;
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            try { yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent())); }
            finally { throw cleanup; }
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("late-event")]
    [InlineData("null-result")]
    [InlineData("unknown")]
    public async Task InvalidTerminalProtocolsAreRejected(string kind)
    {
        var registry = new MiddlewareRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(registry.InvokeAsync(Stage, Context(), Invalid)));
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Invalid(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (kind == "unknown") { yield return new UnknownEvent(); yield break; }
            if (kind == "missing") yield break;
            if (kind == "null-result") { yield return new MiddlewareResultEvent<InvokeModelResult>(null!); yield break; }
            yield return Complete();
            if (kind == "duplicate") yield return Complete();
            else yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent()));
        }
    }

    [Fact]
    public async Task AWrapperCurrentFailureRemainsPrimaryWhenItsEnumeratorAlsoFailsToDispose()
    {
        var registry = new MiddlewareRegistry();
        var primary = new IOException("current failed"); var cleanup = new IOException("dispose failed");
        registry.RegisterWrap(Stage, (_, _, _) => new BrokenCurrent(primary, cleanup));

        var observed = await Assert.ThrowsAsync<IOException>(() => Collect(registry.InvokeAsync(Stage, Context(), Done)));

        Assert.Same(primary, observed);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(observed.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task OutputDoesNotBufferProgressWhileTheTerminalIsStillRunning()
    {
        var registry = new MiddlewareRegistry(); var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOutput(Stage, (result, _) => new(result with { Message = Message.Assistant("transformed") }));
        await using var iterator = registry.InvokeAsync(Stage, Context(), Terminal).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<MiddlewareStreamEvent<InvokeModelResult>>(iterator.Current);
        Assert.False(gate.Task.IsCompleted);
        gate.SetResult(true);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("transformed", Assert.IsType<MiddlewareResultEvent<InvokeModelResult>>(iterator.Current).Result.Message.Text);
        Assert.False(await iterator.MoveNextAsync());

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent()));
            await gate.Task; yield return Complete();
        }
    }

    [Fact]
    public async Task AContinuationCannotEscapeItsCompletedWrapper()
    {
        var registry = new MiddlewareRegistry(); MiddlewareNext<InvokeModelContext, InvokeModelResult>? escaped = null;
        registry.RegisterWrap(Stage, Capture);
        var context = Context();
        await Collect(registry.InvokeAsync(Stage, context, Done));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(escaped!(context)));

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Capture(InvokeModelContext input, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; escaped = next; yield return Complete(); }
    }

    [Fact]
    public async Task ConsumerDisposalRunsOrdinaryNestedFinallyBlocksInsideOut()
    {
        var registry = new MiddlewareRegistry(); var order = new List<string>();
        registry.RegisterWrap(Stage, (context, next, token) => Forward("outer", context, next, token));
        registry.RegisterWrap(Stage, (context, next, token) => Forward("inner", context, next, token));
        await using (var iterator = registry.InvokeAsync(Stage, Context(), Terminal).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(new[] { "terminal", "inner", "outer" }, order);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Forward(string name, InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { try { await foreach (var item in next(context)) yield return item; } finally { order.Add(name); } }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; try { yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent())); yield return Complete(); } finally { order.Add("terminal"); } }
    }

    [Fact]
    public async Task CancellationStopsThePipelineBeforeTheNextHandlerAndKeepsTheExecutorToken()
    {
        var registry = new MiddlewareRegistry(); using var cancellation = new CancellationTokenSource(); var terminalCalls = 0;
        registry.RegisterInput(Stage, (context, token) => { Assert.Equal(cancellation.Token, token); cancellation.Cancel(); return new(context); });
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(registry.InvokeAsync(Stage, Context(), Terminal, cancellation.Token)));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.Equal(0, terminalCalls);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; terminalCalls++; yield return Complete(); }
    }

    [Fact]
    public async Task NullInputAndOutputTransformationsFailAtTheirBoundary()
    {
        var input = new MiddlewareRegistry(); input.RegisterInput(Stage, (_, _) => new((InvokeModelContext)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Collect(input.InvokeAsync(Stage, Context(), Done)));
        var output = new MiddlewareRegistry(); output.RegisterOutput(Stage, (_, _) => new((InvokeModelResult)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Collect(output.InvokeAsync(Stage, Context(), Done)));
    }

    private sealed record UnknownEvent : IMiddlewareEvent<InvokeModelResult>;
    private sealed class BrokenCurrent(Exception primary, Exception cleanup) : IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>>, IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>>
    {
        public IMiddlewareEvent<InvokeModelResult> Current => throw primary;
        public IAsyncEnumerator<IMiddlewareEvent<InvokeModelResult>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(true);
        public ValueTask DisposeAsync() => throw cleanup;
    }
    internal static InvokeModelContext Context() { var model = new UnusedModel(); return new(new Agent(new() { Model = model }), model, new(new[] { Message.User("question") }), new Dictionary<string, object?>()); }
    internal static MiddlewareResultEvent<InvokeModelResult> Complete(string text = "answer") => new(new(Message.Assistant(text), StopReasons.EndTurn, new()));
    internal static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Done(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask; yield return Complete(); }
    internal static async Task<(IReadOnlyList<IAgentStreamEvent> Events, InvokeModelResult Result)> Collect(IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> events)
    {
        var updates = new List<IAgentStreamEvent>(); InvokeModelResult? result = null;
        await foreach (var item in events) { if (item is MiddlewareStreamEvent<InvokeModelResult> update) updates.Add(update.Event); else if (item is MiddlewareResultEvent<InvokeModelResult> completed) result = completed.Result; }
        return (updates, result!);
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The registry fixture must not invoke a provider.");
    }
}
