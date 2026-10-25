using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Models;
using Xunit;
using Xunit.Abstractions;

namespace Strands.Agents.Tests.Models;

public sealed class AnthropicNativeTokenCountMinimumTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExplicitNativeCountUsesTheProviderCountEndpointAndReturnsItsCount()
    {
        using var handler = new CountReply();
        using var client = new HttpClient(handler);
        var options = new AnthropicModelOptions("claude-test", "count-key")
        {
            Endpoint = new Uri("https://count.invalid/v1/messages"),
            MaxTokens = 1234,
            Temperature = 0.2,
            Params = Json("{\"metadata\":{\"user_id\":\"not-a-count-field\"}}")
        };
        RequestProspectiveNativeCountOption(options);
        IModel model = new AnthropicModel(client, options);
        var request = new ModelRequest([Message.User("hello")], Tools: [new("lookup", "Find a record", Json("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}"))])
        {
            SystemPromptContent = [new TextBlock("typed-system")],
            ToolChoice = new(ToolChoiceMode.Tool, "lookup")
        };
        var count = await model.CountTokensAsync(request);

        var captured = Assert.Single(handler.CountRequests);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("https://count.invalid/v1/messages/count_tokens", captured.Uri);
        Assert.Equal("count-key", captured.ApiKey); Assert.Equal("2023-06-01", captured.Version);
        Assert.Equal(new[] { "messages", "model", "system", "tool_choice", "tools" }, captured.Body.EnumerateObject().Select(p => p.Name).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal("claude-test", captured.Body.GetProperty("model").GetString());
        Assert.Equal("hello", captured.Body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("typed-system", captured.Body.GetProperty("system").GetString());
        Assert.Equal("lookup", captured.Body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", captured.Body.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("tool", captured.Body.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("lookup", captured.Body.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal(913L, count);
        using var borrowedProbe = await client.GetAsync("https://count.invalid/borrowed-client-probe");
        Assert.Equal(HttpStatusCode.OK, borrowedProbe.StatusCode); Assert.Equal(1, handler.BorrowedProbes);
    }

    [Fact]
    public async Task DefaultCountUsesTheExistingHeuristicWithoutHttp()
    {
        using var handler = new CountReply();
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, new("claude-test", "count-key") { Endpoint = new Uri("https://count.invalid/v1/messages") });
        var count = await model.CountTokensAsync(new ModelRequest([Message.User("abcd")]));
        Assert.Equal(1L, count); Assert.Empty(handler.CountRequests); Assert.Equal(0, handler.BorrowedProbes);
    }

    private void RequestProspectiveNativeCountOption(AnthropicModelOptions options)
    {
        var property = typeof(AnthropicModelOptions).GetProperty("UseNativeTokenCount");
        output.WriteLine(JsonSerializer.Serialize(new { RequestedNativeTokenCount = true, ProspectiveOptionPresent = property is not null }));
        if (property is null) return;
        Assert.Equal(typeof(bool), property.PropertyType);
        property.SetValue(options, true);
        Assert.True(Assert.IsType<bool>(property.GetValue(options)));
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private sealed record Captured(HttpMethod Method, string Uri, string ApiKey, string Version, JsonElement Body);
    private sealed class CountReply : HttpMessageHandler
    {
        public List<Captured> CountRequests { get; } = [];
        public int BorrowedProbes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/borrowed-client-probe")
            {
                BorrowedProbes++; return new(HttpStatusCode.OK);
            }
            var body = Json(await request.Content!.ReadAsStringAsync());
            CountRequests.Add(new(request.Method, request.RequestUri.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(",", keys) : "",
                request.Headers.TryGetValues("anthropic-version", out var versions) ? string.Join(",", versions) : "", body));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"input_tokens\":913}", Encoding.UTF8, "application/json") };
        }
    }
}
