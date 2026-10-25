using System.Net;
using System.Net.Http;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Strands.Agents;
using Strands.Agents.GoogleAuth;
using Strands.Agents.Models;
using Xunit;

namespace Strands.GoogleAuth.Tests;

public sealed class GoogleCredentialHttpClientGoogleModelTests
{
    private static readonly Uri Endpoint = new("https://example.invalid/v1/projects/synthetic-project/locations/synthetic-region/publishers/google/models/synthetic-model:streamGenerateContent?alt=sse");

    [Fact]
    public async Task VertexStreamingUsesOfficialHeadersAndBorrowsClient()
    {
        var factory = new Factory();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(Credential(), factory);
        var model = new GoogleModel(client, new("synthetic-model") { UseVertexAi = true, Endpoint = Endpoint });
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new([Message.User("question")]))) events.Add(item);
        Assert.Contains(events, item => item is ModelContentBlockDeltaEvent { Text: "ok" });
        var call = Assert.Single(factory.Handler!.Calls);
        Assert.Equal(Endpoint, call.Endpoint);
        AssertHeaders(call);
        Assert.False((object)model is IDisposable);
        Assert.Equal(0, factory.Handler.Disposed);
        // GoogleModel has no disposal API. Completion of its physical enumerator leaves the borrowed client usable.
        using var reuse = await client.GetAsync("https://example.invalid/reuse");
        Assert.Equal(2, factory.Handler.Calls.Count);
        AssertHeaders(factory.Handler.Calls[1]);
    }

    [Fact]
    public async Task NativeTokenCountUsesSameExplicitVertexResourceAndCredential()
    {
        var factory = new Factory();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(Credential(), factory);
        var model = new GoogleModel(client, new("synthetic-model") { UseVertexAi = true, Endpoint = Endpoint, UseNativeTokenCount = true });
        Assert.Equal(7, await model.CountTokensAsync(new([Message.User("question")])));
        var call = Assert.Single(factory.Handler!.Calls);
        Assert.Equal(Endpoint.GetLeftPart(UriPartial.Path).Replace(":streamGenerateContent", ":countTokens"), call.Endpoint.AbsoluteUri);
        AssertHeaders(call);
        Assert.Equal(0, factory.Handler.Disposed);
    }

    private static GoogleCredential Credential() => GoogleCredential.FromAccessToken("synthetic-static-token").CreateWithQuotaProject("synthetic-quota");
    private static void AssertHeaders(Call call)
    { Assert.Equal("Bearer synthetic-static-token", call.Authorization); Assert.Equal("synthetic-quota", call.Quota); Assert.Null(call.ApiKey); }
    private sealed record Call(Uri Endpoint, string? Authorization, string? Quota, string? ApiKey);
    private sealed class Factory : Google.Apis.Http.HttpClientFactory
    {
        internal Handler? Handler;
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => Handler = new Handler();
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];
        internal int Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("example.invalid", request.RequestUri!.Host);
            Calls.Add(new(request.RequestUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-goog-user-project", out var quotas) ? string.Join(",", quotas) : null,
                request.Headers.TryGetValues("x-goog-api-key", out var keys) ? string.Join(",", keys) : null));
            var stream = request.Headers.Accept.Any(header => header.MediaType == "text/event-stream");
            var body = stream ? "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n" : "{\"totalTokens\":7}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") });
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed++; base.Dispose(disposing); }
    }
}
