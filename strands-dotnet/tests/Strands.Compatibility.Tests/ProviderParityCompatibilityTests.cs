using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class ProviderParityCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedToolAndRichResultWireContractsWorkOnFrameworkAsset(bool anthropic)
    {
        using var handler = new Handler(anthropic);
        using var client = new HttpClient(handler);
        IModel model = anthropic
            ? new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Ttl = "5m" } })
            : new OpenAIModel(client, new("model", "key") { CacheConfig = new() { Ttl = "24h" } });
        var spec = new ToolSpec("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }));
        var result = new ToolResultBlock("call", [new TextBlock("found"), new ImageBlock("image/png", "aGVsbG8=")]);
        var request = new ModelRequest([new(Role.User, [result])], "system", [spec]) { ToolChoice = new(ToolChoiceMode.Tool, "lookup"), SessionId = "framework" };
        await foreach (var _ in model.StreamAsync(request)) { }
        var body = handler.Body!.Value;
        Assert.Equal(1, handler.Calls);
        Assert.Equal("lookup", anthropic ? body.GetProperty("tool_choice").GetProperty("name").GetString() : body.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        if (anthropic)
        {
            Assert.Equal("image", body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("5m", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        }
        else
        {
            Assert.Equal("tool", body.GetProperty("messages")[1].GetProperty("role").GetString());
            Assert.Equal("data:image/png;base64,aGVsbG8=", body.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("strands-framework", body.GetProperty("prompt_cache_key").GetString());
        }
    }

    private sealed class Handler(bool anthropic) : HttpMessageHandler
    {
        internal JsonElement? Body { get; private set; }
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Body = document.RootElement.Clone();
            const string openAI = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            const string claude = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[]}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(anthropic ? claude : openAI, Encoding.UTF8, "text/event-stream") };
        }
    }
}
