using System.Text.Json;

namespace Strands.Agents.Tests.EventParity;

public sealed class EventPayloadCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewCompletedEventPayloadsExcludeAgentAndNonserializableCallerContext(bool messageEvent)
    {
        var state = new Dictionary<string, object?> { ["callback"] = (Action)(() => { }) }; var hooks = new HookRegistry(); ContentBlockEvent? block = null; ModelMessageEvent? message = null;
        hooks.Register<ContentBlockEvent>((value, _) => { block = value; return default; });
        hooks.Register<ModelMessageEvent>((value, _) => { message = value; return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        state["agent"] = agent;
        await agent.InvokeAsync("question", new() { InvocationState = state });
        JsonElement actual, projected;
        if (messageEvent)
        {
            actual = JsonSerializer.SerializeToElement(message!); projected = JsonSerializer.SerializeToElement(new { message!.Message, message.StopReason });
        }
        else
        {
            actual = JsonSerializer.SerializeToElement(block!); projected = JsonSerializer.SerializeToElement(new { block!.ContentBlock });
        }
        Assert.True(JsonElement.DeepEquals(projected, actual)); Assert.False(actual.TryGetProperty("Agent", out _)); Assert.False(actual.TryGetProperty("InvocationState", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindingRuntimeContextKeepsTheOriginalPositionalEventJsonPayload(bool resultEvent)
    {
        var state = new Dictionary<string, object?>(); var hooks = new HookRegistry(); ModelStreamUpdateEvent? update = null; AgentResultEvent? result = null;
        hooks.Register<ModelStreamUpdateEvent>((value, _) => { update ??= value; return default; });
        hooks.Register<AgentResultEvent>((value, _) => { result = value; return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        await agent.InvokeAsync("question", new() { InvocationState = state });
        JsonElement actual, original;
        if (resultEvent)
        {
            actual = JsonSerializer.SerializeToElement(result!); original = JsonSerializer.SerializeToElement(new AgentResultEvent(result!.Result));
        }
        else
        {
            actual = JsonSerializer.SerializeToElement(update!); original = JsonSerializer.SerializeToElement(new ModelStreamUpdateEvent(update!.Event));
        }
        Assert.True(JsonElement.DeepEquals(original, actual), "Invocation binding must not add agent/context fields to an existing event's JSON payload.");
    }
}
