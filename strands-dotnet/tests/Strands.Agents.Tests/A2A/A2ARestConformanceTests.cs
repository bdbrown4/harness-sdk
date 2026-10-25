using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ARestConformanceTests
{
    [Fact]
    public async Task TaskRoutesEscapeOpaqueIdsAndNormalizeHistoryArtifactsAndSubscriptionDefaults()
    {
        const string id = "task/雪 ?";
        var calls = 0;
        var contents = new List<TrackedContent>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (++calls == 1) return Json(Card());
            Assert.Equal("https://agent.test/base/v1/tasks/" + Uri.EscapeDataString(id) + (calls == 2 ? "?tenant=one&historyLength=0" : calls == 3 ? ":cancel?tenant=one" : ":subscribe?tenant=one"), request.RequestUri!.AbsoluteUri);
            Assert.Equal(calls == 3 ? HttpMethod.Post : HttpMethod.Get, request.Method);
            if (calls == 3)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("tasks/" + id, body.RootElement.GetProperty("name").GetString());
            }
            else Assert.Null(request.Content);
            var bodyText = calls == 4
                ? "data: {\"statusUpdate\":{\"taskId\":\"task\",\"contextId\":\"context\",\"status\":{\"state\":2}}}\n\ndata: {\"statusUpdate\":{\"taskId\":\"task\",\"contextId\":\"context\",\"status\":{\"state\":3},\"final\":true}}\n\n"
                : """{"id":"task","contextId":"context","status":{"state":5,"message":{"messageId":"update","role":2,"content":[{"text":"stopped"}]}},"history":[{"messageId":"input","role":1,"content":[{"text":""}]}],"artifacts":[{"artifactId":"file","parts":[{"file":{"fileWithBytes":"AQI=","mimeType":"application/octet-stream"}}]}]}""";
            var content = new TrackedContent(bodyText, calls == 4 ? "text/event-stream" : "application/json");
            contents.Add(content);
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var client = new A2AClient(http, new(new("https://agent.test/")));
        var task = await client.CallAsync("tasks/get", JsonSerializer.SerializeToElement(new { id, historyLength = 0 }));
        Assert.Equal("task", task.GetProperty("kind").GetString());
        Assert.Equal("canceled", task.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("agent", task.GetProperty("status").GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("", task.GetProperty("history")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("AQI=", task.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("file").GetProperty("bytes").GetString());
        Assert.Equal("canceled", (await client.CallAsync("tasks/cancel", JsonSerializer.SerializeToElement(new { id }))).GetProperty("status").GetProperty("state").GetString());
        var updates = new List<JsonElement>();
        await foreach (var update in client.StreamCallAsync("tasks/resubscribe", JsonSerializer.SerializeToElement(new { id }))) updates.Add(update);
        Assert.Equal(2, updates.Count);
        Assert.False(updates[0].GetProperty("final").GetBoolean());
        Assert.True(updates[1].GetProperty("final").GetBoolean());
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilePartsAndPushRequestConfigurationUseTheProtobufJsonShape(bool bytes)
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(Card());
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            var file = body.GetProperty("message").GetProperty("content")[0].GetProperty("file");
            Assert.Equal(bytes ? "AQI=" : "https://files.test/雪", file.GetProperty(bytes ? "fileWithBytes" : "fileWithUri").GetString());
            Assert.Equal("application/octet-stream", file.GetProperty("mimeType").GetString());
            Assert.False(file.TryGetProperty("name", out _));
            Assert.False(file.TryGetProperty("custom", out _));
            Assert.False(body.GetProperty("message").GetProperty("content")[0].TryGetProperty("metadata", out _));
            var configuration = body.GetProperty("configuration");
            Assert.Equal("https://callback.test/", configuration.GetProperty("pushNotification").GetProperty("url").GetString());
            Assert.False(configuration.TryGetProperty("pushNotificationConfig", out _));
            return Json(JsonSerializer.Serialize(new { message = new { messageId = "file", role = "ROLE_AGENT", content = new[] { new { file } } } }));
        }));
        var nativeFile = new Dictionary<string, string> { [bytes ? "bytes" : "uri"] = bytes ? "AQI=" : "https://files.test/雪", ["mimeType"] = "application/octet-stream", ["name"] = "display-name", ["custom"] = "local" };
        var result = await new A2AClient(http, new(new("https://agent.test/"))).CallAsync("message/send", JsonSerializer.SerializeToElement(new
        {
            message = new { messageId = "file", role = "user", parts = new[] { new { kind = "file", file = nativeFile, metadata = new { local = true } } } },
            configuration = new { pushNotificationConfig = new { url = "https://callback.test/", token = "opaque" } }
        }));
        var returned = result.GetProperty("parts")[0];
        Assert.Equal("file", returned.GetProperty("kind").GetString());
        Assert.Equal(nativeFile[bytes ? "bytes" : "uri"], returned.GetProperty("file").GetProperty(bytes ? "bytes" : "uri").GetString());
    }

    [Theory]
    [InlineData("set")]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("delete")]
    public async Task PushConfigurationMethodsUseResourceRoutesAndNormalizeTheirResults(string operation)
    {
        const string taskId = "task/雪";
        const string configId = "config ?";
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (++calls == 1) return Json(Card());
            Assert.Equal(operation == "set" ? HttpMethod.Post : operation == "delete" ? HttpMethod.Delete : HttpMethod.Get, request.Method);
            var suffix = operation is "get" or "delete" ? "/" + Uri.EscapeDataString(configId) : "";
            Assert.Equal("https://agent.test/base/v1/tasks/" + Uri.EscapeDataString(taskId) + "/pushNotificationConfigs" + suffix + "?tenant=one", request.RequestUri!.AbsoluteUri);
            if (operation == "set")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("tasks/" + taskId + "/pushNotificationConfigs/" + configId, body.RootElement.GetProperty("name").GetString());
                Assert.Equal("https://callback.test/", body.RootElement.GetProperty("pushNotificationConfig").GetProperty("url").GetString());
                Assert.False(body.RootElement.TryGetProperty("taskId", out _));
            }
            else Assert.Null(request.Content);
            var value = new { name = "tasks/" + taskId + "/pushNotificationConfigs/" + configId, pushNotificationConfig = new { id = configId, url = "https://callback.test/", token = "opaque" } };
            return operation == "delete" ? new(HttpStatusCode.NoContent) : Json(JsonSerializer.Serialize(operation == "list" ? (object)new { configs = new[] { value } } : value));
        }));
        var parameters = operation == "set" ? JsonSerializer.SerializeToElement(new { taskId, pushNotificationConfig = new { id = configId, url = "https://callback.test/", token = "opaque" } })
            : JsonSerializer.SerializeToElement(new { id = taskId, pushNotificationConfigId = configId });
        var result = await new A2AClient(http, new(new("https://agent.test/"))).CallAsync("tasks/pushNotificationConfig/" + operation, parameters);
        if (operation == "delete") Assert.Equal(JsonValueKind.Object, result.ValueKind);
        else
        {
            var configuration = operation == "list" ? Assert.Single(result.EnumerateArray()) : result;
            Assert.Equal(taskId, configuration.GetProperty("taskId").GetString());
            Assert.Equal(configId, configuration.GetProperty("pushNotificationConfig").GetProperty("id").GetString());
            Assert.Equal("opaque", configuration.GetProperty("pushNotificationConfig").GetProperty("token").GetString());
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OmittedProtobufRepeatedFieldsNormalizeToEmptyCollections()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get ? Json(Card())
            : Json("""{"task":{"id":"task","contextId":"context","status":{"state":3,"message":{"messageId":"status","role":2}},"artifacts":[{"artifactId":"empty"}],"history":[{"messageId":"history","role":1}]}}"""))));
        var task = await new A2AClient(http, new(new("https://agent.test/"))).CallAsync("message/send", Parameters());
        Assert.Empty(task.GetProperty("status").GetProperty("message").GetProperty("parts").EnumerateArray());
        Assert.Empty(task.GetProperty("artifacts")[0].GetProperty("parts").EnumerateArray());
        Assert.Empty(task.GetProperty("history")[0].GetProperty("parts").EnumerateArray());
    }

    [Fact]
    public async Task NullProtobufTaskCollectionsNormalizeToEmptyCollections()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get ? Json(Card())
            : Json("""{"task":{"id":"task","contextId":"context","status":{"state":3},"artifacts":null,"history":null}}"""))));
        var task = await new A2AClient(http, new(new("https://agent.test/"))).CallAsync("message/send", Parameters());
        Assert.Empty(task.GetProperty("artifacts").EnumerateArray());
        Assert.Empty(task.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task ExtendedRestCardNormalizesSecurityMetadataAndRetainsTheSelectedBindingOverride()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Json(Card()));
            if (calls == 2)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://override.test/route/v1/card", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json("""{"name":"private","description":"authenticated","version":"1","protocolVersion":"0.3.0","url":"https://agent.test/private","preferredTransport":"HTTP+JSON","capabilities":{},"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"security":[{"schemes":{"oauth":{"list":["read"]}}}],"securitySchemes":{"oauth":{"oauth2SecurityScheme":{"flows":{"clientCredentials":{"tokenUrl":"https://identity.test/token","scopes":{"read":"Read"}}}}}},"skills":[{"id":"private","security":[{"schemes":{"oauth":{"list":["read"]}}}]}],"x-extension":{"retained":true}}"""));
            }
            Assert.Equal("https://override.test/route/v1/tasks/task", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"id":"task","contextId":"context","status":{"state":"TASK_STATE_COMPLETED"}}"""));
        }));
        var client = new A2AClient(http, new(new("https://agent.test/")) { Endpoint = new("https://override.test/route") });
        var card = await client.GetAuthenticatedExtendedCardAsync();
        Assert.Equal("read", card.GetProperty("security")[0].GetProperty("oauth")[0].GetString());
        Assert.Equal("oauth2", card.GetProperty("securitySchemes").GetProperty("oauth").GetProperty("type").GetString());
        Assert.Equal("https://identity.test/token", card.GetProperty("securitySchemes").GetProperty("oauth").GetProperty("flows").GetProperty("clientCredentials").GetProperty("tokenUrl").GetString());
        Assert.Equal("read", card.GetProperty("skills")[0].GetProperty("security")[0].GetProperty("oauth")[0].GetString());
        Assert.True(card.GetProperty("x-extension").GetProperty("retained").GetBoolean());
        Assert.Equal("completed", (await client.CallAsync("tasks/get", JsonSerializer.SerializeToElement(new { id = "task" }))).GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExtendedCardsNormalizeEverySecuritySchemeAndAnEmptySecurityRequirement()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(++calls == 1 ? Card()
            : """{"name":"private","description":"authenticated","version":"1","protocolVersion":"0.3.0","url":"https://agent.test/private","preferredTransport":"HTTP+JSON","capabilities":{},"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"security":[{}],"securitySchemes":{"key":{"apiKeySecurityScheme":{"name":"X-Key","location":"header"}},"bearer":{"httpAuthSecurityScheme":{"scheme":"Bearer","bearerFormat":"JWT"}},"oidc":{"openIdConnectSecurityScheme":{"openIdConnectUrl":"https://identity.test/.well-known/openid-configuration"}},"tls":{"mtlsSecurityScheme":{}}}}"""))));
        var card = await new A2AClient(http, new(new("https://agent.test/"))).GetAuthenticatedExtendedCardAsync();
        Assert.Empty(card.GetProperty("skills").EnumerateArray());
        Assert.Empty(card.GetProperty("security")[0].EnumerateObject());
        var schemes = card.GetProperty("securitySchemes");
        Assert.Equal("apiKey", schemes.GetProperty("key").GetProperty("type").GetString());
        Assert.Equal("header", schemes.GetProperty("key").GetProperty("in").GetString());
        Assert.Equal("http", schemes.GetProperty("bearer").GetProperty("type").GetString());
        Assert.Equal("JWT", schemes.GetProperty("bearer").GetProperty("bearerFormat").GetString());
        Assert.Equal("openIdConnect", schemes.GetProperty("oidc").GetProperty("type").GetString());
        Assert.Equal("mutualTLS", schemes.GetProperty("tls").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("text/plain", "denied")]
    [InlineData("application/json", "{\"message\":\"missing code\"}")]
    public async Task HttpFailuresWithoutAValidProtocolErrorRemainTypedAndDisposeTheBody(string mediaType, string body)
    {
        var content = new TrackedContent(body, mediaType);
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json(Card()) : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content })));
        var error = await Assert.ThrowsAsync<A2AHttpException>(() => new A2AClient(http, new(new("https://agent.test/"))).CallAsync("message/send", Parameters()));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("{\"message\":{\"role\":2,\"content\":[{\"text\":\"yes\"}]},\"task\":{}}")]
    [InlineData("{\"message\":{\"role\":2,\"content\":[{\"text\":\"yes\",\"data\":{\"data\":{}}}]}}")]
    [InlineData("{\"task\":{\"status\":{\"state\":99}}}")]
    public async Task InvalidResultUnionsAndEnumsFailWithAProtocolErrorAndDisposeTheBody(string body)
    {
        var content = new TrackedContent(body, "application/json");
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json(Card()) : new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await Assert.ThrowsAsync<A2AProtocolException>(() => new A2AClient(http, new(new("https://agent.test/"))).CallAsync("message/send", Parameters()));
        Assert.True(content.Disposed);
    }

    private static string Card() => """{"name":"REST","description":"fixture","version":"1","protocolVersion":"0.3.0","url":"https://agent.test/base?tenant=one","preferredTransport":"HTTP+JSON","capabilities":{"streaming":true},"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"skills":[]}""";
    private static JsonElement Parameters() => JsonSerializer.SerializeToElement(new { message = new { messageId = "request", role = "user", parts = new[] { new { kind = "text", text = "hello" } } } });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    private sealed class TrackedContent(string body, string mediaType) : StringContent(body, Encoding.UTF8, mediaType)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
