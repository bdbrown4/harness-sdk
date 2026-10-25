using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Models;
using Xunit;

namespace Strands.Agents.Tests.Models;

public sealed class AnthropicNativeTokenCountBoundaryTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureFallsBackOnceAndDisposesTheOwnedResponse(HttpStatusCode status)
    {
        using var content = new OwnedContent("{\"error\":{\"type\":\"count_failed\",\"message\":\"unavailable\"}}");
        using var handler = new CountHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        Assert.Equal(1L, await model.CountTokensAsync(Request()));
        Assert.Single(handler.CountRequests); Assert.True(content.Disposed);
        await AssertBorrowedClient(client, handler);
    }

    [Fact]
    public async Task TransportFailureUsesTheHeuristicWithoutRetry()
    {
        using var handler = new CountHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("count transport failed")));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        Assert.Equal(1L, await model.CountTokensAsync(Request()));
        Assert.Single(handler.CountRequests);
        await AssertBorrowedClient(client, handler);
    }

    [Fact]
    public async Task InvalidResponseJsonUsesTheHeuristicAndDisposesTheOwnedResponse()
    {
        using var content = new OwnedContent("not-json");
        using var handler = new CountHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        Assert.Equal(1L, await model.CountTokensAsync(Request()));
        Assert.Single(handler.CountRequests); Assert.True(content.Disposed);
        await AssertBorrowedClient(client, handler);
    }

    [Fact]
    public async Task AValidZeroCountDoesNotTriggerHeuristicFallback()
    {
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(0)));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options());
        Assert.Equal(0L, await model.CountTokensAsync(Request())); Assert.Single(handler.CountRequests);
    }

    [Fact]
    public async Task PreCanceledCallerDoesNotSendARequest()
    {
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(913)));
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        IModel model = new AnthropicModel(client, Options());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(Request(), caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(handler.CountRequests);
    }

    [Fact]
    public async Task ActiveCallerCancellationIsNotConvertedToAHeuristicCount()
    {
        var entered = Signal(); var release = Signal();
        using var handler = new CountHandler(async (_, token) =>
        {
            var canceled = Signal();
            using var registration = token.Register(() => canceled.TrySetCanceled(token));
            entered.TrySetResult(true);
            if (await Task.WhenAny(canceled.Task, release.Task) == canceled.Task) await canceled.Task;
            return Reply(913);
        });
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        IModel model = new AnthropicModel(client, Options());
        var operation = model.CountTokensAsync(Request(), caller.Token).AsTask();
        try
        {
            await Within(entered.Task); caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(operation));
            Assert.True(caller.IsCancellationRequested); Assert.True(error.CancellationToken.IsCancellationRequested);
            Assert.Single(handler.CountRequests);
        }
        finally { caller.Cancel(); release.TrySetResult(true); await Observe(operation); }
        await AssertBorrowedClient(client, handler);
    }

    [Fact]
    public async Task EnteredCountKeepsItsConfigurationAndLaterCallsObserveUpdates()
    {
        var entered = Signal(); var release = Signal(); var requestNumber = 0;
        using var handler = new CountHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref requestNumber) == 1) { entered.TrySetResult(true); await release.Task; return Reply(913); }
            return Reply(914);
        });
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, Options() with { Betas = ["old-beta"] });
        var first = ((IModel)model).CountTokensAsync(Request()).AsTask();
        try
        {
            await Within(entered.Task);
            model.UpdateConfig(value => value with { ModelId = "claude-new", ApiKey = "new-key", Betas = ["new-beta"], Endpoint = new Uri("https://count.invalid/new/messages"), UseNativeTokenCount = false });
            release.TrySetResult(true); Assert.Equal(913L, await Within(first));
            Assert.Equal(1L, await ((IModel)model).CountTokensAsync(Request()));
            var original = Assert.Single(handler.CountRequests);
            Assert.Equal("claude-test", original.Body.GetProperty("model").GetString());
            Assert.Equal("count-key", original.ApiKey); Assert.Equal("old-beta", original.Beta);
            model.UpdateConfig(value => value with { UseNativeTokenCount = true });
            Assert.Equal(914L, await ((IModel)model).CountTokensAsync(Request()));
            Assert.Equal(2, handler.CountRequests.Count);
            var next = handler.CountRequests[1];
            Assert.Equal("claude-new", next.Body.GetProperty("model").GetString());
            Assert.Equal("new-key", next.ApiKey); Assert.Equal("new-beta", next.Beta);
            Assert.Equal("https://count.invalid/new/messages/count_tokens", next.Uri);
        }
        finally { release.TrySetResult(true); await Observe(first); }
    }

    [Fact]
    public async Task ConstructorDetachesCountConfigurationFromCallerDocumentsAndLists()
    {
        using var parameters = JsonDocument.Parse("{\"metadata\":{\"user_id\":\"excluded\"},\"tools\":[{\"type\":\"code_execution_20250522\",\"name\":\"code\"}]}");
        using var nativeTool = JsonDocument.Parse("{\"type\":\"web_search_20250305\",\"name\":\"web\"}");
        var betas = new List<string> { "owned-beta" }; var serverTools = new List<JsonElement> { nativeTool.RootElement };
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(913)));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options() with { Params = parameters.RootElement, Betas = betas, AnthropicTools = serverTools });
        parameters.Dispose(); nativeTool.Dispose(); betas.Clear(); serverTools.Clear();
        Assert.Equal(913L, await model.CountTokensAsync(Request()));
        var captured = Assert.Single(handler.CountRequests);
        Assert.Equal("owned-beta", captured.Beta);
        Assert.Equal(new[] { "web", "code" }, captured.Body.GetProperty("tools").EnumerateArray().Select(value => value.GetProperty("name").GetString()));
        Assert.False(captured.Body.TryGetProperty("metadata", out _));
    }

    [Theory]
    [InlineData(ToolChoiceMode.Auto, 3)]
    [InlineData(ToolChoiceMode.None, 1)]
    public async Task CountWhitelistPreservesExistingFunctionAndServerToolPolicy(ToolChoiceMode mode, int expectedTools)
    {
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(913)));
        using var client = new HttpClient(handler);
        IModel model = new AnthropicModel(client, Options() with
        {
            MaxTokens = 2048,
            Temperature = 0.4,
            ThinkingBudgetTokens = 1024,
            TopP = 0.8,
            StopSequences = ["stop"],
            Params = Json("{\"metadata\":{},\"unrelated_count_field\":true,\"tools\":[{\"type\":\"code_execution_20250522\",\"name\":\"code\"}]}"),
            AnthropicTools = [Json("{\"type\":\"web_search_20250305\",\"name\":\"web\"}")]
        });
        var request = Request() with
        {
            SystemPrompt = "system",
            Tools = [new("lookup", "Find", Json("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}"))],
            ToolChoice = new(mode)
        };
        Assert.Equal(913L, await model.CountTokensAsync(request));
        var body = Assert.Single(handler.CountRequests).Body;
        Assert.Equal(new[] { "messages", "model", "system", "tool_choice", "tools" }, body.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(expectedTools, body.GetProperty("tools").GetArrayLength());
        Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", body.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal(mode == ToolChoiceMode.Auto ? "auto" : "none", body.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("https://count.invalid/v1/messages/", "https://count.invalid/v1/messages/count_tokens")]
    [InlineData("https://count.invalid/v1/messages?route=one&key=a%2Fb", "https://count.invalid/v1/messages/count_tokens?route=one&key=a%2Fb")]
    [InlineData("https://count.invalid/gateway/custom/?route=one", "https://count.invalid/gateway/custom/count_tokens?route=one")]
    public async Task ExplicitEndpointAppendsTheCountPathBeforeTheQuery(string endpoint, string expected)
    {
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(913)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://ignored.invalid/base/") };
        IModel model = new AnthropicModel(client, Options() with { Endpoint = new Uri(endpoint) });
        Assert.Equal(913L, await model.CountTokensAsync(Request())); Assert.Equal(expected, Assert.Single(handler.CountRequests).Uri);
    }

    [Theory]
    [InlineData("https://count.invalid/prefix/", "https://count.invalid/prefix/messages/count_tokens")]
    [InlineData("https://count.invalid/prefix", "https://count.invalid/messages/count_tokens")]
    public async Task MissingEndpointKeepsTheExistingBaseAddressResolution(string baseAddress, string expected)
    {
        using var handler = new CountHandler((_, _) => Task.FromResult(Reply(913)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        IModel model = new AnthropicModel(client, Options() with { Endpoint = null });
        Assert.Equal(913L, await model.CountTokensAsync(Request())); Assert.Equal(expected, Assert.Single(handler.CountRequests).Uri);
    }

    [Fact]
    public void OptionDefaultSerializationAndRecordCopyKeepTheAdditiveContract()
    {
        var original = new AnthropicModelOptions("claude-test", "count-key");
        Assert.False(original.UseNativeTokenCount);
        Assert.False(JsonSerializer.SerializeToElement(original).TryGetProperty("UseNativeTokenCount", out _));
        var enabled = original with { UseNativeTokenCount = true };
        Assert.True(JsonSerializer.SerializeToElement(enabled).GetProperty("UseNativeTokenCount").GetBoolean());
        Assert.True((enabled with { ModelId = "changed" }).UseNativeTokenCount); Assert.False(original.UseNativeTokenCount);
    }

    private static AnthropicModelOptions Options() => new("claude-test", "count-key") { Endpoint = new Uri("https://count.invalid/v1/messages"), UseNativeTokenCount = true };
    private static ModelRequest Request() => new([Message.User("abcd")]);
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Reply(long count) => new(HttpStatusCode.OK) { Content = new OwnedContent("{\"input_tokens\":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}") };
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task AssertBorrowedClient(HttpClient client, CountHandler handler)
    {
        using var response = await client.GetAsync("https://count.invalid/borrowed-client-probe");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(1, handler.BorrowedProbes);
    }
    private static async Task Observe(Task operation) { try { await operation; } catch { } }
    private static async Task Within(Task operation)
    {
        using var watchdog = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token);
        try { if (await Task.WhenAny(operation, delay) != operation) throw new TimeoutException("The owned fake operation did not finish."); await operation; }
        finally { watchdog.Cancel(); try { await delay; } catch (OperationCanceledException) when (watchdog.IsCancellationRequested) { } }
    }
    private static async Task<T> Within<T>(Task<T> operation) { await Within((Task)operation); return await operation; }
    private sealed record Captured(string Uri, string ApiKey, string Beta, JsonElement Body);
    private sealed class CountHandler(Func<Captured, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<Captured> CountRequests { get; } = [];
        public int BorrowedProbes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/borrowed-client-probe") { BorrowedProbes++; return new(HttpStatusCode.OK); }
            var body = Json(await request.Content!.ReadAsStringAsync());
            var captured = new Captured(request.RequestUri.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(",", keys) : "",
                request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : "", body);
            CountRequests.Add(captured); return await reply(captured, cancellationToken);
        }
    }
    private sealed class OwnedContent(string value) : StringContent(value, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
