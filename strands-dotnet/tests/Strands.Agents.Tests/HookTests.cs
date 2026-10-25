namespace Strands.Agents.Tests;

public sealed class HookTests
{
    [Theory]
    [InlineData("move")]
    [InlineData("factory")]
    [InlineData("enumerator")]
    public async Task ModelLifetimeFailuresPreserveThePrimaryException(string stage)
    {
        var primary = new ModelException("model failed");
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((_, _) => ValueTask.FromException(new InvalidOperationException("hook cleanup failed")));
        var agent = new Agent(new() { Model = new LifetimeFailureModel(stage, primary), Hooks = hooks });

        var actual = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("hi"));

        Assert.Same(primary, actual);
        Assert.Equal(stage == "move" ? 2 : 1, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]).Count);
    }

    [Fact]
    public async Task CleanupHooksPreservePrimaryModelFailure()
    {
        var primary = new ModelException("upstream failed");
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((_, _) => ValueTask.FromException(new InvalidOperationException("model cleanup failed")));
        hooks.Register<AfterInvocationEvent>((_, _) => ValueTask.FromException(new InvalidOperationException("invocation cleanup failed")));
        var agent = new Agent(new() { Model = new FailingModel(primary), Hooks = hooks });

        var actual = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("hi"));

        Assert.Same(primary, actual);
        Assert.Equal(2, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]).Count);
    }

    [Fact]
    public async Task CancellationInsideBeforeHookPreventsToolBody()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { cancellation.Cancel(); return ValueTask.CompletedTask; });
        var calls = 0;
        var tool = new FunctionTool(new("write", "Write a value", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => { calls++; return ValueTask.FromResult("written"); });
        var agent = new Agent(new()
        {
            Model = new AgentTests.ScriptedModel(AgentTests.Call(("one", "write", "{}"))),
            Tools = [tool],
            Hooks = hooks
        });

        var result = await agent.InvokeAsync("go", cancellationToken: cancellation.Token);

        Assert.Equal(0, calls);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(agent.Messages[^1].Content[0]).Status);
    }

    [Fact]
    public async Task AfterHooksKeepPriorityAndReverseRegistrationWithinPriority()
    {
        var hooks = new HookRegistry();
        var calls = new List<string>();
        hooks.Register<AfterInvocationEvent>((_, _) => { calls.Add("late-a"); return ValueTask.CompletedTask; }, 10);
        hooks.Register<AfterInvocationEvent>((_, _) => { calls.Add("early-a"); return ValueTask.CompletedTask; }, -10);
        hooks.Register<AfterInvocationEvent>((_, _) => { calls.Add("late-b"); return ValueTask.CompletedTask; }, 10);
        hooks.Register<AfterInvocationEvent>((_, _) => { calls.Add("early-b"); return ValueTask.CompletedTask; }, -10);
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("hi")), Hooks = hooks });

        await agent.InvokeAsync("hello");

        Assert.Equal(["early-b", "early-a", "late-b", "late-a"], calls);
    }

    private sealed class FailingModel(Exception failure) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            await Task.Yield();
            throw failure;
        }
    }

    private sealed class LifetimeFailureModel(string stage, Exception primary) : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            stage == "factory" ? throw primary : new Sequence(stage, primary);

        private sealed class Sequence(string stage, Exception primary) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            public ModelStreamEvent Current => new ModelMessageStartEvent();
            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                stage == "enumerator" ? throw primary : this;
            public ValueTask<bool> MoveNextAsync() => new(Task.FromException<bool>(primary));
            public ValueTask DisposeAsync() => new(Task.FromException(new IOException("dispose failed")));
        }
    }
}
