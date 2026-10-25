using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerCardTests
{
    [Fact]
    public async Task DiscoveryUsesPinnedApplicationVersionAndCopiedExplicitSkillsWithoutCreatingAnAgent()
    {
        var factories = 0;
        var skill = new A2AAgentSkill(JsonSerializer.SerializeToElement(new { id = "weather", name = "Weather", description = "Read weather", tags = new[] { "weather" }, examples = new[] { "Rain?" }, metadata = new { extension = true } }));
        var skills = new List<A2AAgentSkill> { skill };
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; throw new InvalidOperationException("Discovery does not create agents."); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { Skills = skills });
        skills.Clear();
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var card = await new A2AClient(http, new(new Uri("http://local/"))).GetAgentCardAsync();

        Assert.Equal("0.0.1", card.GetProperty("version").GetString()); Assert.Equal(0, factories);
        Assert.Equal(skill.Json.GetRawText(), card.GetProperty("skills")[0].GetRawText());
    }
    [Fact]
    public async Task AdditionalCardFieldsRetainProviderSecurityAndExtensionsBeyondDocumentLifetime()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        A2AServer server;
        using (var fields = JsonDocument.Parse("""{"provider":{"organization":"Weather","url":"https://weather.example/"},"documentationUrl":"https://weather.example/docs","securitySchemes":{"bearer":{"type":"http","scheme":"Bearer"}},"security":[{"bearer":[]}],"customExtension":{"enabled":true}}"""))
            server = new(executor, A2AServerTests.Options() with { AdditionalCardFields = fields.RootElement });
        await using var serverLifetime = server;
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var card = await new A2AClient(http, new(new Uri("http://local/"))).GetAgentCardAsync();

        Assert.Equal("Weather", card.GetProperty("provider").GetProperty("organization").GetString());
        Assert.True(card.GetProperty("customExtension").GetProperty("enabled").GetBoolean());
        Assert.Equal("Bearer", server.AgentCard.GetProperty("securitySchemes").GetProperty("bearer").GetProperty("scheme").GetString());
        Assert.False(card.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
    }
    [Fact]
    public async Task AtomicCardUpdatesPreserveEarlierViewsAndSupportExplicitMetadataSkillsAndPublicUrl()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        var previous = server.AgentCard; var updated = JsonNode.Parse(previous.GetRawText())!;
        updated["name"] = "Updated"; updated["url"] = "https://public.example/agent/rpc";
        updated["skills"] = JsonNode.Parse("""[{"id":"weather","name":"Weather","description":"Read weather","tags":[]}]""");
        using (var document = JsonDocument.Parse(updated.ToJsonString())) server.UpdateAgentCard(document.RootElement);
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var current = await new A2AClient(http, new(new Uri("http://local/"))).GetAgentCardAsync();

        Assert.Equal("Updated", current.GetProperty("name").GetString());
        Assert.Equal("https://public.example/agent/rpc", current.GetProperty("url").GetString());
        Assert.Equal("weather", current.GetProperty("skills")[0].GetProperty("id").GetString());
        Assert.Equal(A2AServerTests.Options().Name, previous.GetProperty("name").GetString());
        Assert.Equal(0, previous.GetProperty("skills").GetArrayLength());
    }
    [Theory]
    [InlineData("url", "\"relative\"")]
    [InlineData("protocolVersion", "\"1.0\"")]
    [InlineData("preferredTransport", "\"REST\"")]
    [InlineData("defaultInputModes", "[\"image/png\"]")]
    [InlineData("defaultOutputModes", "[]")]
    [InlineData("skills", "[{\"id\":\"x\",\"name\":\"X\",\"description\":\"x\",\"tags\":[]},{\"id\":\"x\",\"name\":\"X\",\"description\":\"x\",\"tags\":[]}]")]
    [InlineData("capabilities", "{\"streaming\":true,\"pushNotifications\":true}")]
    [InlineData("capabilities", "{\"streaming\":\"true\"}")]
    [InlineData("security", "[{\"unknown\":true}]")]
    [InlineData("provider", "{\"organization\":\"X\"}")]
    [InlineData("additionalInterfaces", "[{\"transport\":\"GRPC\",\"url\":\"https://other.example/rpc\"}]")]
    [InlineData("supportsAuthenticatedExtendedCard", "true")]
    public async Task InvalidOrUnsupportedCardsFailAtomicallyBeforeAdvertisement(string property, string value)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        var previous = server.AgentCard; var altered = JsonNode.Parse(previous.GetRawText())!; altered[property] = JsonNode.Parse(value);

        Assert.Throws<A2AProtocolException>(() => server.UpdateAgentCard(JsonSerializer.SerializeToElement(altered)));

        Assert.Equal(previous.GetRawText(), server.AgentCard.GetRawText());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardByteBoundsChargeSerializedUnicodeAndPreserveThePriorCard(bool unicode)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var template = new A2AServer(executor, A2AServerTests.Options());
        var maximum = JsonSerializer.SerializeToUtf8Bytes(template.AgentCard).Length + 250;
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { MaxCardBytes = maximum });
        var previous = server.AgentCard; var altered = JsonNode.Parse(previous.GetRawText())!;
        altered["customExtension"] = new string(unicode ? 'é' : 'x', unicode ? 100 : 5000);
        using var document = JsonDocument.Parse(altered.ToJsonString(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (unicode) Assert.True(System.Text.Encoding.UTF8.GetByteCount(document.RootElement.GetRawText()) < maximum);

        Assert.Throws<A2AProtocolException>(() => server.UpdateAgentCard(document.RootElement));

        Assert.Equal(previous.GetRawText(), server.AgentCard.GetRawText());
    }
    [Theory]
    [InlineData("defaultInputModes")]
    [InlineData("defaultOutputModes")]
    public async Task MissingRequiredModeListsProduceAProtocolErrorWithoutReplacingTheCard(string property)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        var previous = server.AgentCard; var altered = JsonNode.Parse(previous.GetRawText())!.AsObject(); altered.Remove(property);
        Assert.Throws<A2AProtocolException>(() => server.UpdateAgentCard(JsonSerializer.SerializeToElement(altered)));
        Assert.Equal(previous.GetRawText(), server.AgentCard.GetRawText());
    }
    [Fact]
    public async Task WildcardRuntimeSupportCannotAdvertiseAMalformedMimeType()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { InputModes = ["*/*"] });
        var altered = JsonNode.Parse(server.AgentCard.GetRawText())!; altered["defaultInputModes"] = JsonNode.Parse("""["not a MIME type"]""");
        Assert.Throws<A2AProtocolException>(() => server.UpdateAgentCard(JsonSerializer.SerializeToElement(altered)));
    }
    [Theory]
    [InlineData(A2ACardFormat.TypeScript, "0.2.0", "text/plain")]
    [InlineData(A2ACardFormat.Python, "0.3.0", "text")]
    public async Task ExplicitLanguageFormatsPreserveTheirPinnedDiscoveryDefaults(A2ACardFormat format, string protocol, string mode)
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { CardFormat = format });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var card = await new A2AClient(http, new(new Uri("http://local/"))).GetAgentCardAsync();
        Assert.Equal(protocol, card.GetProperty("protocolVersion").GetString());
        Assert.Equal(mode, Assert.Single(card.GetProperty("defaultInputModes").EnumerateArray()).GetString());
        Assert.Equal(mode, Assert.Single(card.GetProperty("defaultOutputModes").EnumerateArray()).GetString());
        Assert.Equal("0.0.1", card.GetProperty("version").GetString());
    }
    [Fact]
    public async Task CompatibilityFormatsCannotAdvertiseUnsupportedTextAndUnknownFormatsAreRejected()
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        foreach (var format in new[] { A2ACardFormat.TypeScript, A2ACardFormat.Python })
            Assert.Throws<A2AProtocolException>(() => new A2AServer(executor, A2AServerTests.Options() with { CardFormat = format, InputModes = ["image/png"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new A2AServer(executor, A2AServerTests.Options() with { CardFormat = (A2ACardFormat)999 }));
    }
}
