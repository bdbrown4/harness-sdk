using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests;

public sealed class ToolRetryTests
{
    [Fact]
    public async Task ForegroundRetryRerunsTheBodyAndBeforeHookWithStableCorrelation()
    {
        var attempts = 0;
        var before = 0;
        var after = 0;
        var state = new Dictionary<string, object?> { ["request"] = "same" };
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            return new ValueTask<string>("attempt-" + Interlocked.Increment(ref attempts));
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { Assert.Equal("call", value.ToolUse.ToolUseId); before++; return default; });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            Assert.Equal("call", value.ToolUse.ToolUseId);
            Assert.Same(state, value.InvocationState);
            value.Retry = Interlocked.Increment(ref after) == 1;
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("work", new() { InvocationState = state }, deadline.Token);
        Assert.Equal(2, attempts); Assert.Equal(2, before); Assert.Equal(2, after);
        Assert.Equal("attempt-2", result.ToString());
        var committed = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("call", committed.ToolUseId);
        Assert.Equal("attempt-2", committed.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryBodyErrorsAndHookDenialsRemainRetryable(bool denyFirst)
    {
        var before = 0; var bodies = 0; var after = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) =>
        {
            if (Interlocked.Increment(ref bodies) == 1 && !denyFirst) throw new InvalidOperationException("ordinary failure");
            return new ValueTask<string>("recovered");
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { if (Interlocked.Increment(ref before) == 1 && denyFirst) value.CancelReason = "denied"; return default; });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = Interlocked.Increment(ref after) == 1; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("recovered", (await agent.InvokeAsync("work", cancellationToken: deadline.Token)).ToString());
        Assert.Equal(2, before); Assert.Equal(2, after); Assert.Equal(denyFirst ? 1 : 2, bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAndBackgroundRoutesHonorExplicitRetries(bool background)
    {
        var attempts = 0; var before = 0; var after = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("attempt-" + Interlocked.Increment(ref attempts)));
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool }, BackgroundTasks = background ? new() { Always = new[] { "work" } } : null });
        agent.Hooks.Register<BeforeToolCallEvent>((_, _) => { before++; return default; });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = Interlocked.Increment(ref after) == 1; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (background) await agent.InvokeAsync("work", cancellationToken: deadline.Token);
        else Assert.Equal("attempt-2", (await agent.InvokeToolAsync(new("call", "work", JsonSerializer.SerializeToElement(new { })), cancellationToken: deadline.Token)).Content);
        Assert.Equal(2, attempts); Assert.Equal(2, after); Assert.Equal(background ? 1 : 2, before);
        if (background) Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), block => block.ContentBlocks.OfType<TextBlock>().Any(text => text.Text == "attempt-2"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedIteratorCleanupCannotBeRetriedOrHiddenByAResultReplacement(bool bodyFails, bool clearDiagnostics)
    {
        var primary = new InvalidOperationException("body failure");
        var cleanup = new IOException("cleanup failure");
        var calls = 0;
        var tool = new StreamingFunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, context, _) => { Interlocked.Increment(ref calls); return new BrokenStream(context.ToolUse.ToolUseId, bodyFails ? primary : null, cleanup); });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            Assert.Same(bodyFails ? primary : cleanup, value.Result.Error);
            Assert.Equal(true, value.Result.Error!.Data["Strands.ToolCleanupFailed"]);
            if (bodyFails) Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
            if (clearDiagnostics) value.Result.Error.Data.Clear();
            value.Result = new("wrong", "replacement");
            value.Retry = calls == 1;
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("work", cancellationToken: deadline.Token);
        Assert.Equal(1, calls);
        Assert.Equal("replacement", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryFollowedByAnApprovalCannotReplayTheRejectedCompletedBody(bool background)
    {
        var bodies = 0; var after = 0; var entries = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("attempt-" + Interlocked.Increment(ref bodies)));
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool }, BackgroundTasks = background ? new() { Always = new[] { "work" } } : null });
        if (background)
            agent.Middleware.RegisterInput(MiddlewareStages.ExecuteTool, (context, _) =>
            {
                if (Interlocked.Increment(ref entries) >= 2) context.Interrupt("retry-approval");
                return new ValueTask<ExecuteToolContext>(context);
            });
        else agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { if (Interlocked.Increment(ref entries) >= 2) value.Interrupt("retry-approval"); return default; });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = Interlocked.Increment(ref after) == 1; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await agent.InvokeAsync("work", cancellationToken: deadline.Token);
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Equal(1, bodies); Assert.Equal(1, after);
        var answer = new InterruptResponse(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("allowed"));
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = new[] { answer } }, deadline.Token);
        Assert.Equal("attempt-2", result.ToString()); Assert.Equal(2, bodies); Assert.Equal(2, after); Assert.Equal(3, entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationAfterAnAcceptedResultNeverStartsAnotherAttempt(bool direct)
    {
        var bodies = 0;
        using var cancellation = new CancellationTokenSource();
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => { Interlocked.Increment(ref bodies); return new ValueTask<string>("accepted"); });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { cancellation.Cancel(); value.Retry = true; return default; });
        if (direct)
            Assert.Equal("accepted", (await agent.InvokeToolAsync(new("call", "work", JsonSerializer.SerializeToElement(new { })), cancellationToken: cancellation.Token)).Content);
        else Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("work", cancellationToken: cancellation.Token)).StopReason);
        Assert.Equal(1, bodies);
    }

    [Fact]
    public async Task CancellingTheBackgroundTaskAtItsAfterHookPreventsItsRequestedRetry()
    {
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = 0; var after = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, token) =>
        {
            Interlocked.Increment(ref bodies);
            await TaskCancellation.WaitAsync(released.Task, token);
            return "accepted";
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool }, BackgroundTasks = new() { Always = new[] { "work" } } });
        agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            if (value.Request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Any(result => result.ToolUseId == "call")) released.TrySetResult(true);
            return default;
        });
        agent.Hooks.Register<AfterToolCallEvent>(async (value, _) =>
        {
            if (value.ToolUse.Name != "work") return;
            Interlocked.Increment(ref after);
            try
            {
                var task = Assert.Single(agent.State.Get("strands.backgroundTasks")!.Value.EnumerateArray());
                var cancel = new ToolUseBlock("cancel", "strands_manage_background_task", JsonSerializer.SerializeToElement(new { mode = "cancel", taskId = task.GetProperty("taskId").GetString() }));
                var result = await agent.InvokeToolAsync(cancel);
                Assert.Equal(ToolResultStatus.Success, result.Status);
                value.Retry = true;
                afterCompleted.TrySetResult(true);
            }
            catch (Exception error) { afterCompleted.TrySetException(error); throw; }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await agent.InvokeAsync("work", cancellationToken: deadline.Token); }
        finally { released.TrySetResult(true); }
        await TaskCancellation.WaitAsync(afterCompleted.Task, deadline.Token);
        Assert.Equal(1, bodies); Assert.Equal(1, after);
    }

    [Fact]
    public async Task ARefusedDirectApprovalRemainsClosedWhenAnAfterHookRequestsRetry()
    {
        var before = 0; var bodies = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("must not run"); });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            before++;
            try { value.Interrupt("approval"); }
            catch (InvalidOperationException) { }
            return default;
        });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = true; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeToolAsync(new("call", "work", JsonSerializer.SerializeToElement(new { })), cancellationToken: deadline.Token);
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("needs approval", result.Content);
        Assert.Equal(1, before); Assert.Equal(0, bodies);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARejectedStructuredValueCannotCompleteALaterFailedAttempt(bool custom, bool pause)
    {
        IStructuredOutputTool output = custom ? new CachedOutputTool() : new StructuredOutputTool<int>(new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { count = new { type = "integer" } }, required = new[] { "count" } }),
            Deserializer = input => input.GetProperty("count").GetInt32(),
        });
        var before = 0; var after = 0;
        await using var agent = new Agent(new() { Model = new Model("strands_structured_output", "{\"count\":1}") });
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            if (Interlocked.Increment(ref before) >= 2)
            {
                if (pause) value.Interrupt("retry-approval");
                value.ToolUse = value.ToolUse with { Input = JsonSerializer.SerializeToElement(new { }) };
            }
            return default;
        });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            if (Interlocked.Increment(ref after) == 1) value.Retry = true;
            else { Assert.Equal(ToolResultStatus.Error, value.Result.Status); value.Result = new(value.ToolUse.ToolUseId, "synthetic success"); }
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new InvocationOptions { StructuredOutputTool = output, StructuredOutputForcePrompt = "format", MaxTurns = 1 };
        var result = await agent.InvokeAsync("format", options, deadline.Token);
        if (pause)
        {
            Assert.Equal(StopReasons.Interrupt, result.StopReason);
            var response = new InterruptResponse(Assert.Single(result.Interrupts).Id, JsonSerializer.SerializeToElement("approved"));
            result = await agent.InvokeAsync(null, options with { InterruptResponses = new[] { response } }, deadline.Token);
        }
        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Null(result.StructuredOutputToolUseId);
        if (!custom) { Assert.False(output.TryGetResult("call", out _)); Assert.False(output.HasResult); }
    }

    [Fact]
    public async Task DiscardingOneStructuredAttemptRetainsOtherAcceptedIdsAndValidationCounts()
    {
        var output = new StructuredOutputTool<int>(new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Deserializer = input => input.GetProperty("count").GetInt32(),
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { output } });
        await agent.InvokeToolAsync(new("retained", output.Spec.Name, JsonSerializer.SerializeToElement(new { count = 1 })));
        var after = 0;
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            if (value.ToolUse.ToolUseId == "rejected" && after > 0) value.ToolUse = value.ToolUse with { Input = JsonSerializer.SerializeToElement(new { }) };
            return default;
        });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { if (value.ToolUse.ToolUseId == "rejected") value.Retry = Interlocked.Increment(ref after) == 1; return default; });
        var rejected = await agent.InvokeToolAsync(new("rejected", output.Spec.Name, JsonSerializer.SerializeToElement(new { count = 2 })));
        Assert.Equal(ToolResultStatus.Error, rejected.Status);
        Assert.True(output.TryGetResult("retained", out var retained)); Assert.Equal(1, retained);
        Assert.False(output.TryGetResult("rejected", out _)); Assert.True(output.HasResult); Assert.Equal(1, output.Output);
        Assert.Equal(3, output.ValidationAttempts);
    }

    [Fact]
    public async Task ReusingAnExceptionInAnUnrelatedBodyDoesNotInheritAnOldCleanupFailure()
    {
        var shared = new IOException("shared exception");
        var calls = 0;
        var broken = new StreamingFunctionTool(new("broken", "Broken", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, context, _) => new BrokenStream(context.ToolUse.ToolUseId, null, shared));
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw shared;
            return new ValueTask<string>("recovered");
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new ITool[] { broken, work } });
        var failed = await agent.InvokeToolAsync(new("old", "broken", JsonSerializer.SerializeToElement(new { })));
        Assert.Same(shared, failed.Error);
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = value.ToolUse.Name == "work" && calls == 1; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovered = await agent.InvokeToolAsync(new("new", "work", JsonSerializer.SerializeToElement(new { })), cancellationToken: deadline.Token);
        Assert.Equal("recovered", recovered.Content); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDispositionSurvivesAPausedFileSessionAndFreshAgent(bool legacyWithoutDisposition)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-retry-" + Guid.NewGuid().ToString("N"));
        var bodies = 0; var after = 0;
        var store = new FileSessionStore(directory);
        var tool = new StreamingFunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Interlocked.Increment(ref bodies);
            return new BrokenStream(context.ToolUse.ToolUseId, null, new IOException("cleanup failure"));
        });
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.ExecuteTool, PauseAfterBody);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            AgentResult paused;
            await using (var first = new Agent(new() { Model = new Model(), Tools = new[] { tool }, SessionStore = store, SessionId = "paused", Middleware = middleware }))
                paused = await first.InvokeAsync("work", cancellationToken: deadline.Token);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var checkpoint = saved["interruptState"]!["toolExecutions"]![0]!;
            Assert.True(checkpoint["iteratorCleanupFailed"]!.GetValue<bool>());
            Assert.Null(checkpoint["result"]!["iteratorCleanupFailed"]);
            if (legacyWithoutDisposition)
            {
                checkpoint.AsObject().Remove("iteratorCleanupFailed");
                File.WriteAllText(path, saved.ToJsonString());
            }
            await using var restored = new Agent(new() { Model = new Model(alreadyStarted: true), Tools = new[] { tool }, SessionStore = store, SessionId = "paused", Middleware = middleware });
            restored.Hooks.Register<AfterToolCallEvent>((value, _) => { Interlocked.Increment(ref after); value.Retry = true; return default; });
            var response = new InterruptResponse(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("approved"));
            await restored.InvokeAsync(null, new() { InterruptResponses = new[] { response } }, deadline.Token);
            Assert.Equal(legacyWithoutDisposition ? 2 : 1, bodies);
            Assert.Equal(legacyWithoutDisposition ? 2 : 1, after);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        static async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> PauseAfterBody(ExecuteToolContext context,
            MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next(context).WithCancellation(cancellationToken))
            {
                if (item is MiddlewareResultEvent<ExecuteToolResult>) context.Interrupt("post-cleanup");
                yield return item;
            }
        }
    }

    private sealed class CachedOutputTool : IStructuredOutputTool
    {
        public ToolSpec Spec { get; } = new("strands_structured_output", "Format", JsonSerializer.SerializeToElement(new { type = "object" }));
        public bool HasResult { get; private set; }
        public object? Result => HasResult ? 1 : null;
        public bool TryGetResult(string toolUseId, out object? result) { result = Result; return HasResult && toolUseId == "call"; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            if (!input.TryGetProperty("count", out _)) throw new StructuredOutputException("Invalid output");
            HasResult = true;
            return new("formatted");
        }
    }

    private sealed class BrokenStream(string id, Exception? primary, Exception cleanup) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private bool _emitted;
        public IToolStreamEvent Current => new ToolStreamResultEvent(new(id, "accepted"));
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (primary is not null) throw primary;
            if (_emitted) return new(false);
            _emitted = true;
            return new(true);
        }
        public ValueTask DisposeAsync() => throw cleanup;
    }

    private sealed class Model(string toolName = "work", string toolInput = "{}", bool alreadyStarted = false) : IModel
    {
        private int _calls = alreadyStarted ? 1 : 0;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            yield return new ModelMessageStartEvent();
            if (first)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", toolName, JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: toolInput);
            }
            else
            {
                var text = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().LastOrDefault()?.Content ?? "waiting";
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: text);
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
    }
}
