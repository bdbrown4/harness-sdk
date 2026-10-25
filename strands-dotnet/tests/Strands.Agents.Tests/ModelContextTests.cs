using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ModelContextTests
{
    [Fact]
    public async Task ReplacedRequestIsSentToModelWithoutMutatingDurableHistory()
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("answer"));
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            var replacement = value.Request with { SystemPrompt = "ephemeral", Messages = [Message.User("injected")] };
            value.Request = replacement;
            return ValueTask.CompletedTask;
        });
        var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("durable");

        Assert.Equal("ephemeral", model.Requests[0].SystemPrompt);
        Assert.Equal("injected", model.Requests[0].Messages[0].Text);
        Assert.Equal("durable", agent.Messages[0].Text);
    }
}
