using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserEnvelopeBoundaryTests
{
    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript, false)]
    [InlineData(HandoffToUserBehavior.Python, false)]
    [InlineData(HandoffToUserBehavior.TypeScript, true)]
    [InlineData(HandoffToUserBehavior.Python, true)]
    public async Task ExtraEnvelopeJsonIsRetainedWithoutDroppingFieldsOrTrustingTheForeignId(HandoffToUserBehavior behavior, bool empty)
    {
        var content = empty ? "[]" : "[{\"text\":\"declined\"}]";
        var raw = "{\"status\":\"error\",\"content\":" + content + ",\"toolUseId\":\"foreign\",\"metadata\":{\"source\":\"human\",\"number\":1.5},\"custom\":[{\"nullable\":null},\"h\u00e9llo\"]}";
        using var document = JsonDocument.Parse(raw);
        var reply = document.RootElement;
        var context = new ToolContext(null!, new("current", "handoff_to_user", Json("{}")), new Dictionary<string, object?>()) { InterruptHandler = (_, _, _) => reply };
        var result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Json("{\"message\":\"Q\"}"), context);
        Assert.Equal("current", result.ToolUseId);
        if (behavior == HandoffToUserBehavior.TypeScript)
        {
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Null(result.Metadata);
            Assert.True(JsonElement.DeepEquals(reply, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json));
        }
        else
        {
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.True(result.HasExplicitContent);
            if (empty) Assert.Empty(result.ContentBlocks);
            else Assert.Equal("declined", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
            var expected = Json("{\"metadata\":{\"source\":\"human\",\"number\":1.5},\"custom\":[{\"nullable\":null},\"h\u00e9llo\"]}");
            Assert.True(JsonElement.DeepEquals(expected, result.Metadata!.Value));
            var restored = JsonSerializer.Deserialize<ToolResultBlock>(JsonSerializer.Serialize(result))!;
            Assert.True(JsonElement.DeepEquals(expected, restored.Metadata!.Value));
            Assert.Equal("current", restored.ToolUseId);
        }
        Assert.Equal(raw, reply.GetRawText());
    }

    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
}
