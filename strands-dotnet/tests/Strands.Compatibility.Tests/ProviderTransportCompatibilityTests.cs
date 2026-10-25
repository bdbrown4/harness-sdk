using System.Net;
using System.Net.Http;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class ProviderTransportCompatibilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FrameworkEmptyResponseFailsWithProtocolErrorAndAlwaysClosesResponse(int provider)
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        IModel model = provider switch
        {
            0 => new OpenAIModel(client, new("model", "key")),
            1 => new AnthropicModel(client, new("model", "key")),
            _ => new OpenAIResponsesModel(client, new("model", "key"))
        };
        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([Message.User("go")]))) { }
        });
        Assert.Contains("text/event-stream", error.Message, StringComparison.Ordinal);
        Assert.True(handler.Response.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HttpClassificationRetainsReadAndCleanupErrorsOnEveryRuntime(int provider)
    {
        var body = new FailingBody();
        using var handler = new Handler();
        handler.Response.StatusCode = (HttpStatusCode)429;
        handler.Response.Content = new StreamContent(body);
        using var client = new HttpClient(handler);
        IModel model = provider switch
        {
            0 => new OpenAIModel(client, new("model", "key")),
            1 => new AnthropicModel(client, new("model", "key")),
            _ => new OpenAIResponsesModel(client, new("model", "key"))
        };
        var error = await Assert.ThrowsAsync<ModelThrottledException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([Message.User("go")]))) { }
        });
        Assert.Same(body.ReadFailure, Assert.IsType<HttpRequestException>(error.InnerException).InnerException);
        Assert.Contains(body.CleanupFailure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, body.DisposeCount);
        Assert.True(handler.Response.Disposed);
        Assert.False(handler.Disposed);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal Response Response { get; } = new();
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult<HttpResponseMessage>(Response);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Response() : HttpResponseMessage(HttpStatusCode.OK)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FailingBody : Stream
    {
        internal IOException ReadFailure { get; } = new("error body read failed");
        internal IOException CleanupFailure { get; } = new("error body close failed");
        internal int DisposeCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw ReadFailure;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(ReadFailure);
        protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); throw CleanupFailure; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
