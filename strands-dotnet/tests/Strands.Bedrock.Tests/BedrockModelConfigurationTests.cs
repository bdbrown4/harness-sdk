using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using static Strands.Bedrock.Tests.BedrockModelTests;

namespace Strands.Bedrock.Tests;

public sealed class BedrockModelConfigurationTests
{
    [Fact]
    public async Task UpdatesAffectFutureRequestsAndMetadataWhileAnEnteredStreamKeepsItsOptions()
    {
        using var first = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var second = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(first, second);
        using var model = new BedrockModel(client, new() { ModelId = "before", MaxTokens = 17 });
        var configurable = Assert.IsAssignableFrom<IConfigurableModel<BedrockModelOptions>>(model);
        await using var stream = model.StreamAsync(new([Message.User("hello")])).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());

        configurable.UpdateConfig(options => options with { ModelId = "after", MaxTokens = 31, Temperature = 0.25 });

        Assert.Equal("after", model.ModelId);
        Assert.Equal("after", model.Metadata.ModelId);
        Assert.Equal(31, model.Metadata.MaxOutputTokens);
        while (await stream.MoveNextAsync()) { }
        await CollectAsync(model, new([Message.User("next")]));
        Assert.Equal(new[] { "before", "after" }, client.Requests.Select(request => request.ModelId));
        Assert.Equal(17, client.Requests[0].InferenceConfig.MaxTokens);
        Assert.Equal(31, client.Requests[1].InferenceConfig.MaxTokens);
        Assert.Equal(0.25F, client.Requests[1].InferenceConfig.Temperature);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task PublishedOptionsDetachJsonAndListsFromCallerOwnedInputs()
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new());
        var configurable = Assert.IsAssignableFrom<IConfigurableModel<BedrockModelOptions>>(model);
        var stops = new List<string> { "done" };
        using (var document = JsonDocument.Parse("""{"top_k":8}"""))
            configurable.UpdateConfig(options => options with { StopSequences = stops, AdditionalModelRequestFields = document.RootElement });
        stops[0] = "mutated";

        var snapshot = configurable.GetConfig();
        Assert.Equal(8, snapshot.AdditionalModelRequestFields!.Value.GetProperty("top_k").GetInt32());
        Assert.Equal("done", Assert.Single(snapshot.StopSequences!));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.StopSequences!)[0] = "changed");
        await CollectAsync(model, new([Message.User("hello")]));
        Assert.Equal("done", Assert.Single(Assert.Single(client.Requests).InferenceConfig.StopSequences));
    }

    [Fact]
    public void InvalidUpdatesAndCallbackFailuresLeaveThePublishedConfigurationUntouched()
    {
        using var client = new StubClient();
        using var model = new BedrockModel(client, new() { ModelId = "initial" });
        var configurable = Assert.IsAssignableFrom<IConfigurableModel<BedrockModelOptions>>(model);
        var calls = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => configurable.UpdateConfig(options => { calls++; return options with { MaxTokens = 0 }; }));
        var failure = new InvalidOperationException("update callback");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => configurable.UpdateConfig(_ => throw failure)));
        Assert.Throws<ArgumentNullException>(() => configurable.UpdateConfig(_ => null!));
        Assert.Throws<ArgumentNullException>(() => configurable.UpdateConfig(null!));
        Assert.Equal(1, calls);
        Assert.Equal("initial", configurable.GetConfig().ModelId);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void UpdatesPreserveConstructionTimeTransportAndRespectDisposal()
    {
        using var client = new StubClient();
        var model = new BedrockModel(client, new() { Region = "us-east-1" });
        var configurable = Assert.IsAssignableFrom<IConfigurableModel<BedrockModelOptions>>(model);
        Assert.Throws<ArgumentException>(() => configurable.UpdateConfig(options => options with { Region = "us-west-2" }));
        Assert.Equal("us-east-1", model.Region);
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => configurable.UpdateConfig(options => options with { MaxTokens = 10 }));
        Assert.False(client.Disposed);
    }
}
