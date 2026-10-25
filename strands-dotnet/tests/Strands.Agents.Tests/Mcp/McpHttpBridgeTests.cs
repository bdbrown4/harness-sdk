using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpHttpBridgeTests
{
    [Fact]
    public async Task OpaqueUnknownLengthPayloadLargerThanOneMiBReachesTheRealServerUnchanged()
    {
        var payload = new byte[1024 * 1024 + 257];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31);
        payload[0] = 0xef; payload[1] = 0xbb; payload[2] = 0xbf;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient? connected = null;
        var uri = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/opaque");
        var serving = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            Volatile.Write(ref connected, socket);
            using var stream = socket.GetStream();
            Assert.StartsWith("POST /opaque ", await ReadAsciiLineAsync(stream));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = await ReadAsciiLineAsync(stream)).Length != 0)
            {
                var colon = line.IndexOf(':');
                headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
            }
            if (headers.TryGetValue("Expect", out var expect) && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(interim, 0, interim.Length);
                await stream.FlushAsync();
            }
            using var received = new MemoryStream();
            if (headers.TryGetValue("Content-Length", out var length))
                await CopyExactAsync(stream, received, int.Parse(length, System.Globalization.CultureInfo.InvariantCulture));
            else
            {
                Assert.Contains("chunked", headers["Transfer-Encoding"]);
                while (true)
                {
                    var chunk = (await ReadAsciiLineAsync(stream)).Split(';')[0];
                    var count = int.Parse(chunk, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    if (count == 0) { while ((await ReadAsciiLineAsync(stream)).Length != 0) { } break; }
                    await CopyExactAsync(stream, received, count);
                    Assert.Equal("", await ReadAsciiLineAsync(stream));
                    Assert.True(received.Length <= payload.Length);
                }
            }
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(response, 0, response.Length);
            await stream.FlushAsync();
            return received.ToArray();
        });
        try
        {
            using var borrowed = new HttpClient();
            using var bridge = new Client.HttpClientBridge(borrowed);
            using var content = new Content(new MemoryStream(payload));
            Assert.Null(content.Headers.ContentLength);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            using var response = await bridge.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(payload, await serving);
        }
        finally
        {
            listener.Stop();
            Volatile.Read(ref connected)?.Dispose();
            try { await serving; } catch (Exception) { /* Preserve the primary assertion/transport failure. */ }
        }
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, 0, 1) == 1)
        {
            if (one[0] == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
            if (bytes.Count > 8192) throw new InvalidDataException("Fixture HTTP line too long.");
        }
        throw new EndOfStreamException("Fixture HTTP line incomplete.");
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, int length)
    {
        var buffer = new byte[16384];
        while (length > 0)
        {
            var count = await source.ReadAsync(buffer, 0, Math.Min(length, buffer.Length));
            if (count == 0) throw new EndOfStreamException("Fixture HTTP body incomplete.");
            await destination.WriteAsync(buffer, 0, count);
            length -= count;
        }
    }

    [Fact]
    public async Task RealRedirectRetainsTheEffectiveResponseRequestUri()
    {
        await using var destination = new McpToolChangeLoopbackServer();
        var redirect = new TcpListener(IPAddress.Loopback, 0);
        redirect.Start();
        var uri = new Uri("http://127.0.0.1:" + ((IPEndPoint)redirect.LocalEndpoint).Port + "/redirect");
        var serving = Task.Run(async () =>
        {
            using var socket = await redirect.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + destination.Endpoint.AbsoluteUri + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        });
        try
        {
            using var borrowed = new HttpClient();
            using var bridge = new Client.HttpClientBridge(borrowed);
            using var response = await bridge.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(destination.Endpoint, response.RequestMessage!.RequestUri);
            await serving;
        }
        finally { redirect.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledFactoryKeepsExactCallerTokenAndBorrowedHttpClient(bool settingsOverload)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var borrowed = new HttpClient();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var options = new HttpClientTransportOptions
        { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settingsOverload
            ? Client.CreateHttpAsync(new(), borrowed, options, cancellationToken: caller.Token)
            : Client.CreateHttpAsync(borrowed, options, cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        using var reused = await borrowed.GetAsync(server.Endpoint);
    }

    [Fact]
    public async Task DirectHttpClientCharacterizesItsOwnRequestContentOwnership()
    {
        var content = new Content(new MemoryStream());
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/direct") { Content = content };
        using var response = await borrowed.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
#if NETFRAMEWORK
        Assert.Equal(1, content.Disposals);
#else
        Assert.Equal(0, content.Disposals);
#endif
    }

    [Fact]
    public async Task ForwardingPreservesRequestDataAndBorrowedOwnership()
    {
        HttpRequestMessage? forwarded = null;
        byte[]? body = null;
        var handler = new Handler(async (request, token) =>
        {
            forwarded = request;
            if (request.Content is not null) body = await request.Content.ReadAsByteArrayAsync();
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("response") };
        });
        using var borrowed = new HttpClient(handler) { BaseAddress = new("http://localhost/base/"), Timeout = TimeSpan.FromSeconds(7) };
        borrowed.DefaultRequestHeaders.Add("X-Default", "host");
        var bridge = new Client.HttpClientBridge(borrowed);
        var content = new Content(new MemoryStream(Encoding.UTF8.GetBytes("payload")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "relative") { Content = content, Version = new(1, 1) };
        request.Headers.Add("X-Explicit", "request");
        var property = new object();
#pragma warning disable CS0618
        request.Properties["property"] = property;
#pragma warning restore CS0618
        using var response = await bridge.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.NotSame(request, forwarded);
        Assert.Equal(new Uri("http://localhost/base/relative"), forwarded!.RequestUri);
        Assert.Equal(HttpMethod.Post, forwarded.Method);
        Assert.Equal(request.Version, forwarded.Version);
        Assert.Equal("host", Assert.Single(forwarded.Headers.GetValues("X-Default")));
        Assert.Equal("request", Assert.Single(forwarded.Headers.GetValues("X-Explicit")));
#pragma warning disable CS0618
        Assert.Same(property, forwarded.Properties["property"]);
#pragma warning restore CS0618
        Assert.Equal(Encoding.UTF8.GetBytes("payload"), body);
        Assert.Same(request, response.RequestMessage);
#if NETFRAMEWORK
        Assert.Equal(1, content.Disposals); // Matches direct Framework HttpClient; the clone adds none.
#else
        Assert.Equal(0, content.Disposals);
#endif
        bridge.Dispose();
        bridge.Dispose();
        Assert.Equal(0, handler.Disposals);
        using var reused = await borrowed.GetAsync("http://localhost/again");
    }

    [Fact]
    public async Task BorrowedTimeoutRemainsAuthoritative()
    {
        using var borrowed = new HttpClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }))
        { Timeout = TimeSpan.FromMilliseconds(100) };
        using var bridge = new Client.HttpClientBridge(borrowed);
        Assert.Equal(Timeout.InfiniteTimeSpan, bridge.Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.GetAsync("http://localhost/timeout"));
    }

    [Fact]
    public async Task ResponseWrapperDisposesItsContentOnceAndDoesNotOwnBorrowedClient()
    {
        var stream = new ProbeStream();
        var content = new Content(stream);
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var borrowed = new HttpClient(handler);
        using var bridge = new Client.HttpClientBridge(borrowed);
        var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
        _ = await response.Content.ReadAsStreamAsync();
        response.Dispose();
        response.Dispose();
        Assert.Equal(1, content.Disposals);
        Assert.True(stream.Disposals >= 1); // HttpContent can also own its cached underlying stream.
        Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationClosesAnIgnoringBodyAndRetainsCleanupCause(bool cleanupThrows)
    {
        var cleanup = new IOException("close failure");
        var stream = new ProbeStream { Hang = true, CleanupError = cleanupThrows ? cleanup : null };
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(stream) })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        using var caller = new CancellationTokenSource();
        var read = body.ReadAsync(new byte[1], 0, 1, caller.Token);
        await stream.Entered.Task;
        caller.Cancel();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(3000)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.IsType<IOException>(error.InnerException);
        if (cleanupThrows) Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])));
        try { response.Dispose(); } catch (IOException error2) { Assert.Same(cleanup, error2); }
    }

    [Fact]
    public async Task IndependentReadErrorKeepsItsIdentity()
    {
        var primary = new IOException("read failure");
        var stream = new ProbeStream { ReadError = primary };
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(stream) })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        using var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => body.ReadAsync(new byte[1], 0, 1)));
    }

    [Fact]
    public async Task ReadPrimaryWinsOverUniqueResponseCleanupErrors()
    {
        var primary = new IOException("read primary");
        var first = new IOException("stream cleanup");
        var second = new IOException("content cleanup");
        var stream = new ProbeStream { ReadError = primary, CleanupError = first };
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new Content(stream) { CleanupError = second } })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => body.ReadAsync(new byte[1], 0, 1)));
        response.Dispose();
        response.Dispose();
        var errors = Assert.IsAssignableFrom<IEnumerable<Exception>>(primary.Data["Strands.CleanupErrors"]).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Same(first, errors[0]);
        Assert.Same(second, errors[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyReadPrimaryCannotBeReplacedByResponseCleanup(bool alreadyCanceled)
    {
        var primary = new IOException("synchronous read primary");
        var cleanup = new IOException("response cleanup");
        using var caller = new CancellationTokenSource();
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new Content(new ProbeStream { ReadError = primary }) { CleanupError = cleanup } })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        async Task ReadAndDisposeAsync()
        {
            using var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStreamAsync();
            if (alreadyCanceled)
            {
                caller.Cancel();
                Assert.Equal(0, await body.ReadAsync(new byte[1], 0, 1, caller.Token));
            }
            else Assert.Equal(0, body.Read(new byte[1], 0, 1));
        }
        var failure = await Assert.ThrowsAnyAsync<Exception>(ReadAndDisposeAsync);
        if (alreadyCanceled) Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        else Assert.Same(primary, failure);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(failure.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task HandlerCancellationCallbackHasALiveSameBridgeReceipt()
    {
        Client.HttpClientBridge? bridge = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reentry = null;
        using var borrowed = new HttpClient(new Handler(async (_, token) =>
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() =>
            {
                try { bridge!.RefuseLifecycleReentry(); }
                catch (Exception error) { reentry = error; }
                finally { canceled.TrySetResult(true); }
            });
            entered.SetResult(true);
            await canceled.Task;
            token.ThrowIfCancellationRequested();
            return new(HttpStatusCode.OK);
        }));
        using (bridge = new Client.HttpClientBridge(borrowed))
        using (var caller = new CancellationTokenSource())
        {
            var sending = bridge.GetAsync("http://localhost/pending", caller.Token);
            await entered.Task;
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.IsType<InvalidOperationException>(reentry);
            bridge.RefuseLifecycleReentry();
        }
    }

    [Fact]
    public async Task RejectedResponseEnvelopeKeepsPrimaryAndClosesOwnedContent()
    {
        var primary = new FormatException("custom header conversion failed");
        var cleanup = new IOException("failed-envelope cleanup");
        var content = new Content(new ProbeStream()) { CleanupError = cleanup };
        content.Headers.ContentDisposition = new ThrowingDisposition(primary);
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => bridge.GetAsync("http://localhost/headers", HttpCompletionOption.ResponseHeadersRead));
        Assert.Same(primary, failure);
        Assert.Equal(1, content.Disposals);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(failure.Data["Strands.CleanupErrors"])));
    }

    private sealed class ThrowingDisposition(Exception error) : ContentDispositionHeaderValue("attachment")
    {
        public override string ToString() => throw error;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAndCleanupCallbacksCannotWaitOnTheirOwnBridge(bool tokenCallback)
    {
        Client.HttpClientBridge? bridge = null;
        Exception? reentry = null;
        var stream = new ProbeStream { Hang = true };
        Action callback = () => { try { bridge!.RefuseLifecycleReentry(); } catch (Exception error) { reentry = error; } };
        if (tokenCallback) stream.DuringRead = token => token.Register(callback);
        else stream.DuringDispose = callback;
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(stream) })));
        using (bridge = new Client.HttpClientBridge(borrowed))
        using (var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead))
        using (var caller = new CancellationTokenSource())
        {
            var body = await response.Content.ReadAsStreamAsync();
            var read = body.ReadAsync(new byte[1], 0, 1, caller.Token);
            await stream.Entered.Task;
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.IsType<InvalidOperationException>(reentry);
        }
    }

    [Fact]
    public async Task EscapedReceiptExpiresAfterTheResponseAndOtherBridgesStayIndependent()
    {
        Client.HttpClientBridge? bridge = null;
        using var release = new SemaphoreSlim(0);
        Task? escaped = null;
        using var otherBorrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using var other = new Client.HttpClientBridge(otherBorrowed);
        using var borrowed = new HttpClient(new Handler((_, _) =>
        {
            Assert.Throws<InvalidOperationException>(() => bridge!.RefuseLifecycleReentry());
            other.RefuseLifecycleReentry();
            escaped = Task.Run(async () => { await release.WaitAsync(); bridge!.RefuseLifecycleReentry(); });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }));
        using (bridge = new Client.HttpClientBridge(borrowed))
        {
            var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
            response.Dispose();
            release.Release();
            await escaped!;
        }
    }

    [Fact]
    public async Task LateOpenedBodyIsClosedAfterConcurrentResponseDisposal()
    {
        var ready = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ProbeStream();
        using var borrowed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(stream) { Opening = ready.Task } })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        var response = await bridge.GetAsync("http://localhost/body", HttpCompletionOption.ResponseHeadersRead);
        var opening = response.Content.ReadAsStreamAsync();
        response.Dispose();
        ready.SetResult(stream);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
        Assert.True(stream.Disposals > 0);
    }

    [Fact]
    public async Task CancelingOneResponseLeavesAnotherRequestAndBorrowedClientUsable()
    {
        var first = new ProbeStream { Hang = true };
        using var borrowed = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath == "/first" ? new Content(first) : new StringContent("other") })));
        using var bridge = new Client.HttpClientBridge(borrowed);
        using var response = await bridge.GetAsync("http://localhost/first", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        using var caller = new CancellationTokenSource();
        var read = body.ReadAsync(new byte[1], 0, 1, caller.Token);
        await first.Entered.Task;
        using var other = await bridge.GetAsync("http://localhost/other");
        Assert.Equal("other", await other.Content.ReadAsStringAsync());
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        using var reused = await borrowed.GetAsync("http://localhost/again");
        Assert.Equal("other", await reused.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFrameworkFactoryRefusesItsOwnBodyLifecycleCallbacks(bool tokenCallback)
    {
#if NETFRAMEWORK
        await using var server = new McpToolChangeLoopbackServer();
        Client? client = null;
        var reentries = new List<Exception>();
        var handler = new BodyObserverHandler(() =>
        {
            try { _ = client!.DisposeAsync(); }
            catch (Exception error) { lock (reentries) reentries.Add(error); }
        }, tokenCallback);
        using var http = new HttpClient(handler);
        client = await Client.CreateHttpAsync(http,
            new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2026-07-28" });
        McpToolChangeSubscription? watch = null;
        try
        {
            watch = await client.WatchToolChangesAsync((_, _) => default);
            var closing = watch.DisposeAsync().AsTask();
            Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(5000)));
            await closing;
            lock (reentries) { Assert.NotEmpty(reentries); Assert.All(reentries, error => Assert.IsType<InvalidOperationException>(error)); }
            Assert.Single(await client.ListToolsAsync());
        }
        finally
        {
            server.EndListen();
            if (watch is not null) await watch.DisposeAsync();
            await client.DisposeAsync();
        }
#else
        // The native modern factory intentionally retains the original HTTP transport path.
        await Task.CompletedTask;
        _ = tokenCallback;
#endif
    }

#if NETFRAMEWORK
    private sealed class BodyObserverHandler(Action callback, bool tokenCallback) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                response.Content = new ObservedContent(response.Content, callback, tokenCallback);
            return response;
        }
    }
    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent owned;
        private readonly Action callback;
        private readonly bool tokenCallback;
        internal ObservedContent(HttpContent owned, Action callback, bool tokenCallback)
        {
            this.owned = owned; this.callback = callback; this.tokenCallback = tokenCallback;
            foreach (var header in owned.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new ObservedStream(await owned.ReadAsStreamAsync(), callback, tokenCallback);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => owned.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) owned.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ObservedStream(Stream owned, Action callback, bool tokenCallback) : Stream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var registration = tokenCallback ? cancellationToken.Register(callback) : default;
            return await owned.ReadAsync(buffer, offset, count, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) { if (!tokenCallback) callback(); owned.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => owned.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => owned.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
#endif

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }

    private sealed class Content(Stream stream) : HttpContent
    {
        internal int Disposals;
        internal Task<Stream>? Opening { get; init; }
        internal IOException? CleanupError { get; init; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Opening ?? Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref Disposals);
            try { base.Dispose(disposing); }
            finally { if (disposing && CleanupError is { } error) throw error; }
        }
    }

    private sealed class ProbeStream : Stream
    {
        internal bool Hang { get; init; }
        internal IOException? ReadError { get; init; }
        internal IOException? CleanupError { get; init; }
        internal Action<CancellationToken>? DuringRead { get; set; }
        internal Action? DuringDispose { get; set; }
        internal int Disposals;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            DuringRead?.Invoke(cancellationToken);
            Entered.TrySetResult(true);
            return ReadError is { } error ? Task.FromException<int>(error) : Hang ? _read.Task : Task.FromResult(0);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref Disposals);
                DuringDispose?.Invoke();
                _read.TrySetException(new IOException("body closed"));
                if (CleanupError is { } error) throw error;
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw ReadError ?? new IOException("Unexpected synchronous read.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
