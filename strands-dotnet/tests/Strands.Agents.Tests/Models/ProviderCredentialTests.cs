using System.Net;
using System.Net.Http;
using System.Text;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ProviderCredentialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshesCredentialsForEachRequestWithoutChangingTheBorrowedClient(bool responses)
    {
        using var handler = new Handler(responses);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated");
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var model = Create(client, responses, token =>
        {
            Assert.Equal(cancellation.Token, token);
            return new ValueTask<string>("fresh-" + ++calls);
        });

        await Consume(model, cancellation.Token);
        await Consume(model, cancellation.Token);

        Assert.Equal(new[] { "fresh-1", "fresh-2" }, handler.Credentials);
        Assert.Equal("unrelated", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotAcquireCredentialsForAnAlreadyCancelledOperation(bool responses)
    {
        using var handler = new Handler(responses);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var model = Create(client, responses, _ => { calls++; return new("key"); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Consume(model, cancellation.Token));

        Assert.Equal(0, calls);
        Assert.Empty(handler.Credentials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsCredentialFailuresWithoutStartingHttpOrRetrying(bool responses)
    {
        using var handler = new Handler(responses);
        using var client = new HttpClient(handler);
        var failure = new InvalidOperationException("credential resolution failed");
        var calls = 0;
        var model = Create(client, responses, _ => { calls++; throw failure; });

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Consume(model)));

        Assert.Equal(1, calls);
        Assert.Empty(handler.Credentials);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, " ")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    public async Task RejectsEmptyResolvedCredentialsBeforeHttp(bool responses, string? key)
    {
        using var handler = new Handler(responses);
        using var client = new HttpClient(handler);
        var model = Create(client, responses, _ => new(key!));

        await Assert.ThrowsAsync<ModelException>(() => Consume(model));

        Assert.Empty(handler.Credentials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsWaitingForAnUncooperativeCredentialProvider(bool responses)
    {
        using var handler = new Handler(responses);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Create(client, responses, _ => { entered.TrySetResult(true); return new(released.Task); });
        var consuming = Consume(model, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        try
        {
            Assert.Same(consuming, await Task.WhenAny(consuming, Task.Delay(TimeSpan.FromSeconds(2))));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consuming);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(handler.Credentials);
        }
        finally
        {
            released.TrySetResult("late credential");
            try { await consuming; } catch (OperationCanceledException) { }
        }
    }

    private static IModel Create(HttpClient client, bool responses, Func<CancellationToken, ValueTask<string>> provider) => responses
        ? new OpenAIResponsesModel(client, new("model", provider))
        : new OpenAIModel(client, new("model", provider));
    private static async Task Consume(IModel model, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in model.StreamAsync(new([Message.User("question")]), cancellationToken)) { }
    }
    private sealed class Handler(bool responses) : HttpMessageHandler
    {
        public List<string?> Credentials { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            var text = responses
                ? "data: {\"type\":\"response.created\",\"response\":{\"id\":\"response-id\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"response-id\",\"status\":\"completed\",\"output\":[]}}\n\n"
                : "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") });
        }
    }
}
