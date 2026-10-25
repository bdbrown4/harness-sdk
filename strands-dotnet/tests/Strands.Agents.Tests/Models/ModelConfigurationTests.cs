using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ModelConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResponsesGoogleAndOllamaUpdatesApplyAtomicallyToFutureHttpRequests(int provider)
    {
        using var handler = new RejectingHandler();
        using var client = new HttpClient(handler);
        using var native = JsonDocument.Parse(provider == 1 ? "{\"temperature\":0.3,\"maxOutputTokens\":10}" : "{\"temperature\":0.3}");
        IModel model;
        Action update;
        if (provider == 0)
        {
            var value = new OpenAIResponsesModel(client, new("old", "key") { MaxTokens = 10, Params = native.RootElement });
            model = value;
            update = () => value.UpdateConfig(options => options with { ModelId = "new", MaxTokens = 20 });
        }
        else if (provider == 1)
        {
            var value = new GoogleModel(client, new("old", "key") { GenerationConfig = native.RootElement });
            model = value;
            update = () => value.UpdateConfig(options => options with { ModelId = "new", GenerationConfig = JsonSerializer.SerializeToElement(new { temperature = options.GenerationConfig!.Value.GetProperty("temperature").GetDouble(), maxOutputTokens = 20 }) });
        }
        else
        {
            var value = new OllamaModel(client, new("old") { MaxTokens = 10, Options = native.RootElement });
            model = value;
            update = () => value.UpdateConfig(options => options with { ModelId = "new", MaxTokens = 20 });
        }
        native.Dispose();
        handler.DuringFirstSend = update;

        for (var call = 0; call < 2; call++)
            await Assert.ThrowsAsync<ModelThrottledException>(async () => { await foreach (var _ in model.StreamAsync(new([Message.User("question")]))) { } });

        if (provider == 1)
        {
            Assert.Contains("models/old:", handler.Endpoints[0].AbsoluteUri, StringComparison.Ordinal);
            Assert.Contains("models/new:", handler.Endpoints[1].AbsoluteUri, StringComparison.Ordinal);
        }
        else Assert.Equal(new[] { "old", "new" }, handler.Requests.Select(value => value.GetProperty("model").GetString()));
        var limits = handler.Requests.Select(value => provider switch
        {
            0 => value.GetProperty("max_output_tokens").GetInt32(),
            1 => value.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32(),
            _ => value.GetProperty("options").GetProperty("num_predict").GetInt32()
        });
        Assert.Equal(new[] { 10, 20 }, limits);
        Assert.Equal("new", ((IModelMetadataProvider)model).Metadata.ModelId);
        Assert.Equal(20, ((IModelMetadataProvider)model).Metadata.MaxOutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatAndMessagesUpdatesPreserveActiveRequestsAndDetachConfiguration(bool anthropic)
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var parameters = JsonDocument.Parse("{\"seed\":17}");
        IModel model = anthropic
            ? new AnthropicModel(client, new("old", "first-key") { MaxTokens = 10, Params = parameters.RootElement })
            : new OpenAIModel(client, new("old", "first-key") { MaxTokens = 10, Params = parameters.RootElement });
        parameters.Dispose();
        await using (var active = model.StreamAsync(new([Message.User("first")])).GetAsyncEnumerator())
        {
            Assert.True(await active.MoveNextAsync());
            if (model is AnthropicModel a) a.UpdateConfig(options => options with { ModelId = "new", ApiKey = "second-key", MaxTokens = 20 });
            else ((OpenAIModel)model).UpdateConfig(options => options with { ModelId = "new", ApiKey = "second-key", MaxTokens = 20 });
            while (await active.MoveNextAsync()) { }
        }
        await foreach (var _ in model.StreamAsync(new([Message.User("second")]))) { }

        Assert.Equal(new[] { "old", "new" }, handler.Requests.Select(value => value.GetProperty("model").GetString()));
        Assert.Equal(new[] { 10, 20 }, handler.Requests.Select(value => value.GetProperty(anthropic ? "max_tokens" : "max_completion_tokens").GetInt32()));
        Assert.All(handler.Requests, value => Assert.Equal(17, value.GetProperty("seed").GetInt32()));
        Assert.Equal(new[] { "first-key", "second-key" }, handler.Credentials);
        Assert.Equal("new", ((IModelMetadataProvider)model).Metadata.ModelId);
        Assert.Equal(20, ((IModelMetadataProvider)model).Metadata.MaxOutputTokens);
    }

    [Fact]
    public void ResponsesHistoryOwnershipIsStableAndAnInvalidPolicyChangePublishesNothing()
    {
        using var client = new HttpClient();
        var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true });

        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Stateful = false, ModelId = "invalid" }));

        Assert.True(model.Stateful);
        Assert.True(model.GetConfig().Stateful);
        Assert.Equal("model", model.Metadata.ModelId);
    }

    [Fact]
    public void RepeatedUpdatesEvaluateCallbacksOnceAndThrownCallbacksRetainTheirIdentity()
    {
        using var client = new HttpClient();
        var model = new OpenAIModel(client, new("model", "key") { MaxTokens = 10 });
        var calls = 0;
        for (var index = 0; index < 10; index++) model.UpdateConfig(options => { calls++; return options with { MaxTokens = options.MaxTokens + 1 }; });
        var failure = new FormatException("callback failed");

        Assert.Same(failure, Assert.Throws<FormatException>(() => model.UpdateConfig(_ => throw failure)));

        Assert.Equal(10, calls);
        Assert.Equal(20, model.GetConfig().MaxTokens);
        Assert.Throws<ArgumentNullException>(() => model.UpdateConfig(null!));
        Assert.Throws<ArgumentNullException>(() => model.UpdateConfig(_ => null!));
        Assert.Equal(20, model.Metadata.MaxOutputTokens);
    }

    [Fact]
    public void AnthropicSnapshotsDetachNativeListsAndInvalidUpdatesPublishNothing()
    {
        using var client = new HttpClient();
        using var serverTool = JsonDocument.Parse("{\"type\":\"web_search_20250305\",\"name\":\"web_search\"}");
        var tools = new List<JsonElement> { serverTool.RootElement };
        var betas = new List<string> { "first-beta" };
        var stops = new List<string> { "END" };
        var model = new AnthropicModel(client, new("model", "sensitive-key") { AnthropicTools = tools, Betas = betas, StopSequences = stops });
        tools.Clear(); betas.Clear(); stops.Clear(); serverTool.Dispose();
        var before = model.GetConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxTokens = -1 }));

        Assert.Equal("web_search", Assert.Single(before.AnthropicTools).GetProperty("name").GetString());
        Assert.Equal("first-beta", Assert.Single(before.Betas));
        Assert.Equal("END", Assert.Single(before.StopSequences!));
        Assert.Equal(64000, model.GetConfig().MaxTokens);
        Assert.DoesNotContain("sensitive-key", before.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)before.Betas).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationSnapshotsSerializeWithStandardWebJsonOptions(bool writer)
    {
        using var client = new HttpClient();
        object options = writer ? new WriterModel(client, new("model", "key")).GetConfig() : new MistralModel(client, new("model", "key")).GetConfig();

        var json = JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("model", json.GetProperty("modelId").GetString());
        Assert.Equal("key", json.GetProperty("apiKey").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdatesApplyToFutureRequestsAndMetadataWhileEnteredStreamsRetainTheirSnapshot(bool writer)
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var model = writer ? (IModel)new WriterModel(client, new("old", "first-key") { MaxTokens = 10 }) : new MistralModel(client, new("old", "first-key") { MaxTokens = 10 });
        await using (var active = model.StreamAsync(new([Message.User("first")])).GetAsyncEnumerator())
        {
            Assert.True(await active.MoveNextAsync());
            if (model is WriterModel w) w.UpdateConfig(options => options with { ModelId = "new", ApiKey = "second-key", MaxTokens = 20 });
            else ((MistralModel)model).UpdateConfig(options => options with { ModelId = "new", ApiKey = "second-key", MaxTokens = 20 });
            while (await active.MoveNextAsync()) { }
        }
        await foreach (var _ in model.StreamAsync(new([Message.User("second")]))) { }

        Assert.Equal("old", handler.Requests[0].GetProperty("model").GetString());
        Assert.Equal(10, handler.Requests[0].GetProperty("max_tokens").GetInt32());
        Assert.Equal("new", handler.Requests[1].GetProperty("model").GetString());
        Assert.Equal(20, handler.Requests[1].GetProperty("max_tokens").GetInt32());
        Assert.Equal(new[] { "first-key", "second-key" }, handler.Credentials);
        Assert.Equal("new", ((IModelMetadataProvider)model).Metadata.ModelId);
        Assert.Equal(20, ((IModelMetadataProvider)model).Metadata.MaxOutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUpdatesLeaveThePriorConfigurationUnchangedAndDiagnosticsOmitCredentials(bool writer)
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        const string credential = "sensitive-credential-value";
        if (writer)
        {
            var model = new WriterModel(client, new("original", credential));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxTokens = -1 }));
            Assert.Equal("original", model.GetConfig().ModelId);
            Assert.DoesNotContain(credential, model.GetConfig().ToString(), StringComparison.Ordinal);
        }
        else
        {
            var model = new MistralModel(client, new("original", credential));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxTokens = -1 }));
            Assert.Equal("original", model.GetConfig().ModelId);
            Assert.DoesNotContain(credential, model.GetConfig().ToString(), StringComparison.Ordinal);
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void WriterSnapshotsDetachStopSequencesBeforeAndAfterUpdates()
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var original = new List<string> { "first" };
        var model = new WriterModel(client, new("palmyra-x4", "key") { Stop = original });
        original.Clear();
        var before = model.GetConfig();
        var replacement = new List<string> { "second" };

        model.UpdateConfig(options => options with { Stop = replacement });
        replacement.Clear();

        Assert.Equal("first", Assert.Single(before.Stop!));
        Assert.Equal("second", Assert.Single(model.GetConfig().Stop!));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)model.GetConfig().Stop!).Clear());
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<JsonElement> Requests { get; } = [];
        internal List<string?> Credentials { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Requests.Add(parsed.RootElement.Clone());
            Credentials.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : request.Headers.Authorization?.Parameter);
            var wire = request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                ? "data: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"usage\":{\"input_tokens\":1}}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var content = new StringContent(wire, Encoding.UTF8, "text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal Action? DuringFirstSend { get; set; }
        internal List<JsonElement> Requests { get; } = [];
        internal List<Uri> Endpoints { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Requests.Add(parsed.RootElement.Clone());
            Endpoints.Add(request.RequestUri!);
            if (Requests.Count == 1) DuringFirstSend?.Invoke();
            return new((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"message\":\"busy\",\"status\":\"RESOURCE_EXHAUSTED\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
