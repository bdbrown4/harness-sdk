using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerProtocolTests
{
    [Fact]
    public async Task EmptyLegacyReasoningFallsBackToTheVllmReasoningField()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"reasoning_content":"","reasoning":"plan"},"finish_reason":"stop"}]}"""));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        Assert.Equal("plan", string.Concat((await CollectAsync(model)).OfType<ModelContentBlockDeltaEvent>().Select(value => value.Reasoning)));
    }

    [Fact]
    public async Task NullEnvelopeUsageFallsBackToChoiceLocalUsage()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"usage":null,"choices":[{"delta":{},"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6},"finish_reason":"stop"}]}"""));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        Assert.Equal(new Usage(4, 2, ReportedTotalTokens: 6), Assert.Single((await CollectAsync(model)).OfType<ModelMetadataEvent>()).Usage);
    }

    [Fact]
    public async Task CumulativeUsageIsEmittedAsAdditiveDeltas()
    {
        var first = """{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1,"total_tokens":6}}""";
        var last = """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""";
        using var wire = Wire(Encoding.UTF8.GetBytes(first + "\n" + last + "\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        Assert.Equal(new Usage(5, 2, ReportedTotalTokens: 7), (await CollectAsync(model)).OfType<ModelMetadataEvent>().Aggregate(new Usage(), (total, value) => total + value.Usage));
    }

    [Fact]
    public async Task NonStreamingUsageFollowsTheMessageStop()
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""));
        using var client = new StubClient(body); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", Payload = new() { Stream = false } });
        var events = await CollectAsync(model);
        Assert.IsType<ModelMetadataEvent>(events[events.Count - 1]);
        Assert.IsType<ModelMessageStopEvent>(events[events.Count - 2]);
    }

    [Fact]
    public async Task UnknownAwsEndpointArgumentsFailBeforeConfigurationIsPublished()
    {
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "original" });
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { AdditionalArguments = JsonSerializer.SerializeToElement(new { unknown = "x" }) }));
        Assert.Equal("original", model.GetConfig().EndpointName); Assert.Null(model.GetConfig().AdditionalArguments);
    }

    [Theory]
    [InlineData("{\"choices\":[],\"choices\":[]}")]
    [InlineData("null")]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}")]
    public async Task InvalidOrUnfinishedContainerProtocolNeverProducesAnAcceptedCompletion(string json)
    {
        using var wire = Wire(Encoding.UTF8.GetBytes(json)); using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task RequestedCancellationBeforeIoRetainsTheCallerToken()
    {
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" }); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Empty(client.StreamRequests);
    }

    [Fact]
    public async Task ToolArgumentBufferBoundsApplyAcrossSmallPayloadParts()
    {
        var chunks = new List<byte[]> { Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call","function":{"name":"lookup","arguments":"{\"q\":\""}}]},"finish_reason":null}]}""" + "\n") };
        for (var index = 0; index < 20; index++) chunks.Add(Encoding.UTF8.GetBytes("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"" + new string('a', 30) + "\"}}]},\"finish_reason\":null}]}\n"));
        chunks.Add(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"}"}}]},"finish_reason":"tool_calls"}]}""" + "\n"));
        using var wire = Wire(chunks.ToArray()); using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", MaximumResponseBytes = 256 });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }

    [Theory]
    [InlineData("{\"content\":\"late\"}")]
    [InlineData("{\"reasoning\":\"late\"}")]
    [InlineData("{\"tool_calls\":[{\"id\":\"late\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{}\"}}]}")]
    public async Task PostFinishOutputCannotBeSilentlyDiscarded(string delta)
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("{\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n{\"choices\":[{\"delta\":" + delta + ",\"finish_reason\":null}]}\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task EmptyPostFinishDeltaMayCarryTheFinalUsage()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""" + "\n" + """{"choices":[{"delta":{},"finish_reason":null}],"usage":{"prompt_tokens":1,"completion_tokens":2}}""" + "\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var events = await CollectAsync(model);
        Assert.Equal("ok", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal(new Usage(1, 2), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
    }

    [Fact]
    public async Task UsageOverflowIsAProtocolErrorAndStillReleasesTheResponse()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":9223372036854775807,"completion_tokens":1}}"""));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task OneLargeAwsPayloadCanContainManyIndividuallyBoundedEvents()
    {
        var parts = Enumerable.Repeat("{\"choices\":[{\"delta\":{\"content\":\"x\"},\"finish_reason\":null}]}\n", 300);
        using var wire = Wire(Encoding.UTF8.GetBytes(string.Concat(parts) + "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", MaximumResponseBytes = 128 });
        Assert.Equal(new string('x', 300), string.Concat((await CollectAsync(model)).OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
    }
}
