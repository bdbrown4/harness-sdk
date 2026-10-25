using System.Net;
using System.Net.Http;
using System.Text;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ProviderTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task CancellationCallbackCannotThrowAndRetainsTheBodyCloseFailure(int provider)
    {
        var body = new WaitingBody();
        using var handler = new Handler(body, HttpStatusCode.OK, provider == 3 ? "application/x-ndjson" : "text/event-stream");
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var execution = Consume(Create(provider, client), cancellation.Token);
        Assert.Same(body.Started.Task, await Task.WhenAny(body.Started.Task, Task.Delay(TimeSpan.FromSeconds(10))));

        var callbackError = Record.Exception(() => cancellation.Cancel());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Null(callbackError);
        Assert.Contains(body.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task HttpClassificationSurvivesResponseCleanupFailure(int provider)
    {
        var body = new ErrorBodyStream("""{"error":{"code":"rate_limit_exceeded","type":"rate_limit_error","message":"busy"}}""");
        using var handler = new Handler(body, (HttpStatusCode)429, "application/json");
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => Consume(Create(provider, client)));
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Contains(body.CleanupFailure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, body.DisposeCount);
        Assert.True(handler.Response.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task InvalidMediaTypeRetainsProtocolErrorWhenBodyCloseFails(int provider)
    {
        var body = new ErrorBodyStream("{}");
        using var handler = new Handler(body, HttpStatusCode.OK, "application/json");
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelException>(() => Consume(Create(provider, client)));
        Assert.Contains("not text/event-stream", error.Message, StringComparison.Ordinal);
        Assert.Contains(body.CleanupFailure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, body.DisposeCount);
        Assert.True(handler.Response.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task FailedErrorBodyReadAndCleanupRemainAvailableUnderHttpClassification(int provider)
    {
        var body = new ErrorBodyStream("") { FailRead = true };
        using var handler = new Handler(body, (HttpStatusCode)429, "application/json");
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => Consume(Create(provider, client)));
        Assert.Same(body.ReadFailure, Assert.IsType<HttpRequestException>(error.InnerException).InnerException);
        Assert.Contains(body.CleanupFailure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, body.DisposeCount);
        Assert.True(handler.Response.Disposed);
    }

    private static IModel Create(int provider, HttpClient client) => provider switch
    {
        0 => new OpenAIModel(client, new("model", "key")),
        1 => new AnthropicModel(client, new("model", "key")),
        2 => new OpenAIResponsesModel(client, new("model", "key")),
        3 => new OllamaModel(client, new("model")),
        4 => new GoogleModel(client, new("model", "key")),
        5 => new MistralModel(client, new("model", "key")),
        _ => new WriterModel(client, new("model", "key"))
    };
    private static async Task Consume(IModel model, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in model.StreamAsync(new([Message.User("go")]), cancellationToken)) { }
    }
    private sealed class WaitingBody : Stream
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FormatException Failure { get; } = new("body close failed");
        private int _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Started.TrySetResult(true); return _read.Task; }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _read.TrySetException(new ObjectDisposedException(nameof(WaitingBody)));
            throw Failure;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal Handler(Stream body, HttpStatusCode status, string mediaType)
        {
            Response = new(status) { Content = new StreamContent(body) };
            Response.Content.Headers.ContentType = new(mediaType);
        }
        internal Response Response { get; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult<HttpResponseMessage>(Response);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Response(HttpStatusCode status) : HttpResponseMessage(status)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ErrorBodyStream(string text) : Stream
    {
        private readonly MemoryStream _source = new(Encoding.UTF8.GetBytes(text));
        internal IOException ReadFailure { get; } = new("error body read failed");
        internal IOException CleanupFailure { get; } = new("error body close failed");
        internal bool FailRead { get; init; }
        internal int DisposeCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => FailRead ? throw ReadFailure : _source.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => FailRead ? Task.FromException<int>(ReadFailure) : Task.FromResult(Read(buffer, offset, count));
        protected override void Dispose(bool disposing) { DisposeCount++; _source.Dispose(); base.Dispose(disposing); throw CleanupFailure; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
