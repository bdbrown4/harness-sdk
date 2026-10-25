using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class WebFetchTests
{
    [Fact]
    public async Task IndependentDirectToolsCannotReceiveAnotherToolsSessionCookie()
    {
        var path = "/web-fetch-cookie-" + Guid.NewGuid().ToString("N") + "/";
        string? receivedCookie = null;
        await using var server = new WebTestServer(request =>
        {
            if (request.Target == path + "set")
                return new(Encoding.UTF8.GetBytes("session created")) { SetCookie = "web_fetch_session=first-tool; Path=" + path };
            receivedCookie = request.Headers.TryGetValue("Cookie", out var cookie) ? cookie : null;
            return new(Encoding.UTF8.GetBytes(receivedCookie is null ? "public page" : "private page for the first tool"));
        });
        var first = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        var independent = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        Assert.Equal("session created", await InvokeAsync(first, new Uri(server.Endpoint, path + "set").AbsoluteUri));
        Assert.Equal("public page", await InvokeAsync(independent, new Uri(server.Endpoint, path + "fresh").AbsoluteUri));
        Assert.Null(receivedCookie);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task BorrowedDirectClientsRetainTheirExplicitCookiePolicyAndOwnership()
    {
        var path = "/web-fetch-cookie-" + Guid.NewGuid().ToString("N") + "/";
        string? receivedCookie = null;
        await using var server = new WebTestServer(request =>
        {
            if (request.Target == path + "set")
                return new(Encoding.UTF8.GetBytes("session created")) { SetCookie = "web_fetch_session=caller-owned; Path=" + path };
            receivedCookie = request.Headers.TryGetValue("Cookie", out var cookie) ? cookie : null;
            return new(Encoding.UTF8.GetBytes(receivedCookie is null ? "public page" : "caller authenticated page"));
        });
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies };
        using var client = new HttpClient(handler);
        var first = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client });
        var second = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client });
        var fresh = new Uri(server.Endpoint, path + "fresh");
        Assert.Equal("session created", await InvokeAsync(first, new Uri(server.Endpoint, path + "set").AbsoluteUri));
        Assert.Equal("caller authenticated page", await InvokeAsync(second, fresh.AbsoluteUri));
        Assert.Equal("web_fetch_session=caller-owned", receivedCookie);
        Assert.True(handler.UseCookies);
        Assert.Same(cookies, handler.CookieContainer);
        Assert.Equal("caller-owned", cookies.GetCookies(fresh)["web_fetch_session"]?.Value);
        Assert.Equal("caller authenticated page", await client.GetStringAsync(new Uri(server.Endpoint, path + "probe")));
        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public async Task CancellationPreservesItsErrorWhenResponseDisposalThrows()
    {
        var stream = new CancellationStream();
        var cleanup = new IOException("content disposal failed");
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.OK) { Content = new ThrowingContent(stream, cleanup) }));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client });
        using var cancellation = new CancellationTokenSource();
        var operation = InvokeAsync(tool, "http://fixture.test/page", cancellationToken: cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var callbackFailure = Record.Exception(cancellation.Cancel);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(callbackFailure);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task DeadlineDisposalFailuresCannotEscapeTheTimerCallback()
    {
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.OK) { Content = new ThrowingContent(new CancellationStream(), new IOException("content disposal failed")) }));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client, Timeout = TimeSpan.FromMilliseconds(100) });
        Assert.Contains("gave up after 0.1s", await InvokeAsync(tool, "http://fixture.test/page").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(WebFetchTransport.Direct)]
    [InlineData(WebFetchTransport.Curl)]
    public async Task SummariesReceiveTheFinalRedirectUrl(WebFetchTransport transport)
    {
        await using var server = new WebTestServer(request => request.Target == "/start"
            ? new(Encoding.UTF8.GetBytes("intermediate redirect body"), Status: 302, Location: "/page") : new(Encoding.UTF8.GetBytes("final content")));
        var model = new RecordingModel();
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, model, new() { Transport = transport });
        Assert.Equal("summary", await InvokeAsync(tool, new Uri(server.Endpoint, "start").AbsoluteUri, "Explain"));
        Assert.Contains("Fetched URL: " + new Uri(server.Endpoint, "page").AbsoluteUri, Assert.Single(model.Requests).Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurlIncludeParsingSkipsNonemptyIntermediateRedirectBodies()
    {
        await using var server = new WebTestServer(request => request.Target == "/start"
            ? new(Encoding.UTF8.GetBytes("intermediate redirect body"), Status: 302, Location: "/page")
            : new(Encoding.UTF8.GetBytes("final content")));
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        Assert.Equal("final content", await InvokeAsync(tool, new Uri(server.Endpoint, "start").AbsoluteUri));
    }

    [Fact]
    public async Task CurlRetrievesMoreThanTheShellToolCaptureLimitWithoutLosingContent()
    {
        var html = "<script>" + new string('x', 2 * 1024 * 1024) + "</script><p>Full visible page</p>";
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes(html), "text/html"));
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        Assert.Equal("Full visible page", await InvokeAsync(tool, server.Endpoint.ToString()).WaitAsync(TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public async Task CurlRejectsTruncatedCaptureInsteadOfDecodingPartialBase64()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes(new string('x', 2048))));
        var tool = WebFetch.Create(new LocalFileSandbox(shellOptions: new() { MaxOutputBytes = 1024 }), new RecordingModel());
        Assert.Contains("capture was truncated", await InvokeAsync(tool, server.Endpoint.ToString()).WaitAsync(TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public async Task CurlFollowsRedirectsAndCapsDownloadsBeforeTheBodyIsFullyReceived()
    {
        await using var server = new WebTestServer(request => request.Target == "/start"
            ? new(Array.Empty<byte>(), Status: 302, Location: "/large")
            : new(Encoding.UTF8.GetBytes(new string('x', WebFetch.MaxBytes + 1024 * 1024)), "text/plain") { AllowPeerAbort = true });
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        var text = await InvokeAsync(tool, new Uri(server.Endpoint, "start").ToString()).WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(new string('x', WebFetch.MaxCharacters), text);
        Assert.Equal(2, server.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    public async Task CurlUsesOnlyTheFinalResponseContentType(string? contentType)
    {
        await using var server = new WebTestServer(request => request.Target == "/start"
            ? new(Array.Empty<byte>(), "text/html", Status: 302, Location: "/page")
            : new(Encoding.UTF8.GetBytes("<p>literal markup</p>"), ContentType: contentType));
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        Assert.Equal("<p>literal markup</p>", await InvokeAsync(tool, new Uri(server.Endpoint, "start").AbsoluteUri));
    }

    [Fact]
    public async Task CurlRejectsOversizedResponseHeaders()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("body"), "text/plain; padding=" + new string('x', 70000)) { AllowPeerAbort = true });
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        var result = await InvokeAsync(tool, server.Endpoint.AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(25));
        Assert.StartsWith("Failed to fetch ", result, StringComparison.Ordinal);
        Assert.Contains("headers", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationStopsCurlWhileWaitingForTheResponseBody()
    {
        var headers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("never sent")) { HeadersSent = headers, BodyGate = gate, AllowPeerAbort = true });
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        using var cancellation = new CancellationTokenSource();
        var execution = InvokeAsync(tool, server.Endpoint.ToString(), cancellationToken: cancellation.Token);
        await headers.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CurlReceivesLiteralUrlMetacharactersAndReportsHttpErrors()
    {
        string? target = null;
        await using var server = new WebTestServer(request => { target = request.Target; return new(Array.Empty<byte>(), Status: 404); });
        var tool = WebFetch.Create(new HarnessOptions().Sandbox, new RecordingModel());
        var url = server.Endpoint.AbsoluteUri + "page?quote='x'&literal=$();[]&space=a%20b";
        Assert.StartsWith("Failed to fetch ", await InvokeAsync(tool, url), StringComparison.Ordinal);
        Assert.NotNull(target);
        Assert.Contains("quote='x'", target, StringComparison.Ordinal);
        Assert.Contains("literal=$();[]", target, StringComparison.Ordinal);
        Assert.Contains("space=a%20b", target, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/plain; charset=iso-8859-1", "café")]
    [InlineData("text/plain; charset=windows-1252", "café")]
    public async Task DeclaredCharsetsAreDecodedBeforeTextReachesTheModel(string contentType, string expected)
    {
        await using var server = new WebTestServer(_ => new(new byte[] { 99, 97, 102, 233 }, contentType));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        Assert.Equal(expected, await InvokeAsync(tool, server.Endpoint.ToString()));
    }

    [Fact]
    public async Task DirectReadsStopAtTheByteCapAndDisposeTheRemainder()
    {
        var stream = new TrackedStream(new byte[WebFetch.MaxBytes + 1024 * 1024]);
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client });
        Assert.Equal(WebFetch.MaxCharacters, (await InvokeAsync(tool, "http://fixture.test/page")).Length);
        Assert.Equal(WebFetch.MaxBytes, stream.ReadBytes);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("http://example.test/has space")]
    [InlineData("https://éxample.test/")]
    [InlineData("http://example.test/'quoted\"'")]
    [InlineData("https:///example.test/")]
    [InlineData("ftp://example.test/")]
    [InlineData("file:///etc/passwd")]
    public async Task MalformedUrlsNeverReachTheTransport(string url)
    {
        var calls = 0;
        using var client = new HttpClient(new ResponseHandler(() => { calls++; return new(HttpStatusCode.OK); }));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, HttpClient = client });
        Assert.StartsWith("Failed to fetch ", await InvokeAsync(tool, url), StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FailedResponsesAreNotCachedAndNonHttpRedirectsAreRejected()
    {
        var calls = 0;
        await using var server = new WebTestServer(request =>
        {
            if (request.Target == "/unsafe") return new(Array.Empty<byte>(), Status: 302, Location: "ftp://example.test/private");
            return Interlocked.Increment(ref calls) == 1 ? new(Array.Empty<byte>(), Status: 503) : new(Encoding.UTF8.GetBytes("recovered"));
        });
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        Assert.Contains("HTTP 503", await InvokeAsync(tool, server.Endpoint.ToString()));
        Assert.Equal("recovered", await InvokeAsync(tool, server.Endpoint.ToString()));
        Assert.StartsWith("Failed to fetch ", await InvokeAsync(tool, new Uri(server.Endpoint, "unsafe").ToString()), StringComparison.Ordinal);
        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public async Task AFetchDeadlineAndCallerCancellationBothStopPendingBodyReads()
    {
        var headers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("never sent")) { HeadersSent = headers, BodyGate = gate, AllowPeerAbort = true });
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, Timeout = TimeSpan.FromMilliseconds(250) });
        Assert.Contains("gave up after 0.25s", await InvokeAsync(tool, server.Endpoint.ToString()).WaitAsync(TimeSpan.FromSeconds(5)));
        headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        using var cancellation = new CancellationTokenSource();
        var execution = InvokeAsync(tool, server.Endpoint.ToString(), cancellationToken: cancellation.Token);
        await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CacheExpiresAfterFifteenMinutesAndCharacterBoundsPreserveSurrogatePairs()
    {
        long ticks = 0;
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes(new string('x', 49_999) + "😀tail")));
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct }, () => ticks);
        var text = await InvokeAsync(tool, server.Endpoint.ToString());
        Assert.Equal(49_999, text.Length);
        ticks = Stopwatch.Frequency * 899L;
        await InvokeAsync(tool, server.Endpoint.ToString());
        Assert.Equal(1, server.Calls);
        ticks = Stopwatch.Frequency * 900L;
        await InvokeAsync(tool, server.Endpoint.ToString());
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task DirectFetchFollowsHttpRedirectsAndConvertsHtmlWithoutAcquiringSandboxAuthority()
    {
        string? userAgent = null;
        await using var server = new WebTestServer(request =>
        {
            userAgent = request.Headers["User-Agent"];
            return request.Target == "/start" ? new(Array.Empty<byte>(), Status: 302, Location: "/page")
                : new(Encoding.UTF8.GetBytes("<html><style>hide</style><script>secret()</script><p>Hello   world &amp; café</p></html>"), "text/html; charset=utf-8");
        });
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct });
        Assert.Equal("Hello world &amp; café", await InvokeAsync(tool, new Uri(server.Endpoint, "start").ToString()));
        Assert.Equal(2, server.Calls);
        Assert.Equal("strands-harness/1.0", userAgent);
    }

    [Fact]
    public async Task CachedPagesAreFetchedOnceButEachPromptUsesAFreshSummarizer()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("Concrete source facts")));
        var model = new RecordingModel();
        var tool = WebFetch.Create(new TextOnlySandbox(), new RecordingModel(), new() { Transport = WebFetchTransport.Direct, Model = model });
        Assert.Equal("summary", await InvokeAsync(tool, server.Endpoint.ToString(), "First request"));
        Assert.Equal("summary", await InvokeAsync(tool, server.Endpoint.ToString(), "Second request"));
        Assert.Equal(1, server.Calls);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request => { Assert.Single(request.Messages); Assert.Contains("Concrete source facts", request.Messages[0].Text); Assert.Contains("Use only the provided content", request.SystemPrompt); });
        Assert.Contains("First request", model.Requests[0].Messages[0].Text);
        Assert.Contains("Second request", model.Requests[1].Messages[0].Text);
    }

    private static async Task<string> InvokeAsync(ITool tool, string url, string? prompt = null, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(new { url, prompt });
        var parent = new Agent(new() { Model = new RecordingModel() });
        return await tool.InvokeAsync(input, new(parent, new("fetch", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sandbox access is forbidden.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Sandbox access is forbidden.");
    }
    private sealed class RecordingModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "summary"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
    private sealed class TrackedStream(byte[] data) : MemoryStream(data)
    {
        public int ReadBytes { get; private set; }
        public bool Disposed { get; private set; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await base.ReadAsync(buffer, offset, count, cancellationToken);
            ReadBytes += read;
            return read;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CancellationStream : MemoryStream
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Started.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class ThrowingContent(Stream stream, Exception failure) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw failure; }
    }
}
