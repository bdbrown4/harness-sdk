using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using static Strands.Bedrock.Tests.BedrockSystemPromptConformanceTests;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;

namespace Strands.Bedrock.Tests;

public sealed class BedrockSystemPromptCompositionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourAcquisitionsAndRememberedNativeCountKeepOneDetachedTypedSystemGraph(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, BedrockRecoveryConformanceTests.Options(streaming) with
        {
            ModelId = "anthropic.claude",
            FilterRejectedReasoningContent = true,
            UseNativeTokenCount = true,
            Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", SystemPromptTtl = "5m" }
        });
        var original = BedrockRecoveryConformanceTests.Turns();
        var supplied = new List<ContentBlock> { new TextBlock("instructions 雪"), new CachePointBlock(), new TextBlock("suffix") };
        var request = original with
        {
            SystemPromptContent = supplied,
            Messages = original.Messages.Select((message, index) => index == 1
                ? new Message(message.Role, [new ReasoningBlock("rejected"), new ReasoningBlock("signed", "signature"), .. message.Content]) : message).ToArray()
        };
        supplied.Clear();
        Assert.Equal("summary", await CollectAsync(model, request));
        Assert.Equal(9, await model.CountTokensAsync(request));
        await wire.Completion;
        Assert.Equal(5, wire.Requests.Length);
        var attempts = wire.Requests.Take(4).Select(value => value.Body).ToArray();
        Assert.Equal(new[] { false, true, false, true }, attempts.Select(BedrockRecoveryConformanceTests.HasDropBlock));
        Assert.Equal(new[] { 4, 4, 5, 5 }, attempts.Select(value => value.GetProperty("messages").GetArrayLength()));
        var counted = wire.Requests[4].Body.GetProperty("input").GetProperty("converse");
        foreach (var graph in attempts.Append(counted))
        {
            Assert.Equal(attempts[0].GetProperty("system").GetRawText(), graph.GetProperty("system").GetRawText());
            var system = graph.GetProperty("system");
            Assert.Equal(3, system.GetArrayLength());
            Assert.Equal("instructions 雪", system[0].GetProperty("text").GetString());
            AssertTtl(system[1].GetProperty("cachePoint"), "5m");
            Assert.Equal("suffix", system[2].GetProperty("text").GetString());
            var reasoning = graph.GetProperty("messages").EnumerateArray().SelectMany(value => value.GetProperty("content").EnumerateArray())
                .Where(value => value.TryGetProperty("reasoningContent", out _)).ToArray();
            Assert.Equal("signed", Assert.Single(reasoning).GetProperty("reasoningContent").GetProperty("reasoningText").GetProperty("text").GetString());
        }
        Assert.Equal(attempts[3].GetProperty("messages").GetRawText(), counted.GetProperty("messages").GetRawText());
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal("rejected", Assert.IsType<ReasoningBlock>(request.Messages[1].Content[0]).Text);
        Assert.Null(Assert.IsType<CachePointBlock>(request.SystemPromptContent![1]).Ttl);
        Assert.All(wire.Requests, value => Assert.True(value.Signed));
        Emit(wire, "binding-turn-binding-count-original-graph");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedSystemComposesWithNovaStatusReasoningGuardAndExplicitNoTools(bool streaming)
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"value\" : 9007199254740993, \"雪\" : \"😀\" }");
        var result = new ToolResultBlock("u", "", ToolResultStatus.Error) { ContentBlocks = [new JsonBlock(json)], StructuredContent = json };
        var request = new ModelRequest([Message.User("start"), new(Role.Assistant, [new ReasoningBlock("rejected"),
            new ToolUseBlock("u", "historical", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [result, new TextBlock("tail")])], "ignored")
        {
            SystemPromptContent = [new TextBlock("instructions"), new CachePointBlock("caller"), new TextBlock("suffix")],
            ToolChoice = new(ToolChoiceMode.None)
        };
        await using var wire = new BedrockRecoveryWireFixture(new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile() with
        {
            ModelId = "amazon.nova-pro",
            Streaming = streaming,
            FilterRejectedReasoningContent = true,
            ConvertNovaToolResultJsonToText = true,
            IncludeToolResultStatus = true,
            Guardrail = new() { Identifier = "fixture", Version = "1", GuardLatestUserMessage = true }
        });
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var body = Assert.Single(wire.Requests).Body;
        Assert.False(body.TryGetProperty("toolConfig", out _));
        Assert.Equal(3, body.GetProperty("system").GetArrayLength());
        Assert.Equal("instructions", body.GetProperty("system")[0].GetProperty("text").GetString());
        AssertTtl(body.GetProperty("system")[1].GetProperty("cachePoint"), "caller");
        Assert.Single(body.GetProperty("messages")[1].GetProperty("content").EnumerateArray());
        var content = body.GetProperty("messages")[2].GetProperty("content");
        var mapped = content[0].GetProperty("toolResult");
        Assert.Equal("error", mapped.GetProperty("status").GetString());
        Assert.Equal(new[] { json.GetRawText(), json.GetRawText() }, mapped.GetProperty("content").EnumerateArray().Select(value => value.GetProperty("text").GetString()));
        Assert.Contains(content.EnumerateArray(), value => value.TryGetProperty("guardContent", out var guard) && guard.GetProperty("text").GetProperty("text").GetString() == "tail");
        Assert.Equal(JsonValueKind.Number, result.StructuredContent!.Value.GetProperty("value").ValueKind);
        Assert.Equal(2, request.Messages[1].Content.Count);
        Emit(wire, "nova-reasoning-no-tools-guard");
    }

    [Fact]
    public async Task AdmittedTypedGraphAndProfileStayFixedWhileFutureRequestsUseUpdatedConfiguration()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply())
        { BeforeReply = index => { if (index != 0) return Task.CompletedTask; entered.TrySetResult(true); return release.Task; } };
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile());
        var supplied = new List<ContentBlock> { new TextBlock("accepted"), new CachePointBlock() };
        var request = Typed(supplied);
        var pending = CollectAsync(model, request);
        Exception? primary = null;
        try
        {
            await Ready(entered.Task);
            supplied.Clear();
            model.UpdateConfig(value => value with { ModelId = "future-model", Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "5m" } });
            release.TrySetResult(true);
            Assert.Equal("summary", await pending);
            Assert.Equal("summary", await CollectAsync(model, request));
            await wire.Completion;
            Assert.Contains("opaque-model", wire.Requests[0].Target);
            Assert.Contains("future-model", wire.Requests[1].Target);
            Assert.Equal("accepted", wire.Requests[0].Body.GetProperty("system")[0].GetProperty("text").GetString());
            Assert.Equal("accepted", wire.Requests[1].Body.GetProperty("system")[0].GetProperty("text").GetString());
            AssertTtl(wire.Requests[0].Body.GetProperty("system")[1].GetProperty("cachePoint"), "1h");
            AssertTtl(wire.Requests[1].Body.GetProperty("system")[1].GetProperty("cachePoint"), "5m");
            Assert.Null(Assert.IsType<CachePointBlock>(request.SystemPromptContent![1]).Ttl);
            Emit(wire, "admitted-profile");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await pending; }
            catch (Exception error) when (primary is not null) { primary.Data["FixtureJoinError"] = error; }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquiredMalformedResponseNeverRetriesTheTypedSystemGraph(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Malformed: true));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, BedrockRecoveryConformanceTests.Options(streaming));
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Typed([new TextBlock("instructions"), new CachePointBlock("caller")])));
        await wire.Completion;
        Assert.Equal(2, Assert.Single(wire.Requests).Body.GetProperty("system").GetArrayLength());
        Emit(wire, "acquired-malformed-no-retry");
    }

    [Fact]
    public async Task PartialStreamFailureDoesNotRetryOrChangeTypedCallerBoundaries()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(StreamFault: true)) { BeforeStreamFault = observed.Task };
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, BedrockRecoveryConformanceTests.Options(true));
        var seen = new StringBuilder();
        try
        {
            await Assert.ThrowsAsync<ModelException>(async () =>
            {
                await foreach (var item in model.StreamAsync(Typed([new TextBlock("instructions"), new CachePointBlock("caller")])))
                    if (item is ModelContentBlockDeltaEvent { Text: { } text }) { seen.Append(text); observed.TrySetResult(true); }
            });
        }
        finally { observed.TrySetResult(true); }
        Assert.Equal("partial", seen.ToString());
        await wire.Completion;
        Assert.Equal(2, Assert.Single(wire.Requests).Body.GetProperty("system").GetArrayLength());
        Emit(wire, "partial-no-retry");
    }

    [Fact]
    public async Task CanceledAdmissionKeepsTheCallerTokenAndDoesNoOfficialIo()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile());
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, Typed([new TextBlock("instructions")]), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task DisposingTheTypedModelDoesNotTakeOwnershipOfTheOfficialClient()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply());
        using var client = wire.CreateClient();
        var url = client.Config.ServiceURL;
        var timeout = client.Config.Timeout;
        using (var model = new BedrockModel(client, Profile())) Assert.Equal("summary", await CollectAsync(model, Typed([new TextBlock("instructions")])));
        var response = await client.ConverseAsync(new Amazon.BedrockRuntime.Model.ConverseRequest
        { ModelId = "direct-model", Messages = [new() { Role = "user", Content = [new() { Text = "direct" }] }] });
        Assert.Equal("summary", response.Output.Message.Content[0].Text);
        await wire.Completion;
        Assert.Equal(url, client.Config.ServiceURL);
        Assert.Equal(timeout, client.Config.Timeout);
        Assert.False(wire.Requests[1].Body.TryGetProperty("system", out _));
        Assert.All(wire.Requests, value => Assert.True(value.Signed));
        Emit(wire, "borrowed-reuse");
    }

    private static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }
    private void Emit(BedrockRecoveryWireFixture wire, string scenario) => output.WriteLine(JsonSerializer.Serialize(new
    { Phase = "b07-composition-wire", Scenario = scenario, Requests = wire.Requests }));
}
