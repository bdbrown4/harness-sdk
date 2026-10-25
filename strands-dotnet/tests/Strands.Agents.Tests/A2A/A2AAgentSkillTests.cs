using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AAgentSkillTests
{
    [Fact]
    public void SkillCopiesProtocolFieldsAndExtensionsBeyondTheSourceDocumentLifetime()
    {
        const string raw = """{"id":"weather","name":"Weather","description":"Read weather","tags":["weather"],"examples":["Will it rain?"],"inputModes":["text/plain"],"outputModes":["application/json"],"metadata":{"owner":"weather"},"customExtension":{"nested":true}}""";
        A2AAgentSkill skill;
        using (var document = JsonDocument.Parse(raw)) skill = new(document.RootElement);

        Assert.Equal(raw, skill.Json.GetRawText());
        Assert.True(skill.Json.GetProperty("customExtension").GetProperty("nested").GetBoolean());
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":\"Read weather\"}")]
    [InlineData("{\"id\":\"weather\",\"name\":null,\"description\":\"Read weather\",\"tags\":[]}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":1,\"tags\":[]}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":\"\",\"tags\":[1]}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":\"\",\"tags\":[],\"examples\":null}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":\"\",\"tags\":[],\"inputModes\":{}}")]
    [InlineData("{\"id\":\"weather\",\"name\":\"Weather\",\"description\":\"\",\"tags\":[],\"security\":[{\"oauth\":true}]}")]
    public void InvalidProtocolMemberTypesFailBeforeAdvertisement(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        Assert.Throws<A2AProtocolException>(() => new A2AAgentSkill(document.RootElement));
    }
    [Fact]
    public void ToolMetadataMapsToThePythonSkillShapeWithoutInvokingItsBody()
    {
        var skill = A2AAgentSkill.FromTool(new("weather", "Read weather", JsonSerializer.SerializeToElement(new { type = "object" })));

        Assert.Equal("weather", skill.Json.GetProperty("id").GetString());
        Assert.Equal("weather", skill.Json.GetProperty("name").GetString());
        Assert.Equal("Read weather", skill.Json.GetProperty("description").GetString());
        Assert.Equal(0, skill.Json.GetProperty("tags").GetArrayLength());
        Assert.False(skill.Json.TryGetProperty("inputSchema", out _));
    }
}
