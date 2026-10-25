using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockToolResultTurnRecoveryTests(ITestOutputHelper output)
{
    private const string RejectedTurn = "Conversation blocks and tool result blocks cannot be provided in the same turn.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EligibleAdjacentResultTurnRetriesOnceAndRemembersFormatting(bool streaming)
    {
        await using var server = new BedrockRecoveryTests.RecoveryServer(streaming, error: true, requests: 3, errorMessage: RejectedTurn);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "fixture-model",
            Streaming = streaming,
            RequestTimeout = TimeSpan.FromSeconds(15),
            RecoverToolResultTurns = true,
        });
        var input = JsonSerializer.SerializeToElement(new { item = 7 });
        var messages = new List<Message>
        {
            Message.User("Look up item seven."),
            new(Role.Assistant, [new ToolUseBlock("history-7", "historical_lookup", input)]),
            new(Role.User, [new ToolResultBlock("history-7", "saved result")]),
            Message.User("Summarize the saved result. 雪")
        };
        var request = new ModelRequest(messages);
        try { Assert.Equal("summary", await CollectAsync(model, request)); }
        catch (ModelException failure)
        {
            var service = failure.InnerException as AmazonServiceException;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Phase = "official-turn-error-shape",
                Streaming = streaming,
                OuterType = failure.GetType().FullName,
                CauseType = failure.InnerException?.GetType().FullName,
                ErrorCode = service?.ErrorCode,
                Message = service?.Message,
                StatusCode = (int?)service?.StatusCode,
                WireRequests = server.Bodies.Count,
            }));
            var validation = Assert.IsType<Amazon.BedrockRuntime.Model.ValidationException>(failure.InnerException);
            Assert.Equal("ValidationException", validation.ErrorCode);
            Assert.Equal(RejectedTurn, validation.Message);
            throw;
        }
        Assert.Equal("summary", await CollectAsync(model, request));
        await server.Completion;
        Assert.Equal(3, server.Bodies.Count);
        var payloads = server.Bodies.Select(body => JsonSerializer.Deserialize<JsonElement>(body)).ToArray();
        var initial = payloads[0].GetProperty("messages");
        Assert.Equal(4, initial.GetArrayLength());
        Assert.Equal("user", initial[2].GetProperty("role").GetString());
        Assert.Equal("user", initial[3].GetProperty("role").GetString());
        foreach (var payload in payloads.Skip(1))
        {
            var repaired = payload.GetProperty("messages");
            Assert.Equal(5, repaired.GetArrayLength());
            for (var index = 0; index < 3; index++) Assert.Equal(initial[index].GetRawText(), repaired[index].GetRawText());
            Assert.Equal("assistant", repaired[3].GetProperty("role").GetString());
            Assert.Equal("Tool result received.", Assert.Single(repaired[3].GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
            Assert.Equal(initial[3].GetRawText(), repaired[4].GetRawText());
            Assert.Equal(payloads[0].GetProperty("toolConfig").GetRawText(), payload.GetProperty("toolConfig").GetRawText());
        }
        Assert.Equal(4, messages.Count);
        Assert.Single(messages[2].Content);
        Assert.IsType<ToolResultBlock>(messages[2].Content[0]);
        Assert.True(server.Signed);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "turn-recovery-wire", Streaming = streaming, Requests = payloads, SourceMessagesUnchanged = true }));
    }

    private static async Task<string> CollectAsync(IModel model, ModelRequest request)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }
}
