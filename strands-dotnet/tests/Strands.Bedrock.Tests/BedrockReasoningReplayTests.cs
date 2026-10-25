using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockReasoningReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OfficialWireFiltersUnsignedClaudeReplayOnlyWhenEnabled(bool streaming, bool filtering)
    {
        await using var server = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "global.anthropic.claude-sonnet-4-6",
            Streaming = streaming,
            FilterRejectedReasoningContent = filtering,
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        var opaque = Convert.ToBase64String(Encoding.UTF8.GetBytes("opaque 雪"));
        ContentBlock[] content = [new ReasoningBlock("unsigned"), new ReasoningBlock("signed", "sig-snow-雪"),
            new RedactedReasoningBlock(opaque), new TextBlock("answer")];
        var request = new ModelRequest([Message.User("hello"), new Message(Role.Assistant, content)]);
        var result = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } text }) result.Append(text);
        await server.Completion;
        Assert.Equal("summary", result.ToString());
        var sent = Assert.Single(server.Requests);
        var projected = sent.Body.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
        var reasoning = projected.Where(block => block.TryGetProperty("reasoningContent", out var value) && value.TryGetProperty("reasoningText", out _))
            .Select(block => block.GetProperty("reasoningContent").GetProperty("reasoningText")).ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = "b03-outbound-wire",
            Streaming = streaming,
            Filtering = filtering,
            sent.Signed,
            RequestCount = server.Requests.Length,
            sent.Target,
            Body = sent.Body,
            ReasoningTexts = reasoning.Select(block => block.GetProperty("text").GetString()).ToArray()
        }));
        Assert.True(sent.Signed);
        Assert.EndsWith(streaming ? "/converse-stream" : "/converse", sent.Target);
        Assert.Equal(filtering ? new[] { "signed" } : new[] { "unsigned", "signed" }, reasoning.Select(block => block.GetProperty("text").GetString()));
        var signed = Assert.Single(reasoning, block => block.GetProperty("text").GetString() == "signed");
        Assert.Equal("sig-snow-雪", signed.GetProperty("signature").GetString());
        Assert.Equal(opaque, Assert.Single(projected, block => block.TryGetProperty("reasoningContent", out var value) && value.TryGetProperty("redactedContent", out _))
            .GetProperty("reasoningContent").GetProperty("redactedContent").GetString());
        Assert.Equal("answer", projected.Last().GetProperty("text").GetString());
        Assert.Equal(4, content.Length);
        Assert.Equal("unsigned", Assert.IsType<ReasoningBlock>(content[0]).Text);
        Assert.Equal("sig-snow-雪", Assert.IsType<ReasoningBlock>(content[1]).Signature);
        Assert.Equal(opaque, Assert.IsType<RedactedReasoningBlock>(content[2]).Data);
    }
}
