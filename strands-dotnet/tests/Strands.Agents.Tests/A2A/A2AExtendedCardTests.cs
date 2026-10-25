using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AExtendedCardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtendedCardRequestsRequireExplicitAuthorizationAndAcceptTheProtocolWithoutParams(bool authorized)
    {
        var calls = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("Metadata only.") });
        await using var template = new A2AServer(executor, A2AServerTests.Options());
        var extended = JsonNode.Parse(template.AgentCard.GetRawText())!; extended["name"] = "Private skills";
        await using var server = new A2AServer(executor, A2AServerTests.Options() with
        {
            AuthenticatedExtendedCard = JsonSerializer.SerializeToElement(extended),
            AuthorizeExtendedCard = (request, _) => { calls++; return new(request.Headers.Authorization?.Parameter == "allowed"); }
        });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authorized ? "allowed" : "denied");
        var client = new A2AClient(http, new(new Uri("http://local/")));
        if (authorized)
        {
            var card = await client.GetAuthenticatedExtendedCardAsync();
            Assert.Equal("Private skills", card.GetProperty("name").GetString());
            Assert.Equal("Private skills", (await client.GetAgentCardAsync()).GetProperty("name").GetString());
        }
        else
        {
            var error = await Assert.ThrowsAsync<A2AHttpException>(() => client.GetAuthenticatedExtendedCardAsync());
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Equal(A2AServerTests.Options().Name, (await client.GetAgentCardAsync()).GetProperty("name").GetString());
        }
        Assert.Equal(1, calls); Assert.True(server.AgentCard.GetProperty("supportsAuthenticatedExtendedCard").GetBoolean());
        Assert.Equal(A2AServerTests.Options().Name, server.AgentCard.GetProperty("name").GetString());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHttpHandlerCarriesAuthorizationAcrossDiscoveryAndTheExtendedCardRpc(bool authorized)
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        await using var wire = new A2AServerTcpFixture(root =>
        {
            var options = A2AExtendedCardLifetimeTests.Options((request, _) => new(request.Headers.Authorization?.Parameter == "allowed"));
            var endpoint = new Uri(root, "rpc"); var card = JsonNode.Parse(options.AuthenticatedExtendedCard!.Value.GetRawText())!; card["url"] = endpoint.AbsoluteUri;
            return new A2AServer(executor, options with { Url = endpoint, AuthenticatedExtendedCard = JsonSerializer.SerializeToElement(card) });
        });
        using var handler = new HttpClientHandler { UseProxy = false }; using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authorized ? "allowed" : "denied");
        var client = new A2AClient(http, new(wire.BaseUri));
        if (authorized)
        {
            var card = await client.GetAuthenticatedExtendedCardAsync();
            Assert.Equal("Private", card.GetProperty("name").GetString()); Assert.Equal(new Uri(wire.BaseUri, "rpc").AbsoluteUri, card.GetProperty("url").GetString());
        }
        else Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<A2AHttpException>(() => client.GetAuthenticatedExtendedCardAsync())).StatusCode);
    }
    [Fact]
    public async Task AProtocolMethodWithoutParametersStillReportsMethodNotFoundWhenItIsUnknown()
    {
        await using var executor = A2AExtendedCardLifetimeTests.Executor();
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new Uri("http://local/"))).CallAsync("agent/unknown"));
        Assert.Equal(-32601, error.Code);
    }
}
