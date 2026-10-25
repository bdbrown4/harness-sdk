using System.Text.Json;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class SessionRecordJsonTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("agent")]
    [InlineData("message")]
    public void PublicRecordsSupportNormalNativeJsonRoundtrips(string kind)
    {
        var stamp = DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        if (kind == "session")
        {
            var original = new SessionRecord("session", createdAt: stamp);
            Assert.Equal(original, JsonSerializer.Deserialize<SessionRecord>(JsonSerializer.Serialize(original)));
        }
        else if (kind == "agent")
        {
            var original = new SessionAgentRecord("agent", SessionRepositoryTests.Object("state"), SessionRepositoryTests.Object("manager"), createdAt: stamp);
            var restored = JsonSerializer.Deserialize<SessionAgentRecord>(JsonSerializer.Serialize(original))!;
            Assert.Equal(stamp, restored.CreatedAt);
            Assert.Equal(original.State.GetRawText(), restored.State.GetRawText());
        }
        else
        {
            var original = new SessionMessageRecord(new(Role.User, [new TextBlock("original")], "tracking"), 10, new(Role.User, [new TextBlock("redacted")], "tracking"), stamp);
            var restored = JsonSerializer.Deserialize<SessionMessageRecord>(JsonSerializer.Serialize(original))!;
            Assert.Equal(stamp, restored.CreatedAt);
            Assert.Equal("redacted", restored.ToMessage().Text);
            Assert.Equal("tracking", restored.Message.TrackingId);
        }
    }
}
