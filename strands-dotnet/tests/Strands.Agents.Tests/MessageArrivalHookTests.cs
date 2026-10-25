namespace Strands.Agents.Tests;

public sealed class MessageArrivalHookTests
{
    [Fact]
    public async Task MessageCallbacksAreAwaitedAfterCommitAndBeforeTheNextModelRequest()
    {
        var observed = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>(async (value, cancellationToken) =>
        {
            Assert.Contains(value.Message, value.Agent.Messages);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            observed.Add(value.Message.Text);
        });
        hooks.Register<BeforeModelCallEvent>((_, _) =>
        { Assert.Equal(["hello"], observed); return default; });
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("ready")), Hooks = hooks });

        await agent.InvokeAsync("hello");

        Assert.Equal(["hello", "ready"], observed);
    }
}
