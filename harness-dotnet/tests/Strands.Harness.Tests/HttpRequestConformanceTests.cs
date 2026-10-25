using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class HttpRequestConformanceTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task EveryAdvertisedMethodMakesAGenuineHttpRequest(string method)
    {
        await using var server = new HttpRequestTests.RawServer();
        using var result = JsonDocument.Parse(await HttpRequestTests.InvokeAsync(HttpRequest.Create(), new { method, url = server.Endpoint }));
        var request = await server.Request;
        Assert.StartsWith(method + " /raw HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Equal(method == "HEAD" ? "" : "actual response", result.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(HttpRequestBehavior.TypeScript, false)]
    [InlineData(HttpRequestBehavior.Python, false)]
    [InlineData(HttpRequestBehavior.Python, true)]
    public async Task EmptyAndPythonNullBodiesHaveTheDocumentedContentType(HttpRequestBehavior behavior, bool nullBody)
    {
        string? body = null, contentType = null;
        using var handler = new HttpTestHandler(async (request, _) =>
        {
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            contentType = request.Content?.Headers.ContentType?.ToString();
            return new(HttpStatusCode.NoContent) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        });
        using var client = new HttpClient(handler);
        using var result = JsonDocument.Parse(await HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = behavior }), new { method = "POST", url = "http://fixture.test/", body = nullBody ? null : "" }));
        Assert.Equal(nullBody ? null : "", body);
        Assert.Equal(behavior == HttpRequestBehavior.TypeScript ? "text/plain; charset=UTF-8" : null, contentType);
        Assert.Equal("", result.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task PythonNullOptionalTimeoutMeansUnspecified()
    {
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null accepted") }))) { Timeout = Timeout.InfiniteTimeSpan };
        using var result = JsonDocument.Parse(await HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python }), new { method = "GET", url = "http://fixture.test/", headers = (object?)null, body = (object?)null, timeout = (object?)null }));
        Assert.Equal("null accepted", result.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task PythonNullTimeoutPassesTheActualRegisteredToolSchema()
    {
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestSupport.Ok("registered null")))) { Timeout = Timeout.InfiniteTimeSpan };
        var tool = HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python });
        await using var agent = new Agent(new() { Model = new HttpTestSupport.EmptyModel(), Tools = [tool] });
        var input = JsonSerializer.SerializeToElement(new { method = "GET", url = "http://fixture.test/", timeout = (object?)null });
        var result = await agent.InvokeToolAsync(new("registered-null", tool.Spec.Name, input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal("registered null", json.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(HttpRequestBehavior.TypeScript, "true")]
    [InlineData(HttpRequestBehavior.Python, "true")]
    [InlineData(HttpRequestBehavior.Python, "false")]
    [InlineData(HttpRequestBehavior.Python, "\"2\"")]
    public async Task NativeNumericContractDoesNotClaimPydanticLaxCoercion(HttpRequestBehavior behavior, string value)
    {
        using var handler = new HttpTestHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => HttpTestSupport.InvokeJsonAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = behavior }), "{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"timeout\":" + value + "}"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"method\":\"TRACE\",\"url\":\"http://fixture.test/\"}")]
    [InlineData("{\"method\":\"get\",\"url\":\"http://fixture.test/\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"/relative\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"file:///private\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://user:secret@fixture.test/\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":{\"X-Test\":\"one\\r\\nInjected: two\"}}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":{\"Bad Name\":\"value\"}}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":{\"Content-Length\":\"0\"}}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":{\"Host\":\"other.test\"}}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":{\"X-Number\":1}}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"headers\":null}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"body\":null}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"body\":\"not permitted\"}")]
    [InlineData("{\"method\":\"HEAD\",\"url\":\"http://fixture.test/\",\"body\":\"\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"timeout\":0}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"timeout\":-1}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"timeout\":1e400}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"http://fixture.test/\",\"timeout\":null}")]
    public async Task MalformedDirectInputIsRejectedWithoutCallingTheClient(string json)
    {
        using var handler = new HttpTestHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => HttpTestSupport.InvokeJsonAsync(HttpRequest.Create(new() { HttpClient = client }), json));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpRequestBehavior.TypeScript, "text/plain; charset=UTF-8")]
    [InlineData(HttpRequestBehavior.Python, null)]
    public async Task Utf8RequestLimitCountsBytesAndRejectsBeforeSend(HttpRequestBehavior behavior, string? expectedType)
    {
        byte[]? sent = null; string? sentType = null;
        using var handler = new HttpTestHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsByteArrayAsync(); sentType = request.Content.Headers.ContentType?.ToString();
            return HttpTestSupport.Ok();
        });
        using var client = new HttpClient(handler);
        var config = new HttpRequestConfig { HttpClient = client, Behavior = behavior, MaximumRequestBodyBytes = 2 };
        _ = await HttpRequestTests.InvokeAsync(HttpRequest.Create(config), new { method = "POST", url = "http://fixture.test/", body = "é" });
        Assert.Equal(new byte[] { 0xc3, 0xa9 }, sent); Assert.Equal(expectedType, sentType);
        await Assert.ThrowsAsync<ArgumentException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(config with { MaximumRequestBodyBytes = 1 }), new { method = "POST", url = "http://fixture.test/", body = "é" }));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseLimitRejectsOverflowInsteadOfReturningTruncatedText(bool knownLength)
    {
        using var handler = new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(Encoding.UTF8.GetBytes("é")) : new HttpTestContent(Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("é"))))
        }));
        using var client = new HttpClient(handler);
        var config = new HttpRequestConfig { HttpClient = client, MaximumResponseBodyBytes = 2 };
        using var result = JsonDocument.Parse(await HttpRequestTests.InvokeAsync(HttpRequest.Create(config), new { method = "GET", url = "http://fixture.test/" }));
        Assert.Equal("é", result.RootElement.GetProperty("body").GetString());
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(config with { MaximumResponseBodyBytes = 1 }), new { method = "GET", url = "http://fixture.test/" }));
    }

    [Fact]
    public async Task RequestHeaderBoundIsCheckedBeforeSendAndResponseProjectionHasItsOwnBound()
    {
        var sentinel = new HttpRequestException("accepted request boundary");
        using var handler = new HttpTestHandler((_, _) => throw sentinel);
        using var client = new HttpClient(handler);
        var config = new HttpRequestConfig { HttpClient = client, MaximumHeaderBytes = 8 };
        var input = new { method = "GET", url = "http://fixture.test/", headers = new Dictionary<string, string> { ["X"] = "123" } };
        Assert.Same(sentinel, await Assert.ThrowsAsync<HttpRequestException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(config), input)));
        await Assert.ThrowsAsync<ArgumentException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(config with { MaximumHeaderBytes = 7 }), input));
        Assert.Equal(1, handler.Calls);
        using var responseClient = new HttpClient(new HttpTestHandler((_, _) =>
        {
            var response = HttpTestSupport.Ok(); response.Headers.Add("X-Large", new string('x', 100)); return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = responseClient, MaximumHeaderBytes = 32 }), new { method = "GET", url = "http://fixture.test/" }));
    }

    [Fact]
    public async Task BclAcceptsAResponseNamedHeaderAsACustomRequestHeader()
    {
        string? observed = null;
        using var handler = new HttpTestHandler((request, _) => { observed = Assert.Single(request.Headers.GetValues("Server")); return Task.FromResult(HttpTestSupport.Ok()); });
        using var client = new HttpClient(handler);
        _ = await HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), new { method = "POST", url = "http://fixture.test/", body = "already allocated", headers = new Dictionary<string, string> { ["Server"] = "custom request value" } });
        Assert.Equal("custom request value", observed); Assert.Equal(1, handler.Calls);
        // Earlier setup-failure characterization incorrectly assumed this header
        // would be rejected. It does not demonstrate the static cleanup issue.
    }

    [Fact]
    public async Task RequestDetachesDisposedJsonBeforeItsFirstTransportAwaitAndBorrowedClientRemainsUsable()
    {
        var entered = HttpTestSupport.Gate<bool>(); var release = HttpTestSupport.Gate<bool>();
        string? observed = null;
        using var handler = new HttpTestHandler(async (request, _) =>
        {
            entered.TrySetResult(true); await release.Task;
            observed = await request.Content!.ReadAsStringAsync();
            return HttpTestSupport.Ok();
        });
        using var client = new HttpClient(handler);
        var original = new HttpRequestConfig { HttpClient = client, Name = "custom_http" };
        var tool = HttpRequest.Create(original);
        var changedCopy = original with { Name = "other", MaximumRequestBodyBytes = 1 };
        Assert.Equal("custom_http", tool.Spec.Name); Assert.Equal("other", changedCopy.Name);
        using var document = JsonDocument.Parse("{\"method\":\"POST\",\"url\":\"http://fixture.test/\",\"body\":\"detached\"}");
        var operation = HttpTestSupport.InvokeElementAsync(tool, document.RootElement);
        try { await HttpTestSupport.Wait(entered.Task); document.Dispose(); }
        finally { release.TrySetResult(true); await operation; }
        Assert.Equal("detached", observed);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://fixture.test/reuse") { Content = new StringContent("still usable") };
        using var response = await client.SendAsync(request);
        Assert.Equal("still usable", observed);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpRequestBehavior.TypeScript)]
    [InlineData(HttpRequestBehavior.Python)]
    public async Task ContentTypeOverrideAndUnknownCharsetFallbackAreExplicit(HttpRequestBehavior behavior)
    {
        string? sentType = null;
        using var handler = new HttpTestHandler((request, _) =>
        {
            sentType = request.Content!.Headers.ContentType?.ToString();
            var response = HttpTestSupport.Ok("é");
            response.Content.Headers.ContentType!.CharSet = "unknown-strands-charset";
            response.Headers.TryAddWithoutValidation("X-Repeat", new[] { "one", "two" });
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var result = JsonDocument.Parse(await HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = behavior }), new { method = "POST", url = "http://fixture.test/", body = "{}", headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" } }));
        Assert.Equal("application/json", sentType);
        Assert.Equal("é", result.RootElement.GetProperty("body").GetString());
        Assert.Equal(behavior == HttpRequestBehavior.Python ? "one\ntwo" : "one, two", result.RootElement.GetProperty("headers").GetProperty("x-repeat").GetString());
    }
}

internal sealed class HttpTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
}

internal sealed class HttpTestContent(Task<Stream> opened, Exception? cleanup = null) : HttpContent
{
    internal int Disposals;
    internal TaskCompletionSource<bool> Entered { get; } = HttpTestSupport.Gate<bool>();
    protected override Task<Stream> CreateContentReadStreamAsync() { Entered.TrySetResult(true); return opened; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); base.Dispose(true); if (cleanup is not null) throw cleanup; } else base.Dispose(false); }
}

internal static class HttpTestSupport
{
    internal static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static HttpResponseMessage Ok(string body = "ok") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    internal static async Task<string> InvokeJsonAsync(ITool tool, string json, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        return await InvokeElementAsync(tool, document.RootElement, cancellationToken);
    }
    internal static async Task<string> InvokeElementAsync(ITool tool, JsonElement input, CancellationToken cancellationToken = default)
    {
        await using var agent = new Agent(new() { Model = new EmptyModel() });
        return await tool.InvokeAsync(input, new(agent, new("test-http", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    internal static async Task Wait(Task operation, int seconds = 10)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(seconds)))); await operation;
    }
    internal sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent(); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
