using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockOpaqueReasoningProgressTests
{
    [Fact]
    public async Task OpaqueChunksProducePublicDeltasBeforeTheUnclosedStreamFails()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQ=="}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AgM="}}}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        var observed = new List<ModelStreamEvent>();

        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var item in model.StreamAsync(new([Message.User("inspect opaque progress")])))
                observed.Add(item);
        });

        Assert.Contains("messageStop", error.Message);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
        Assert.Single(client.Requests);
        Assert.Single(observed.OfType<ModelMessageStartEvent>());
        Assert.Empty(observed.OfType<ModelContentBlockStopEvent>());
        Assert.Empty(observed.OfType<ModelMessageStopEvent>());
        var chunks = observed.OfType<ModelContentBlockDeltaEvent>().ToArray();
        Assert.Equal(2, chunks.Length);
        Assert.All(chunks, chunk => Assert.Equal(0, chunk.Index));
    }

    [Fact]
    public async Task CompletedOpaqueChunksRemainOneLosslessAgentBlock()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQ=="}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AgM="}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"BAU="}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        await using var agent = new Agent(new AgentOptions { Model = model });

        var result = await agent.InvokeAsync("retain opaque bytes");

        var block = Assert.IsType<RedactedReasoningBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Convert.FromBase64String(block.Data));
        Assert.Equal("AQIDBAU=", block.Data);
        Assert.Equal(block.Data, Assert.IsType<RedactedReasoningBlock>(Assert.Single(agent.Messages.Last().Content)).Data);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task OpaqueAndSignedReasoningInOneNativeDeltaIsRejected()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQ==","text":"conflict"}}}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Message.User("reject mixed reasoning")])));

        Assert.Contains("mixed opaque", error.Message);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
        Assert.Single(client.Requests);
    }
}
