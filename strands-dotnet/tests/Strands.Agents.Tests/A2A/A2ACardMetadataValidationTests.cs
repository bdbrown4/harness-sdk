using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ACardMetadataValidationTests
{
    [Theory]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"http\"}}")]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"apiKey\",\"name\":\"key\",\"in\":\"body\"}}")]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"oauth2\",\"flows\":[]}}")]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"oauth2\",\"flows\":{\"clientCredentials\":{\"tokenUrl\":\"https://auth.example/token\",\"scopes\":{\"read\":true}}}}}")]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"openIdConnect\",\"openIdConnectUrl\":\"relative\"}}")]
    [InlineData("securitySchemes", "{\"auth\":{\"type\":\"undefined\"}}")]
    [InlineData("capabilities", "{\"extensions\":[{\"uri\":\"urn:example:extension\",\"required\":\"yes\"}]}")]
    [InlineData("capabilities", "{\"extensions\":[{}]}")]
    [InlineData("signatures", "false")]
    [InlineData("signatures", "[1]")]
    [InlineData("signatures", "[{\"protected\":123,\"signature\":\"opaque\"}]")]
    public async Task StandardOptionalDiscoveryMembersMustHaveTheirProtocolShapes(string property, string raw)
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        var previous = server.AgentCard; var altered = JsonNode.Parse(previous.GetRawText())!; altered[property] = JsonNode.Parse(raw);
        Assert.Throws<A2AProtocolException>(() => server.UpdateAgentCard(JsonSerializer.SerializeToElement(altered)));
        Assert.Equal(previous.GetRawText(), server.AgentCard.GetRawText());
    }
    [Fact]
    public async Task AllStandardSecuritySchemesExtensionsAndSignatureMetadataRemainDetached()
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        var altered = JsonNode.Parse(server.AgentCard.GetRawText())!;
        altered["securitySchemes"] = JsonNode.Parse("""{"key":{"type":"apiKey","in":"header","name":"X-Key"},"http":{"type":"http","scheme":"Bearer","bearerFormat":"JWT"},"oauth":{"type":"oauth2","flows":{"authorizationCode":{"authorizationUrl":"https://auth.example/authorize","tokenUrl":"https://auth.example/token","scopes":{"read":"Read"}},"implicit":{"authorizationUrl":"https://auth.example/authorize","scopes":{}},"password":{"tokenUrl":"https://auth.example/token","scopes":{}},"clientCredentials":{"tokenUrl":"https://auth.example/token","scopes":{}}}},"oidc":{"type":"openIdConnect","openIdConnectUrl":"https://auth.example/.well-known/openid-configuration"},"tls":{"type":"mutualTLS"}}""");
        altered["security"] = JsonNode.Parse("""[{"oauth":["read"]},{"key":[],"tls":[]}]""");
        altered["capabilities"]!["extensions"] = JsonNode.Parse("""[{"uri":"urn:example:extension","required":false,"params":{"opaque":9223372036854775807}}]""");
        altered["signatures"] = JsonNode.Parse("""[{"protected":"opaque-header","signature":"opaque-signature","header":{"key":"value"}}]""");
        using (var document = JsonDocument.Parse(altered.ToJsonString())) server.UpdateAgentCard(document.RootElement);
        var card = server.AgentCard;
        Assert.Equal(5, card.GetProperty("securitySchemes").EnumerateObject().Count());
        Assert.Equal(long.MaxValue, card.GetProperty("capabilities").GetProperty("extensions")[0].GetProperty("params").GetProperty("opaque").GetInt64());
        Assert.Equal("opaque-signature", card.GetProperty("signatures")[0].GetProperty("signature").GetString());
    }
}
