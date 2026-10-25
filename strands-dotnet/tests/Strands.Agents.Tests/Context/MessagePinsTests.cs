using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class MessagePinsTests
{
    [Fact]
    public void PinPreservesOtherMetadataWithoutMutatingTheOriginal()
    {
        var original = new Message(Role.User, [new TextBlock("keep")], "tracked", JsonSerializer.SerializeToElement(new { custom = new { key = "value" }, usage = new { tokens = 12 } }));

        var pinned = MessagePins.Pin(original);

        Assert.True(pinned.Metadata!.Value.GetProperty("custom").GetProperty("pinned").GetBoolean());
        Assert.Equal("value", pinned.Metadata.Value.GetProperty("custom").GetProperty("key").GetString());
        Assert.Equal(12, pinned.Metadata.Value.GetProperty("usage").GetProperty("tokens").GetInt32());
        Assert.Equal("tracked", pinned.TrackingId);
        Assert.False(original.Metadata!.Value.GetProperty("custom").TryGetProperty("pinned", out _));
    }

    [Fact]
    public void UnpinRetainsOtherCustomFieldsAndDoesNotChangeThePinnedSnapshot()
    {
        var original = MessagePins.Pin(new(Role.User, [new TextBlock("keep")], metadata: JsonSerializer.SerializeToElement(new { custom = new { key = "value" } })));

        var unpinned = MessagePins.Unpin(original);

        Assert.False(unpinned.Metadata!.Value.GetProperty("custom").TryGetProperty("pinned", out _));
        Assert.Equal("value", unpinned.Metadata.Value.GetProperty("custom").GetProperty("key").GetString());
        Assert.True(MessagePins.IsPinned([original], 0));
    }

    [Fact]
    public void ApplyPinFirstCapsItsCountAndReplacesOnlyListEntries()
    {
        var first = Message.User("first");
        var second = Message.Assistant("second");
        List<Message> messages = [first, second];

        MessagePins.ApplyPinFirst(messages, 10);

        Assert.True(MessagePins.IsPinned(messages, 0));
        Assert.True(MessagePins.IsPinned(messages, 1));
        Assert.Null(first.Metadata);
        Assert.Null(second.Metadata);
    }
}
