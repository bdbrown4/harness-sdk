using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpAuthenticationTests
{
    [Fact]
    public async Task RealChallengeDiscoversTheIssuerAndReplaysTheExactRequestWithClientCredentials()
    {
        await using var server = new OAuthLoopbackServer();
        var expectedBody = Encoding.UTF8.GetBytes("{\"value\":\"Café 雪\"}");
        using var http = new HttpClient(new McpClientCredentialsHandler(server.Resource,
            new() { ClientId = "client:雪", ClientSecret = "secret&+=é", Scopes = ["configured"] }));
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Resource) { Content = new ByteArrayContent(expectedBody) };
        request.Headers.TryAddWithoutValidation("Mcp-Name", "opaque-route");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = server.Requests.ToArray();
        var resourceCalls = requests.Where(value => value.Path == "/mcp").ToArray();
        Assert.Equal(2, resourceCalls.Length);
        Assert.All(resourceCalls, value => Assert.Equal(expectedBody, value.Body));
        Assert.All(resourceCalls, value => Assert.Equal("opaque-route", value.Headers["Mcp-Name"]));
        Assert.False(resourceCalls[0].Headers.ContainsKey("Authorization"));
        Assert.Equal("Bearer access-1", resourceCalls[1].Headers["Authorization"]);
        var token = Assert.Single(requests, value => value.Path == "/token");
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString("client:雪") + ":" + Uri.EscapeDataString("secret&+=é"))), token.Headers["Authorization"]);
        var form = Encoding.UTF8.GetString(token.Body);
        Assert.Contains("grant_type=client_credentials", form);
        Assert.Contains("scope=required", form);
        Assert.DoesNotContain("configured", form);
        Assert.DoesNotContain("secret", form);
        Assert.Contains("resource=", form);
        Assert.Single(requests, value => value.Path == "/.well-known/oauth-protected-resource/mcp");
        Assert.Single(requests, value => value.Path == "/.well-known/oauth-authorization-server/issuer");
        Assert.DoesNotContain(requests, value => value.Path.Contains("authorize"));
    }

    [Fact]
    public async Task DiscoveryRejectsADifferentIssuerWithMultipleTrailingPathSeparatorsBeforeSendingCredentials()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/.well-known/oauth-authorization-server/issuer"
            ? OAuthLoopbackServer.Json(new { issuer = new Uri(server.Origin, "issuer///").AbsoluteUri, token_endpoint = new Uri(server.Origin, "token").AbsoluteUri }) : null);
        using var http = new HttpClient(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));

        await Assert.ThrowsAsync<InvalidDataException>(() => http.GetAsync(server.Resource));

        Assert.DoesNotContain(server.Requests, value => value.Path == "/token");
    }

    [Fact]
    public async Task InsufficientScopeRetainsPreviouslyRequestedPermissionsDuringTheNewGrant()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/mcp" && request.Headers.ContainsKey("X-StepUp") && request.Headers["Authorization"] == "Bearer access-1"
            ? new OAuthResponse(403, "{}", new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer error=\"insufficient_scope\", scope=\"extra\"" }) : null);
        using var http = new HttpClient(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));
        using var first = await http.GetAsync(server.Resource);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        request.Headers.Add("X-StepUp", "yes");

        using var second = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var grants = server.Requests.Where(value => value.Path == "/token").ToArray();
        Assert.Equal(2, grants.Length);
        Assert.Contains("scope=required+extra", Encoding.UTF8.GetString(grants[1].Body));
    }

    [Fact]
    public async Task UnauthorizedWithoutAChallengeUsesWellKnownDiscovery()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/mcp" && !request.Headers.ContainsKey("Authorization") ? new(401, "{}") : null);
        using var http = Client(server);

        using var response = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(server.Requests, value => value.Path == "/.well-known/oauth-protected-resource/mcp");
        Assert.Single(server.Requests, value => value.Path == "/token");
    }

    [Fact]
    public async Task OrdinaryForbiddenResponseIsReturnedWithoutStartingAuthentication()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/mcp" ? new(403, "ordinary denial") : null);
        using var http = Client(server);

        using var response = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ordinary denial", await response.Content.ReadAsStringAsync());
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task ReactiveCachingAndExplicitExpiryRefreshHaveDistinctBehavior(bool refreshOnExpiry, int expectedGrants)
    {
        await using var server = new OAuthLoopbackServer();
        var issued = 0;
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/token"
            ? OAuthLoopbackServer.Json(new { access_token = "access-" + Interlocked.Increment(ref issued), token_type = "Bearer", expires_in = 0 }) : null);
        using var http = Client(server, new() { RefreshOnExpiry = refreshOnExpiry });
        using var first = await http.GetAsync(server.Resource);

        using var second = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(expectedGrants, server.Requests.Count(value => value.Path == "/token"));
    }

    [Fact]
    public async Task ConcurrentUnauthorizedRequestsShareOneSuccessfulGrant()
    {
        await using var server = new OAuthLoopbackServer();
        var granting = Signal();
        var challenged = Signal();
        var release = Signal();
        var initialCalls = 0;
        server.Respond = async request =>
        {
            if (request.Path == "/mcp" && !request.Headers.ContainsKey("Authorization") && Interlocked.Increment(ref initialCalls) == 4) challenged.TrySetResult(true);
            if (request.Path != "/token") return null;
            granting.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return null;
        };
        using var http = Client(server);
        var calls = new List<Task<HttpResponseMessage>> { http.GetAsync(server.Resource) };
        try
        {
            await Within(granting.Task);
            calls.AddRange(Enumerable.Range(0, 3).Select(_ => http.GetAsync(server.Resource)));
            await Within(challenged.Task);
            Assert.Single(server.Requests, value => value.Path == "/token");
        }
        finally { release.TrySetResult(true); }

        foreach (var response in await Within(Task.WhenAll(calls)))
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Single(server.Requests, value => value.Path == "/token");
    }

    [Fact]
    public async Task CancellingAQueuedFollowerDoesNotCancelTheGrantOrPoisonTheCache()
    {
        await using var server = new OAuthLoopbackServer();
        var granting = Signal();
        var followerArrived = Signal();
        var release = Signal();
        server.Respond = async request =>
        {
            if (request.Headers.ContainsKey("X-Follower")) followerArrived.TrySetResult(true);
            if (request.Path != "/token") return null;
            granting.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return null;
        };
        using var invoker = new HttpMessageInvoker(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        var leader = invoker.SendAsync(firstRequest, default);
        try
        {
            await Within(granting.Task);
            using var cancellation = new CancellationTokenSource();
            using var followerRequest = new HttpRequestMessage(HttpMethod.Get, server.Resource);
            followerRequest.Headers.Add("X-Follower", "yes");
            var follower = invoker.SendAsync(followerRequest, cancellation.Token);
            await Within(followerArrived.Task);

            cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(follower));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(leader.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        using var accepted = await Within(leader);
        using var warmRequest = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        using var warm = await invoker.SendAsync(warmRequest, default);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Single(server.Requests, value => value.Path == "/token");
    }

    [Fact]
    public async Task ACancelledLeaderCannotPublishItsLateTokenAndTheNextCallRecovers()
    {
        await using var server = new OAuthLoopbackServer();
        var entered = Signal();
        var release = Signal();
        var grants = 0;
        server.Respond = async request =>
        {
            if (request.Path != "/token") return null;
            if (Interlocked.Increment(ref grants) != 1) return OAuthLoopbackServer.Json(new { access_token = "access-good", token_type = "Bearer" });
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return OAuthLoopbackServer.Json(new { access_token = "access-cancelled", token_type = "Bearer" });
        };
        using var invoker = new HttpMessageInvoker(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));
        using var cancellation = new CancellationTokenSource();
        using var initial = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        var pending = invoker.SendAsync(initial, cancellation.Token);
        try
        {
            await Within(entered.Task);
            cancellation.Cancel();
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(pending))).CancellationToken);
            using var nextRequest = new HttpRequestMessage(HttpMethod.Get, server.Resource);
            using var next = await Within(invoker.SendAsync(nextRequest, default));
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
        finally { release.TrySetResult(true); }
        using var warmRequest = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        using var warm = await invoker.SendAsync(warmRequest, default);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.DoesNotContain(server.Requests, value => value.Headers.TryGetValue("Authorization", out var authorization) && authorization == "Bearer access-cancelled");
        Assert.Equal(2, grants);
    }

    [Fact]
    public async Task ARejectedTokenCausesAtMostOneResourceReplay()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/mcp" ? new(401, "denied", new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" }) : null);
        using var http = Client(server);

        using var response = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, server.Requests.Count(value => value.Path == "/mcp"));
        Assert.Single(server.Requests, value => value.Path == "/token");
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/token")]
    public async Task ResourceDiscoveryAndTokenRedirectsNeverForwardCredentials(string redirectPath)
    {
        await using var server = new OAuthLoopbackServer();
        await using var destination = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == redirectPath
            ? new(307, "redirect", new Dictionary<string, string> { ["Location"] = destination.Resource.AbsoluteUri }) : null);
        using var http = Client(server);

        if (redirectPath == "/mcp")
        {
            using var response = await http.GetAsync(server.Resource);
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        }
        else await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.Resource));

        Assert.Empty(destination.Requests);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("mcp?unexpected=yes")]
    [InlineData("mcp#fragment")]
    public async Task WarmCredentialsCannotBeSentToADifferentResourceIdentity(string relative)
    {
        await using var server = new OAuthLoopbackServer();
        using var http = Client(server);
        using var first = await http.GetAsync(server.Resource);
        var count = server.Requests.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync(new Uri(server.Origin, relative)));

        Assert.Equal(count, server.Requests.Count);
    }

    [Fact]
    public async Task DiscoveryValidatesUnselectedScopeMetadataBeforeSendingCredentials()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/.well-known/oauth-protected-resource/mcp"
            ? OAuthLoopbackServer.Json(new { resource = server.Resource.AbsoluteUri, authorization_servers = new[] { new Uri(server.Origin, "issuer").AbsoluteUri }, scopes_supported = new[] { 42 } }) : null);
        using var http = Client(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => http.GetAsync(server.Resource));

        Assert.DoesNotContain(server.Requests, value => value.Path == "/token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyAuthorizationServersFallsBackToTheResourceOrigin(bool empty)
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path switch
        {
            "/.well-known/oauth-protected-resource/mcp" => OAuthLoopbackServer.Json(empty
                ? new Dictionary<string, object> { ["resource"] = server.Resource.AbsoluteUri, ["authorization_servers"] = Array.Empty<string>() }
                : new Dictionary<string, object> { ["resource"] = server.Resource.AbsoluteUri }),
            "/.well-known/oauth-authorization-server" => OAuthLoopbackServer.Json(new { issuer = server.Origin.AbsoluteUri, token_endpoint = new Uri(server.Origin, "token").AbsoluteUri }),
            _ => null,
        });
        using var http = Client(server);

        using var response = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(server.Requests, value => value.Path == "/token");
    }

    [Theory]
    [InlineData(true, "metadata")]
    [InlineData(false, "configured")]
    public async Task EmptyRequiredScopesFallThroughTheSourcePriorityOrder(bool emptyChallenge, string expected)
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path switch
        {
            "/mcp" when !request.Headers.ContainsKey("Authorization") => new(401, "{}", new Dictionary<string, string>
            { ["WWW-Authenticate"] = "Bearer resource_metadata=\"" + new Uri(server.Origin, ".well-known/oauth-protected-resource/mcp") + "\"" + (emptyChallenge ? ", scope=\"\"" : "") }),
            "/.well-known/oauth-protected-resource/mcp" => OAuthLoopbackServer.Json(new
            {
                resource = server.Resource.AbsoluteUri,
                authorization_servers = new[] { new Uri(server.Origin, "issuer").AbsoluteUri },
                scopes_supported = emptyChallenge ? new[] { "metadata" } : Array.Empty<string>(),
            }),
            _ => null,
        });
        using var http = new HttpClient(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "id", ClientSecret = "secret", Scopes = ["configured"] }));

        using var response = await http.GetAsync(server.Resource);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scope=" + expected, Encoding.UTF8.GetString(Assert.Single(server.Requests, value => value.Path == "/token").Body));
    }

    [Theory]
    [InlineData("{\"access_token\":\"bad token\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"access-1\",\"token_type\":\"MAC\"}")]
    [InlineData("{\"access_token\":\"access-1\",\"token_type\":\"Bearer\",\"expires_in\":-1}")]
    [InlineData("{\"access_token\":\"access-1\",\"token_type\":\"Bearer\",\"expires_in\":1.5}")]
    [InlineData("{\"access_token\":\"access-1\",\"token_type\":\"Bearer\",\"scope\":[]}")]
    [InlineData("{\"token_type\":\"Bearer\"}")]
    [InlineData("null")]
    public async Task MalformedTokensAreRejectedBeforeAnyAuthenticatedReplay(string body)
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == "/token" ? new(200, body) : null);
        using var http = Client(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => http.GetAsync(server.Resource));

        Assert.DoesNotContain(server.Requests, value => value.Path == "/mcp" && value.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataAndTokenByteLimitsApplyToDeclaredAndChunkedBodies(bool token, bool chunked)
    {
        await using var server = new OAuthLoopbackServer();
        var endpoint = token ? "/token" : "/.well-known/oauth-protected-resource/mcp";
        server.Respond = request => Task.FromResult<OAuthResponse?>(request.Path == endpoint ? new(200, new string('x', 1024), chunked: chunked) : null);
        using var http = Client(server, token ? new() { MaximumTokenBytes = 32 } : new() { MaximumMetadataBytes = 32 });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => http.GetAsync(server.Resource));

        Assert.Contains("byte limit", error.Message);
        Assert.DoesNotContain(server.Requests, value => value.Path == "/mcp" && value.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerReplayLimitsApplyBeforeIoAndDoNotDisposeBorrowedContent(bool declaredLength)
    {
        await using var server = new OAuthLoopbackServer();
        // Framework HttpClient independently disposes outgoing content; the handler's borrowed-content contract is tested at its own boundary.
        using var http = new HttpMessageInvoker(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }, new() { MaximumRequestBodyBytes = 16 }));
        var content = new ObservableContent(new byte[17], declaredLength);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Resource) { Content = content };

        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(request, default));

        Assert.Empty(server.Requests);
        Assert.False(content.Disposed);
    }

    [Fact]
    public async Task CancellationStopsAnUncooperativeBodyCopyWithoutDisposingBorrowedContent()
    {
        await using var server = new OAuthLoopbackServer();
        using var handler = new HttpMessageInvoker(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));
        var entered = Signal();
        var release = Signal();
        var completed = Signal();
        var content = new ObservableContent([], false, async stream =>
        {
            try
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                await stream.WriteAsync(new byte[1], 0, 1).ConfigureAwait(false);
            }
            finally { completed.TrySetResult(true); }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Resource) { Content = content };
        using var cancellation = new CancellationTokenSource();
        var pending = handler.SendAsync(request, cancellation.Token);
        try
        {
            await Within(entered.Task);
            cancellation.Cancel();
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(pending))).CancellationToken);
            Assert.False(content.Disposed);
            Assert.Empty(server.Requests);
        }
        finally { release.TrySetResult(true); await Within(completed.Task); }
    }

    [Theory]
    [InlineData("http://example.test/token", false)]
    [InlineData("https://example.test/token", true)]
    [InlineData("http://127.0.0.1/token", true)]
    [InlineData("http://[::1]/token", true)]
    [InlineData("http://app.localhost/token", true)]
    public void TokenCredentialsRequireTlsExceptLoopbackDevelopmentEndpoints(string uri, bool allowed)
    {
        var failure = Record.Exception(() => McpClientCredentialsHandler.ValidateTokenEndpoint(new(uri)));
        if (allowed) Assert.Null(failure);
        else Assert.IsType<InvalidDataException>(failure);
    }

    [Fact]
    public async Task HandlerDisposalCancelsAnInFlightGrantAndRefusesNewCalls()
    {
        await using var server = new OAuthLoopbackServer();
        var entered = Signal();
        var release = Signal();
        server.Respond = async request =>
        {
            if (request.Path != "/token") return null;
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return null;
        };
        using var handler = new HttpMessageInvoker(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }));
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Resource);
        var pending = handler.SendAsync(request, default);
        try
        {
            await Within(entered.Task);
            handler.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(pending));
            using var next = new HttpRequestMessage(HttpMethod.Get, server.Resource);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.SendAsync(next, default));
        }
        finally { release.TrySetResult(true); }
    }

    private sealed class ObservableContent(byte[] bytes, bool declared, Func<Stream, Task>? write = null) : HttpContent
    {
        internal bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return declared; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write is null ? stream.WriteAsync(bytes, 0, bytes.Length) : write(stream);
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000)));
        return await task;
    }

    private static HttpClient Client(OAuthLoopbackServer server, McpClientCredentialsHandlerOptions? options = null) =>
        new(new McpClientCredentialsHandler(server.Resource, new() { ClientId = "client", ClientSecret = "secret" }, options));
}
