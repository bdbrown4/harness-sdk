using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class TypedContentTests
{
    [Fact]
    public async Task OpaqueReasoningAndTypedJsonSurviveTheModelStreamAndSnapshot()
    {
        using var document = JsonDocument.Parse("{\"number\":42}");
        var model = new AgentTests.ScriptedModel([
            new ModelMessageStartEvent(),
            new ModelContentBlockStartEvent(0) { ContentBlock = new RedactedReasoningBlock("opaque-signature") },
            new ModelContentBlockStopEvent(0),
            new ModelContentBlockStartEvent(1) { ContentBlock = new JsonBlock(document.RootElement) },
            new ModelContentBlockStopEvent(1),
            new ModelMessageStopEvent(StopReasons.EndTurn)]);
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("question");
        document.Dispose();

        Assert.Equal("opaque-signature", Assert.IsType<RedactedReasoningBlock>(agent.Messages[1].Content[0]).Data);
        Assert.Equal(42, Assert.IsType<JsonBlock>(agent.Messages[1].Content[1]).Json.GetProperty("number").GetInt32());
        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(agent.Messages[1]))!;
        Assert.Equal("opaque-signature", Assert.IsType<RedactedReasoningBlock>(restored.Content[0]).Data);
    }
}
