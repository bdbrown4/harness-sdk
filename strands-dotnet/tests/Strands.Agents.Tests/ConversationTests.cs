using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ConversationTests
{
    [Fact]
    public void WindowKeepsEntireLatestUserExchangeWithoutMutatingHistory()
    {
        Message[] messages = [Message.User("old"), Message.Assistant("old answer"), Message.User("new"),
            new(Role.Assistant, [new ToolUseBlock("a", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [new ToolResultBlock("a", "found")]), Message.Assistant("answer")];
        var manager = new SlidingWindowConversationManager(2);

        var reduced = manager.Reduce(messages);

        Assert.Equal(messages.Skip(2), reduced);
        Assert.Equal(6, messages.Length);
    }
}
