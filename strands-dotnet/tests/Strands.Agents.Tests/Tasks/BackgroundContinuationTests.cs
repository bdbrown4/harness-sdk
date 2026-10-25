using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.Tasks;

public sealed class BackgroundContinuationTests
{
    [Fact]
    public async Task ShutdownAbandonsDeferredInputOnceWithoutAppendingIt()
    {
        var appended = 0; var abandoned = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        await using var agent = new Agent(new() { Model = new Model(new(TaskCreationOptions.RunContinuationsAsynchronously)), Tools = new[] { work } });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            agent.AddContinuationInput(value, new[] { Message.User("deferred input") }, () => { appended++; return default; }, _ => { abandoned++; return default; });
            return default;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("request", cancellationToken: deadline.Token)).StopReason);
        Assert.Equal(0, appended); Assert.Equal(0, abandoned);
        await agent.ShutdownAsync(); await agent.ShutdownAsync();
        Assert.Equal(0, appended); Assert.Equal(1, abandoned);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "deferred input");
    }

    [Fact]
    public async Task CallerCancellationDuringAutomaticWaitingReturnsCancelledAndLeavesAcceptedWorkRunning()
    {
        var modelRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, ct) =>
        { started.TrySetResult(ct); await Wait(workRelease.Task, ct); return "accepted work"; });
        await using var agent = new Agent(new() { Model = new Model(modelRelease), Tools = new[] { work }, BackgroundTasks = new() { Always = new[] { "work" } } });
        agent.Hooks.Register<AfterInvocationEvent>(async (_, _) =>
        {
            await Wait(started.Task, deadline.Token);
            // The body is admitted and remains gated. This cancels only the caller's automatic wait.
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        }, -1);
        try
        {
            var invocation = agent.InvokeAsync("request", cancellationToken: cancellation.Token);
            await Wait(invocation, deadline.Token);
            Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
            Assert.False((await started.Task).IsCancellationRequested);
            Assert.NotNull(agent.State.Get("strands.backgroundTasks"));
        }
        finally { workRelease.TrySetResult(true); modelRelease.TrySetResult(true); }
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("after-hook")]
    [InlineData("message-observer")]
    [InlineData("session-write")]
    public async Task UnappendedDeliverySurvivesDisposalAndFailuresWithoutRepeatingAnAcceptedPair(string boundary)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Model(release); var bodies = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, ct) =>
        { await Wait(release.Task, ct); bodies++; return "background result"; });
        var expected = new IOException(boundary); var fail = true;
        var store = boundary == "session-write" ? new FailingDeliveryStore(expected) : null;
        await using var agent = new Agent(new() { Model = model, Tools = new[] { work }, BackgroundTasks = new() { Always = new[] { "work" } }, SessionStore = store, SessionId = store is null ? null : "delivery" });
        if (boundary == "after-hook") agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        { if (fail && value.Result?.ToString() == "waiting") { fail = false; throw expected; } return default; });
        if (boundary == "message-observer") agent.Hooks.Register<MessageAddedEvent>((value, _) =>
        { if (fail && value.Message.Content.OfType<ToolUseBlock>().Any(call => call.Name == BackgroundTaskPolicy.ManageToolName)) { fail = false; throw expected; } return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "dispose")
            {
                var iterator = agent.StreamAsync("request", cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
                try { while (await iterator.MoveNextAsync()) if (iterator.Current is AfterInvocationEvent) break; }
                finally { await iterator.DisposeAsync(); }
            }
            else Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("request", cancellationToken: deadline.Token)));
            Assert.Equal(2, model.Calls); Assert.Equal(1, bodies);
            var acceptedPairs = agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Count(call => call.Name == BackgroundTaskPolicy.ManageToolName);
            var committed = boundary is "message-observer" or "session-write";
            Assert.Equal(committed ? 1 : 0, acceptedPairs);
            Assert.Equal(committed, agent.State.Get("strands.backgroundTasks") is null);
            Assert.Equal("received", (await agent.InvokeAsync(null, cancellationToken: deadline.Token)).ToString());
            Assert.Equal(3, model.Calls); Assert.Equal(1, bodies);
            var management = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == BackgroundTaskPolicy.ManageToolName);
            Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.ToolUseId == management.ToolUseId);
            Assert.Null(agent.State.Get("strands.backgroundTasks"));
            if (store is not null)
            {
                Assert.NotNull(store.Saved);
                ConversationHistory.Validate(store.Saved.Messages);
                Assert.Single(store.Saved.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == BackgroundTaskPolicy.ManageToolName);
            }
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task BeforeModelInputCommitsAndSettlesBeforeObserversAndPreservesRequestPolicy()
    {
        var model = new RecordingModel(); var appended = 0; var observed = 0;
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            agent.AddContinuationInput(value, new[] { Message.User("queued input") }, () => { appended++; return default; });
            value.Request = value.Request with { SystemPrompt = "effective policy" }; return default;
        });
        agent.Hooks.Register<MessageAddedEvent>((value, _) =>
        {
            if (value.Message.Role == Role.User && value.Message.Text == "queued input") { Assert.Equal(1, appended); observed++; }
            return default;
        });
        var result = await agent.InvokeAsync("original input");
        Assert.Equal("queued input", result.ToString()); Assert.Equal("effective policy", model.Request!.SystemPrompt);
        Assert.Equal(1, appended); Assert.Equal(1, observed);
        Assert.Contains(agent.Messages, message => message.Text == "queued input");
    }

    [Fact]
    public async Task ABeforeModelHookFailureAbandonsItsInputAndRetainsThePrimaryException()
    {
        var model = new RecordingModel(); var abandoned = 0; Exception? reason = null;
        var expected = new IOException("before model failed");
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            agent.AddContinuationInput(value, new[] { Message.User("unappended") }, () => throw new IOException("must not append"), error => { abandoned++; reason = error; return default; });
            throw expected;
        });
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("original")));
        Assert.Equal(1, abandoned); Assert.Same(expected, reason); Assert.Null(model.Request);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "unappended");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticHumanInputContinuationRetainsItsCumulativeAccounting(bool resetExplicitLimits)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, context, ct) =>
        { await Wait(release.Task, ct); return context.Interrupt("approval").GetString()!; });
        await using var agent = new Agent(new() { Model = new Model(release), Tools = new[] { work }, BackgroundTasks = new() { Always = new[] { "work" } } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var paused = await agent.InvokeAsync("request", new() { ResetLimitsOnHookResume = resetExplicitLimits }, deadline.Token);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Single(paused.Interrupts); Assert.Equal(2, paused.Turns);
            Assert.Equal("waiting", paused.ToString());
        }
        finally { release.TrySetResult(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticDeliveryStartsANewLifecycleCycleWithoutResettingLimitsOrAccounting(bool resetExplicitLimits)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = 0; var after = 0; var bodies = 0;
        var state = new Dictionary<string, object?> { ["tenant"] = "original" };
        var model = new Model(release);
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, context, ct) =>
        {
            Assert.Same(state, context.InvocationState);
            await Wait(release.Task, ct); bodies++; return "background result";
        });
        await using var agent = new Agent(new() { Model = model, Tools = new[] { work }, BackgroundTasks = new() { Always = new[] { "work" } } });
        agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { Assert.Same(state, value.InvocationState); before++; return default; });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { Assert.Same(state, value.InvocationState); after++; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        AgentResult result;
        try { result = await agent.InvokeAsync("request", new() { InvocationState = state, ResetLimitsOnHookResume = resetExplicitLimits, MaxTurns = 3 }, deadline.Token); }
        finally { release.TrySetResult(true); }
        Assert.Equal("received", result.ToString()); Assert.Equal(3, result.Turns); Assert.Equal(2, before); Assert.Equal(2, after); Assert.Equal(1, bodies);
        Assert.Equal(3, model.Calls); Assert.Same(state, result.InvocationState); Assert.Single(state);
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    private static async Task Wait(Task task, CancellationToken cancellationToken)
    {
        if (await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken)) != task) cancellationToken.ThrowIfCancellationRequested();
        await task;
    }

    private sealed class Model(TaskCompletionSource<bool> release) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            var results = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
            var delivered = request.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(call => call.Name == BackgroundTaskPolicy.ManageToolName);
            yield return new ModelMessageStartEvent();
            if (results.Length == 0) yield return new ModelContentBlockStartEvent(0, new("call", "work", JsonSerializer.SerializeToElement(new { })));
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: delivered ? "received" : "waiting");
                if (!delivered) release.TrySetResult(true);
            }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(results.Length == 0 ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class RecordingModel : IModel
    {
        internal ModelRequest? Request;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: request.Messages.Last().Text);
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class FailingDeliveryStore(Exception expected) : ISessionStore
    {
        private bool _fail = true;
        internal SessionSnapshot? Saved;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Saved);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (_fail && snapshot.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(call => call.Name == BackgroundTaskPolicy.ManageToolName))
            { _fail = false; throw expected; }
            Saved = snapshot; return default;
        }
    }
}
