using System.Text.Json;
using Strands.Agents;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.ConversationSupplement;

public sealed class ClassicMetadataSupplementTests
{
    [Fact]
    public async Task ReactiveTruncationRetainsDetachedRichResultMetadataAndExecutionFacts()
    {
        var failure = new IOException("original execution failure");
        ToolResultBlock result;
        using (var producer = JsonDocument.Parse("""{"protocol":{"receipt":"retained"}}"""))
            result = new("call", [new TextBlock(new string('x', 1000))], ToolResultStatus.Error, JsonSerializer.SerializeToElement(new { fact = "structured" }))
            { Metadata = producer.RootElement, Error = failure, IteratorCleanupFailed = true };
        await using var agent = new Agent(new()
        {
            Model = new global::Strands.Agents.Tests.Conversation.ClassicConversationManagerTests.TextModel(),
            Messages = [Message.User("task"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [result])]
        });
        var manager = new Classic.SlidingWindowConversationManager();
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        var truncated = Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
        Assert.Equal("retained", truncated.Metadata!.Value.GetProperty("protocol").GetProperty("receipt").GetString());
        Assert.Equal("structured", truncated.StructuredContent!.Value.GetProperty("fact").GetString());
        Assert.Equal("call", truncated.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, truncated.Status);
        Assert.Same(failure, truncated.Error);
        Assert.True(truncated.IteratorCleanupFailed);
        Assert.Contains("[truncated:", truncated.Content);
    }
}
