using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class InvocationContinuationTests
{
    [Fact]
    public async Task HookResumeRunsFreshLifecyclesWithOriginalStateAndOnlyOneFinalResult()
    {
        var state = new Dictionary<string, object?> { ["tenant"] = new object() };
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        var before = 0; var after = 0;
        agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { Assert.Same(state, value.InvocationState); before++; return default; });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            Assert.Same(state, value.InvocationState);
            if (++after == 1) value.Resume = new("follow up");
            return default;
        });
        var events = new List<IAgentStreamEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in agent.StreamAsync("start", new() { InvocationState = state }, deadline.Token)) events.Add(item);
        var result = Assert.Single(events.OfType<AgentResultEvent>()).Result;
        Assert.Equal(2, before); Assert.Equal(2, after); Assert.Equal(2, model.Requests.Count);
        Assert.Equal("follow up", model.Requests[1].Messages.Last(message => message.Role == Role.User).Text);
        Assert.Equal("answer 2", result.ToString()); Assert.Equal(2, result.Turns); Assert.Equal(15, result.Usage.TotalTokens);
        Assert.Same(state, result.InvocationState);
        Assert.IsType<AfterInvocationEvent>(events[events.Count - 2]);
        Assert.IsType<AgentResultEvent>(events[events.Count - 1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyInputContinuesWithoutAppendingAUserMessage(bool content)
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Requests.Count == 1) value.Resume = content ? new(Array.Empty<ContentBlock>()) : InvocationContinuation.Continue;
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("start", cancellationToken: deadline.Token);
        Assert.Equal("answer 2", result.ToString());
        Assert.Single(agent.Messages, message => message.Role == Role.User);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookResumeLimitsAreCumulativeUnlessExplicitlyReset(bool reset)
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Requests.Count == 1 && value.Result?.StopReason == StopReasons.EndTurn) value.Resume = new("next");
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("start", new() { MaxTurns = 1, ResetLimitsOnHookResume = reset }, deadline.Token);
        Assert.Equal(reset ? 2 : 1, model.Requests.Count);
        Assert.Equal(reset ? StopReasons.EndTurn : StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(1, result.Turns); Assert.Equal(reset ? 10 : 5, result.Usage.TotalTokens);
        Assert.Equal(reset ? 2 : 1, agent.Messages.Count(message => message.Role == Role.User));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenLimitsCannotBeBypassedByEmptyHookResume(bool output)
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = InvocationContinuation.Continue; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("start", output ? new() { MaxOutputTokens = 3 } : new() { MaxTotalTokens = 5 }, deadline.Token);
        Assert.Equal(output ? StopReasons.MaxOutputTokens : StopReasons.MaxTotalTokens, result.StopReason);
        Assert.Single(model.Requests); Assert.Equal(5, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task ContentIsDetachedBeforeJsonDisposalAndCallerListMutation()
    {
        var model = new Model();
        var content = new List<ContentBlock>();
        InvocationContinuation continuation;
        using (var document = JsonDocument.Parse("{\"tenant\":\"original\"}"))
        {
            content.Add(new JsonBlock(document.RootElement));
            content.Add(new ImageBlock("image/png", Convert.ToBase64String(new byte[] { 1, 2, 3 })));
            continuation = new(content);
        }
        content.Clear(); content.Add(new TextBlock("mutated"));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { if (model.Requests.Count == 1) value.Resume = continuation; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await agent.InvokeAsync("start", cancellationToken: deadline.Token);
        var input = model.Requests[1].Messages.Last(message => message.Role == Role.User);
        Assert.Equal("original", Assert.IsType<JsonBlock>(input.Content[0]).Json.GetProperty("tenant").GetString());
        Assert.Equal("AQID", Assert.IsType<ImageBlock>(input.Content[1]).Data);
    }

    [Fact]
    public async Task EntireMessageBatchCommitsBeforeObserversEvenWhenAnObserverFails()
    {
        var model = new Model();
        var call = new ToolUseBlock("imported", "work", JsonSerializer.SerializeToElement(new { }));
        var first = new Message(Role.Assistant, new ContentBlock[] { call }, "imported-call");
        var second = new Message(Role.User, new ContentBlock[] { new ToolResultBlock("imported", "done") }, "imported-result");
        var messages = new List<Message> { first, second };
        var continuation = new InvocationContinuation(messages);
        messages.Clear();
        var failure = new IOException("observer failed");
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { if (value.Error is null) value.Resume = continuation; return default; });
        agent.Hooks.Register<MessageAddedEvent>((value, _) =>
        {
            if (value.Message.TrackingId == first.TrackingId)
            {
                Assert.Equal(second.TrackingId, agent.Messages.Last().TrackingId);
                throw failure;
            }
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("start", cancellationToken: deadline.Token)));
        Assert.Single(model.Requests);
        Assert.Equal(new[] { first.TrackingId, second.TrackingId }, agent.Messages.Skip(2).Select(message => message.TrackingId));
    }

    [Fact]
    public async Task MalformedHistoryFailsBeforeAnyContinuationInputIsAdded()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            value.Resume = new(new Message[] { Message.User("must not be added"), new(Role.User, new ContentBlock[] { new ToolResultBlock("missing", "forged") }) });
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InvokeAsync("start", cancellationToken: deadline.Token));
        Assert.Single(model.Requests); Assert.Equal(2, agent.Messages.Count);
        Assert.Throws<ArgumentException>(() => new InvocationContinuation(new ContentBlock[] { new ToolResultBlock("missing", "forged") }));
    }

    [Fact]
    public async Task HookAnswersResumeTheExactPendingToolBatchWithTheOriginalState()
    {
        var model = new ToolModel();
        var state = new Dictionary<string, object?> { ["tenant"] = "unchanged" };
        var bodies = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Assert.Same(state, context.InvocationState); bodies++;
            return new ValueTask<string>(context.Interrupt("approval").GetString()!);
        });
        await using var agent = new Agent(new() { Model = model, Tools = new[] { work } });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (value.Result?.StopReason == StopReasons.Interrupt)
                value.Resume = new(new[] { new InterruptResponse(Assert.Single(value.Result.Interrupts).Id, JsonSerializer.SerializeToElement("approved")) });
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("work", new() { InvocationState = state, MaxTurns = 2 }, deadline.Token);
        Assert.Equal("approved", result.ToString()); Assert.Equal(2, result.Turns); Assert.Empty(result.Interrupts);
        Assert.Equal(2, bodies); Assert.Null(agent.InterruptState); Assert.Single(state);
    }

    [Fact]
    public async Task UnknownHookAnswersCannotMutateThePausedBatch()
    {
        var model = new ToolModel();
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        await using var agent = new Agent(new() { Model = model, Tools = new[] { work } });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (value.Result?.StopReason == StopReasons.Interrupt)
                value.Resume = new(new[] { new InterruptResponse("unknown", JsonSerializer.SerializeToElement("forged")) });
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("work", cancellationToken: deadline.Token));
        var pending = agent.InterruptState;
        Assert.NotNull(pending); Assert.Empty(pending.Responses); Assert.Single(model.Requests);
    }

    [Fact]
    public async Task DisposingAtAfterInvocationCannotStartAHiddenContinuation()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        var after = 0;
        using var hook = agent.Hooks.Register<AfterInvocationEvent>((value, _) => { after++; value.Resume = new("hidden"); return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iterator = agent.StreamAsync("start", cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (await iterator.MoveNextAsync()) if (iterator.Current is AfterInvocationEvent) break;
        await iterator.DisposeAsync();
        Assert.Equal(1, after); Assert.Single(model.Requests); Assert.Equal(2, agent.Messages.Count);
        hook.Dispose();
        await agent.InvokeAsync("next explicit invocation", cancellationToken: deadline.Token);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task DisposingDuringAModelStreamRunsAfterOnceAndReleasesTheGuard()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        var after = 0;
        using var hook = agent.Hooks.Register<AfterInvocationEvent>((value, _) => { after++; Assert.Null(value.Result); value.Resume = new("hidden"); return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iterator = agent.StreamAsync("start", cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent) break;
        await iterator.DisposeAsync();
        Assert.Equal(1, after); Assert.Single(model.Requests); Assert.Single(agent.Messages);
        hook.Dispose(); await agent.InvokeAsync("next", cancellationToken: deadline.Token);
    }

    [Fact]
    public async Task EarlyDisposalRetainsAnIteratorCleanupFailure()
    {
        var cleanup = new IOException("iterator cleanup");
        await using var agent = new Agent(new() { Model = new FailingModel(null, cleanup) });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iterator = agent.StreamAsync("start", cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent) break;
        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(async () => await iterator.DisposeAsync()));
        agent.AddTool(new FunctionTool(new("after_cleanup", "Allowed after cleanup", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ok")));
    }

    [Fact]
    public async Task BodyFailureRetainsItsIdentityAndEveryCleanupAndIgnoresResume()
    {
        var primary = new InvalidOperationException("provider failed");
        var cleanup = new IOException("provider cleanup");
        var afterFailure = new IOException("after cleanup");
        var model = new FailingModel(primary, cleanup);
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { Assert.Same(primary, value.Error); value.Resume = new("ignored"); throw afterFailure; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("start", cancellationToken: deadline.Token)));
        var errors = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
        Assert.Contains(cleanup, errors); Assert.Contains(afterFailure, errors); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task CancellationAndBeforeDenialPreventHookContinuation()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("ignored"); deadline.Cancel(); return default; }))
            await agent.InvokeAsync("start", cancellationToken: deadline.Token);
        Assert.Single(model.Requests);
        using var denied = agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        using var again = agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("ignored"); return default; });
        var result = await agent.InvokeAsync("denied input");
        Assert.Equal("denied", result.ToString()); Assert.Single(model.Requests);
        Assert.DoesNotContain(agent.Messages, message => message.Text is "ignored" or "denied input");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAResumedRunUsesTheSelectedAccountingBoundary(bool reset)
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var before = 0;
        agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { if (++before == 2) deadline.Cancel(); return default; });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("next"); return default; });
        var result = await agent.InvokeAsync("start", new() { ResetLimitsOnHookResume = reset }, deadline.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason); Assert.Single(model.Requests);
        Assert.Equal(reset ? 0 : 1, result.Turns); Assert.Equal(reset ? 0 : 5, result.Usage.TotalTokens);
        Assert.Equal(reset ? null : "answer 1", result.Message?.Text);
        Assert.Single(agent.Messages, message => message.Role == Role.User);
    }

    [Fact]
    public async Task LastAfterCallbackWinsAndTheOuterInvocationGuardRemainsHeld()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        var callbacks = new List<string>();
        agent.Hooks.Register<AfterInvocationEvent>(async (value, _) =>
        {
            callbacks.Add("last");
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("recursive"));
            value.Resume = null;
        });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { callbacks.Add("first"); value.Resume = new("discarded"); return default; });
        await agent.InvokeAsync("start");
        Assert.Equal(new[] { "first", "last" }, callbacks); Assert.Single(model.Requests);
    }

    [Fact]
    public async Task DisposalAtBeforeInvocationCannotCommitInputOrStartTheModel()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        var after = 0;
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { after++; Assert.Null(value.Result); value.Resume = new("ignored"); return default; });
        var iterator = agent.StreamAsync("unaccepted").GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync()); Assert.IsType<BeforeInvocationEvent>(iterator.Current);
        await iterator.DisposeAsync();
        Assert.Equal(1, after); Assert.Empty(model.Requests); Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task ErrorsExposeTheAfterBoundaryBeforeRethrowAndNeverEmitAFinalResult()
    {
        var primary = new InvalidOperationException("provider failed");
        await using var agent = new Agent(new() { Model = new FailingModel(primary, new IOException("cleanup")) });
        var events = new List<IAgentStreamEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in agent.StreamAsync("start", cancellationToken: deadline.Token)) events.Add(item);
        }));
        Assert.Same(primary, Assert.Single(events.OfType<AfterInvocationEvent>()).Error);
        Assert.Empty(events.OfType<AgentResultEvent>());
    }

    [Fact]
    public async Task HookResumeCannotBypassStructuredOutputValidation()
    {
        var calls = 0;
        var model = new ToolModel("strands_structured_output", "{\"value\":1}");
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { if (++calls == 1) value.Resume = InvocationContinuation.Continue; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync<Output>("format", new()
        {
            Schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { value = new { type = "integer" } },
                required = new[] { "value" },
                additionalProperties = false
            })
        }, cancellationToken: deadline.Token));
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ResetLimitsAppliesOnlyToExplicitHookResumeAndNotAutomaticBackgroundDelivery()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new BackgroundModel(started.Task, release);
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, ct) =>
        {
            started.TrySetResult(true); await release.Task; ct.ThrowIfCancellationRequested(); return "completed";
        });
        await using var agent = new Agent(new() { Model = model, Tools = new[] { work }, BackgroundTasks = new() { Always = new[] { "work" } } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await agent.InvokeAsync("background", new() { MaxTurns = 2, ResetLimitsOnHookResume = true }, deadline.Token);
            Assert.Equal(StopReasons.MaxTurns, result.StopReason); Assert.Equal(2, result.Turns); Assert.Equal(2, model.Calls);
            Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == "strands_manage_background_task");
            Assert.NotNull(agent.State.Get("strands.backgroundTasks"));
            var next = await agent.InvokeAsync(null, cancellationToken: deadline.Token);
            Assert.Equal(1, next.Turns); Assert.Equal(3, model.Calls);
            Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.Content.Contains("completed"));
            Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == "strands_manage_background_task");
            Assert.Null(agent.State.Get("strands.backgroundTasks"));
        }
        finally { release.TrySetResult(true); }
    }

    public sealed record Output(int Value);

    private sealed class Model : IModel
    {
        public List<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var call = Requests.Count;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer " + call);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(call * 2, call * 3));
            await Task.CompletedTask;
        }
    }

    private sealed class ToolModel(string toolName = "work", string input = "{}") : IModel
    {
        public List<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            if (Requests.Count == 1)
                yield return new ModelContentBlockStartEvent(0, new("call", toolName, JsonSerializer.Deserialize<JsonElement>(input)));
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Last().Content);
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(Requests.Count == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
    }

    private sealed class BackgroundModel(Task started, TaskCompletionSource<bool> release) : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 2)
            {
                var waiting = Task.Delay(Timeout.Infinite, cancellationToken);
                if (await Task.WhenAny(started, waiting) != started) cancellationToken.ThrowIfCancellationRequested();
                await started; release.TrySetResult(true);
            }
            yield return new ModelMessageStartEvent();
            if (Calls == 1) yield return new ModelContentBlockStartEvent(0, new("background", "work", JsonSerializer.SerializeToElement(new { })));
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "waiting");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(Calls == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FailingModel(Exception? primary, Exception cleanup) : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return new FailingStream(primary, cleanup); }
    }

    private sealed class FailingStream(Exception? primary, Exception cleanup) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        private bool _started;
        public ModelStreamEvent Current => new ModelMessageStartEvent();
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (primary is not null) throw primary;
            if (_started) return new(false);
            _started = true; return new(true);
        }
        public ValueTask DisposeAsync() => throw cleanup;
    }
}
