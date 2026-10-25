using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Models;
using Xunit;

namespace Strands.Agents.Tests.Models;

public sealed class AnthropicNativeCountPolicyConformanceTests
{
    [Theory]
    [InlineData("missing", "{}")]
    [InlineData("null", "{\"input_tokens\":null}")]
    [InlineData("string", "{\"input_tokens\":\"913\"}")]
    [InlineData("fractional", "{\"input_tokens\":1.5}")]
    [InlineData("negative", "{\"input_tokens\":-1}")]
    [InlineData("outside-int64", "{\"input_tokens\":9223372036854775808}")]
    public async Task MalformedCountFallsBackAndDisposesResponse(string shape, string json)
    {
        Assert.NotEmpty(shape);
        var content = new ObservedContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            Assert.Equal(1L, await model.CountTokensAsync(Request()));
            Assert.Equal(1, handler.CountRequests);
            Assert.True(content.Disposed);
        }
        finally { content.CloseFixture(); }
    }

    [Theory]
    [InlineData(65536, 913L)]
    [InlineData(65537, 1L)]
    public async Task CompleteResponseByteLimitAcceptsBoundaryAndFallsBackAboveIt(int bytes, long expected)
    {
        const string prefix = "{\"input_tokens\":913,\"padding\":\"";
        const string suffix = "\"}";
        var json = prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(json));
        var content = new ObservedContent(json);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            Assert.Equal(expected, await model.CountTokensAsync(Request()));
            Assert.Equal(1, handler.CountRequests);
            Assert.True(content.Disposed);
        }
        finally { content.CloseFixture(); }
    }

    [Fact]
    public async Task NonJsonContentTypeFallsBackAndDisposesResponse()
    {
        var content = new ObservedContent("{\"input_tokens\":913}", mediaType: "text/plain");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            Assert.Equal(1L, await model.CountTokensAsync(Request()));
            Assert.Equal(1, handler.CountRequests);
            Assert.True(content.Disposed);
        }
        finally { content.CloseFixture(); }
    }

    [Fact]
    public async Task CleanupOnlyFailurePropagatesTheOriginalException()
    {
        var failure = new InvalidOperationException("owned count response cleanup failed");
        var content = new ObservedContent("{\"input_tokens\":913}", failure);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => model.CountTokensAsync(Request()).AsTask());
            Assert.Same(failure, error);
            Assert.Equal(1, handler.CountRequests);
            Assert.True(content.Disposed);
        }
        finally
        {
            content.CloseFixture();
            response.Dispose();
        }
    }

    [Fact]
    public async Task ParseFailureRetainsCleanupDiagnosticsAndDoesNotFallBack()
    {
        var cleanup = new InvalidOperationException("owned count response cleanup failed");
        var content = new ObservedContent("not-json", cleanup);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            var error = await Assert.ThrowsAsync<ModelException>(() => model.CountTokensAsync(Request()).AsTask());
            Assert.Contains("JSON", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
            var errors = Assert.IsType<System.Collections.Generic.List<Exception>>(error.Data["Strands.CleanupErrors"]);
            Assert.Same(cleanup, Assert.Single(errors));
            Assert.Equal(1, handler.CountRequests);
            Assert.True(content.Disposed);
        }
        finally
        {
            content.CloseFixture();
            response.Dispose();
        }
    }

    [Fact]
    public async Task IndependentBodyCancellationPropagatesInsteadOfReturningAnEstimate()
    {
        using var caller = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        var failure = new OperationCanceledException("independent body cancellation", foreign.Token);
        using var body = new CancelledBody(failure);
        using var content = new StreamContent(body);
        content.Headers.ContentType = new("application/json");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new ReplyHandler(response);
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(Request(), caller.Token).AsTask());
            Assert.False(caller.IsCancellationRequested);
            Assert.False(foreign.IsCancellationRequested);
            Assert.Equal(1, handler.CountRequests);
            Assert.Equal(1, body.ReadCalls);
            Assert.True(body.Disposed);
        }
        finally { content.Dispose(); }
    }

    private static AnthropicModelOptions Options() => new("claude-test", "count-key")
    {
        Endpoint = new Uri("https://count.invalid/v1/messages"),
        UseNativeTokenCount = true
    };

    private static ModelRequest Request() => new([Message.User("abcd")]);

    private sealed class ReplyHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        internal int CountRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://count.invalid/v1/messages/count_tokens", request.RequestUri!.AbsoluteUri);
            CountRequests++;
            Assert.Equal(1, CountRequests);
            return Task.FromResult(response);
        }
    }

    private sealed class ObservedContent(string json, Exception? cleanupFailure = null, string mediaType = "application/json")
        : StringContent(json, Encoding.UTF8, mediaType)
    {
        private bool _suppressFixtureFailure;
        private bool _failureThrown;
        internal bool Disposed { get; private set; }
        internal void CloseFixture()
        {
            _suppressFixtureFailure = true;
            Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
            if (disposing && !_suppressFixtureFailure && !_failureThrown && cleanupFailure is not null)
            {
                _failureThrown = true;
                throw cleanupFailure;
            }
        }
    }

    private sealed class CancelledBody(OperationCanceledException failure) : Stream
    {
        internal int ReadCalls { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw failure;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromException<int>(failure);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
