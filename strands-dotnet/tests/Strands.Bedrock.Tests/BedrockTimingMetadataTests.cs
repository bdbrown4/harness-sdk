using System.Globalization;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockTimingMetadataTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(345L)]
    public async Task NativeEventStreamPreservesReportedMillisecondLatency(long milliseconds)
    {
        var events = await CollectAsync("{\"latencyMs\":" + milliseconds.ToString(CultureInfo.InvariantCulture) + "}");
        var metadata = Assert.Single(events.OfType<ModelMetadataEvent>());
        Assert.Equal(new Usage(1, 2, ReportedTotalTokens: 3), metadata.Usage);
        Assert.NotNull(metadata.PerformanceMetrics);
        Assert.Equal(TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond), metadata.PerformanceMetrics.Latency);
        Assert.Null(metadata.PerformanceMetrics.TimeToFirstToken);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task InvalidLatencyFailsAsAProviderProtocolError(long milliseconds)
    {
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync("{\"latencyMs\":" + milliseconds.ToString(CultureInfo.InvariantCulture) + "}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task OmittedLatencyRemainsUnknownInsteadOfBecomingZero(string? metrics)
    {
        var metadata = Assert.Single((await CollectAsync(metrics)).OfType<ModelMetadataEvent>());
        Assert.Null(metadata.PerformanceMetrics);
        Assert.Equal(3, metadata.Usage.TotalTokens);
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(string? metrics)
    {
        var metadata = "{\"usage\":{\"inputTokens\":1,\"outputTokens\":2,\"totalTokens\":3}" +
            (metrics is null ? "" : ",\"metrics\":" + metrics) + "}";
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""), ("metadata", metadata));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        return await BedrockModelTests.CollectAsync(model, new([Message.User("timing")]));
    }
}
