using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.OpenAIResponsesStructuredOutputTests;

namespace Strands.Agents.Tests.Models;

public sealed class OpenAIResponsesStructuredOutputLifetimeTests
{
    [Fact]
    public async Task AlreadyCanceledCallNeverResolvesCredentialsOrSends()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var credentials = 0;
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        using var client = new HttpClient(transport);
        var model = new OpenAIResponsesModel(client, new("model", _ => { credentials++; return new ValueTask<string>("key"); }));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.InvokeStructuredAsync(new([Message.User("question")]), Options(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RealTcpBodyReadCancellationHasTheCallerToken()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ResponsesStructuredLoopback(Wire("{\"n\":1}"), release.Task);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = new OpenAIResponsesModel(client, new("model", "key") { Endpoint = server.Endpoint }).InvokeStructuredAsync(new([Message.User("question")]), Options(), cancellation.Token);
        await server.ResponseHeadersSent;
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task CancellationDuringSuccessfulResponseCleanupCannotReturnTypedSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        transport.Body.Closing = cancellation.Cancel;
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(transport.Body.Closed);
    }

    [Fact]
    public async Task CancellationDuringDeserializerPreservesTheCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}", "{\"n\":2}"));
        using var client = new HttpClient(transport);
        var calls = 0;
        var options = Options() with { Deserializer = value => { calls++; cancellation.Cancel(); return new(value.GetProperty("n").GetInt32()); } };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), options, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, calls);
        Assert.True(transport.Body.Closed);
    }

    [Fact]
    public async Task ParseFailureRetainsItsOriginalCauseAndSecondaryCleanupFailure()
    {
        var original = new IOException("deserialize-primary");
        var cleanup = new IOException("response-cleanup");
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        transport.Body.CloseError = cleanup;
        using var client = new HttpClient(transport);
        var options = Options() with { Deserializer = _ => throw original };
        var error = await Assert.ThrowsAsync<StructuredOutputException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), options));
        Assert.Same(original, error.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(transport.Body.Closed);
    }

    [Fact]
    public async Task CleanupOnlyFailureRemainsTheSameException()
    {
        var cleanup = new IOException("response-cleanup");
        using var transport = new ResponsesStructuredTransport(Wire("{\"n\":1}"));
        transport.Body.CloseError = cleanup;
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<IOException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Same(cleanup, error);
    }

    [Fact]
    public async Task CleanupCannotAttachThePrimaryExceptionToItself()
    {
        var original = new IOException("read-and-cleanup");
        using var transport = new ResponsesStructuredTransport("");
        transport.Body.ReadError = original;
        transport.Body.CloseError = original;
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<IOException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Same(original, error);
        Assert.Null(error.Data["Strands.CleanupErrors"]);
    }

    [Fact]
    public async Task DuplicateSecondaryCleanupReferencesAreRecordedOnlyOnce()
    {
        var original = new IOException("read-primary");
        var cleanup = new IOException("same-secondary");
        using var transport = new ResponsesStructuredTransport("");
        transport.Body.ReadError = original;
        transport.Body.CloseError = cleanup;
        transport.RequestAccepted = request =>
        {
            var old = request.Content;
            request.Content = new DisposeFailureContent(cleanup, () => transport.Body.ReadAttempted);
            old!.Dispose();
        };
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<IOException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.Same(original, error);
        Assert.True(transport.Body.ReadAttempted);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task NullOptionsFailBeforeCredentialResolution()
    {
        var credentials = 0;
        using var client = new HttpClient();
        var model = new OpenAIResponsesModel(client, new("model", _ => { credentials++; return new ValueTask<string>("key"); }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => model.InvokeStructuredAsync<Answer>(new([Message.User("question")]), null!));
        Assert.Equal(0, credentials);
    }

    [Fact]
    public async Task CanceledBodyOpenObservesAndClosesALateOwnedStream()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new DeferredStructuredContent();
        using var transport = new ResponsesStructuredTransport("")
        {
            Sending = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
        };
        using var client = new HttpClient(transport);
        var pending = new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options(), cancellation.Token);
        await content.Entered.Task;
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(content.Closed);
        var body = new ResponsesStructuredBody(Encoding.UTF8.GetBytes(Wire("{\"n\":1}")));
        content.Opening.TrySetResult(body);
        var cleanup = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
        Assert.Empty(await cleanup);
        Assert.True(body.Closed);
    }

    [Fact]
    public async Task EnteredConfigurationSurvivesAnUpdateWithoutTakingClientOwnership()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new ResponsesStructuredTransport("")
        { Sending = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Wire("{\"n\":1}"), Encoding.UTF8, "application/json") }) };
        using var client = new HttpClient(transport);
        var model = new OpenAIResponsesModel(client, new("old", _ => { entered.TrySetResult(true); return new ValueTask<string>(release.Task); }) { MaxTokens = 11 });
        var pending = model.InvokeStructuredAsync(new([Message.User("one")]), Options());
        await entered.Task;
        model.UpdateConfig(options => options with { ModelId = "new", ApiKeyProvider = null, ApiKey = "new-key", MaxTokens = 22 });
        release.TrySetResult("old-key");
        await pending;
        await model.InvokeStructuredAsync(new([Message.User("two")]), Options());
        Assert.Equal(new[] { "old", "new" }, transport.Requests.Select(value => value.GetProperty("model").GetString()));
        Assert.Equal(new[] { 11, 22 }, transport.Requests.Select(value => value.GetProperty("max_output_tokens").GetInt32()));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("new", model.GetConfig().ModelId);
    }

    [Fact]
    public async Task NativeHttpErrorRetainsClassificationAndClosesTheResponse()
    {
        using var transport = new ResponsesStructuredTransport("""{"error":{"code":"rate_limit_exceeded","message":"limited"}}""") { Status = (HttpStatusCode)429 };
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.True(transport.Body.Closed);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("Too many requests from provider", true)]
    [InlineData("prompt tokens exceed customer model maximum", false)]
    public async Task PythonMessageOnlyApiErrorClassificationRetainsNativeCause(string message, bool throttled)
    {
        using var transport = new ResponsesStructuredTransport(JsonSerializer.Serialize(new { error = new { message } })) { Status = HttpStatusCode.BadRequest };
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<ModelException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        if (throttled) Assert.IsType<ModelThrottledException>(error);
        else Assert.IsType<ContextWindowOverflowException>(error);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.True(transport.Body.Closed);
    }

    [Theory]
    [InlineData("application/json", "{broken")]
    [InlineData("text/event-stream", "data: {}\n\n")]
    public async Task MalformedBodyOrWrongProtocolAlwaysCloses(string type, string wire)
    {
        using var transport = new ResponsesStructuredTransport(wire) { ContentType = type };
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.True(transport.Body.Closed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Utf8ResponseLimitIsAnExactByteBoundary(int difference)
    {
        var wire = Wire("{\"n\":1}").Replace("response-one", "é-response");
        using var transport = new ResponsesStructuredTransport(wire);
        using var client = new HttpClient(transport);
        var model = new OpenAIResponsesModel(client, new("model", "key") { MaxResponseBytes = Encoding.UTF8.GetByteCount(wire) + difference });
        if (difference == 0) Assert.Equal(1, (await model.InvokeStructuredAsync(new([Message.User("question")]), Options())).N);
        else await Assert.ThrowsAsync<ModelException>(() => model.InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.True(transport.Body.Closed);
    }

    private sealed class DeferredStructuredContent : HttpContent
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Stream> Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        internal DeferredStructuredContent() => Headers.ContentType = new("application/json");
        protected override Task<Stream> CreateContentReadStreamAsync() { Entered.TrySetResult(true); return Opening.Task; }
#if NET8_0_OR_GREATER
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
#endif
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private sealed class DisposeFailureContent(Exception failure, Func<bool> armed) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (armed()) throw failure; }
    }
}
