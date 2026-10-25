using System.Text.Json;
using Strands.Agents.Tests.ToolBatchExecutor;

namespace Strands.Agents.Tests.EventParity;

public sealed class ToolBatchInputIntegrationTests
{
    [Fact]
    public async Task RedactedInputStaysOutOfHistoryWhenACustomExecutorJoinsDuplicateScheduling()
    {
        var state = new Dictionary<string, object?> { ["request"] = new object() };
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Same(state, value.InvocationState);
            Assert.Equal("SECRET", Assert.Single(value.Messages!).Text);
            value.Messages = [Message.User("REDACTED")];
            return default;
        });
        var bodies = 0;
        var tool = BatchTestSupport.Tool((_, context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            Interlocked.Increment(ref bodies);
            return new ValueTask<string>("completed");
        });
        var scheduler = new BatchTestSupport.Scheduler(async (context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            Assert.Single(context.Calls);
            var first = context.ExecuteAsync(0);
            Assert.Same(first, context.ExecuteAsync(0));
            Assert.Equal("completed", (await first)!.Content);
        });
        var model = new EventTestModel(
            [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("call", "work", JsonSerializer.SerializeToElement(new { }))),
             new ModelContentBlockDeltaEvent(0, ToolInput: "{}"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)],
            EventTestModel.Text("finished"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [tool], ToolBatchExecutor = scheduler });

        var result = await agent.InvokeAsync("SECRET", new() { InvocationState = state });

        Assert.Same(state, result.InvocationState);
        Assert.Single(state);
        Assert.Equal(1, bodies);
        Assert.Equal(1, scheduler.Calls);
        Assert.Equal("REDACTED", agent.Messages[0].Text);
        Assert.Equal("REDACTED", model.Requests[0].Messages[0].Text);
        Assert.DoesNotContain(agent.Messages, message => message.Text.Contains("SECRET"));
        Assert.Equal("finished", result.ToString());
    }
}
