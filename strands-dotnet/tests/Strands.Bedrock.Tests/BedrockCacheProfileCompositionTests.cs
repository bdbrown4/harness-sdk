using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using static Strands.Bedrock.Tests.BedrockCacheProfileConformanceTests;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;
using ValidationException = Amazon.BedrockRuntime.Model.ValidationException;

namespace Strands.Bedrock.Tests;

public sealed class BedrockCacheProfileCompositionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InFlightOptionsRemainDetachedWhileUpdatesAffectOnlyFutureAdmissions()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply())
        { BeforeReply = index => { if (index != 0) return Task.CompletedTask; entered.TrySetResult(true); return release.Task; } };
        using var client = wire.CreateClient();
        var supplied = new BedrockCacheOptions { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", SystemPromptTtl = "5m" };
        using var model = new BedrockModel(client, Options(supplied));
        Assert.NotSame(supplied, model.GetConfig().Cache);
        Assert.NotSame(model.GetConfig().Cache, model.GetConfig().Cache);
        var pending = CollectAsync(model, Request());
        Exception? primary = null;
        try
        {
            await Ready(entered.Task);
            model.UpdateConfig(value => value with { ModelId = "new-model", Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "5m", Tools = false } });
            release.TrySetResult(true);
            Assert.Equal("summary", await pending);
            Assert.Equal("summary", await CollectAsync(model, Request()));
            await wire.Completion;
            var old = wire.Requests[0].Body;
            var current = wire.Requests[1].Body;
            Assert.Contains("opaque-model", wire.Requests[0].Target);
            Assert.Contains("new-model", wire.Requests[1].Target);
            AssertTtl(Assert.Single(Points(old.GetProperty("toolConfig").GetProperty("tools"))), "1h");
            AssertTtl(Assert.Single(Points(old.GetProperty("system"))), "5m");
            Assert.Empty(Points(current.GetProperty("toolConfig").GetProperty("tools")));
            AssertTtl(Assert.Single(Points(current.GetProperty("system"))), "5m");
            Assert.Equal("1h", supplied.Ttl);
            Emit(wire, "admitted-config");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await pending; }
            catch (Exception error) when (primary is not null) { primary.Data["FixtureJoinError"] = error; }
        }
    }

    [Fact]
    public async Task ClearingProfileRestoresLegacyFlagsWithoutTakingBorrowedClientOwnership()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using (var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Tools = false, SystemPrompt = false, Messages = false }) with
        { CacheTools = true, CacheSystemPrompt = true, CacheLastMessage = true, CacheTtl = "1h" }))
        {
            Assert.Equal("summary", await CollectAsync(model, Request()));
            model.UpdateConfig(value => value with { Cache = null });
            Assert.Equal("summary", await CollectAsync(model, Request()));
        }
        using var borrowedAgain = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "5m" }));
        Assert.Equal("summary", await CollectAsync(borrowedAgain, Request()));
        await wire.Completion;
        Assert.Empty(Points(wire.Requests[0].Body.GetProperty("system")));
        AssertTtl(Assert.Single(Points(wire.Requests[1].Body.GetProperty("system"))), "1h");
        AssertTtl(Assert.Single(Points(wire.Requests[2].Body.GetProperty("system"))), "5m");
        Emit(wire, "clear-profile-borrowed-reuse");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledMessageManagementKeepsAllExplicitCallerBoundaries(bool supported)
    {
        var firstPoint = new CachePointBlock("original-first");
        var secondPoint = new CachePointBlock("original-second");
        var messages = new[] { new Message(Role.User, [new TextBlock("old"), firstPoint]),
            new Message(Role.User, [new TextBlock("current"), secondPoint, new TextBlock("suffix"), new CachePointBlock("third")]) };
        await using var wire = new BedrockRecoveryWireFixture(new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = supported ? BedrockCacheStrategy.Anthropic : BedrockCacheStrategy.Auto, Messages = !supported }));
        Assert.Equal("summary", await CollectAsync(model, new(messages)));
        await wire.Completion;
        var mapped = Assert.Single(wire.Requests).Body.GetProperty("messages");
        AssertTtl(Assert.Single(Points(mapped[0].GetProperty("content"))), "original-first");
        Assert.Equal(new[] { "original-second", "third" }, Points(mapped[1].GetProperty("content")).Select(point => point.GetProperty("ttl").GetString()));
        Assert.Same(firstPoint, messages[0].Content[1]);
        Assert.Same(secondPoint, messages[1].Content[1]);
        Emit(wire, "caller-boundaries");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedRecoveryPreservesProfileGraphAndOriginalHistoryAcrossFourAttempts(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, BedrockRecoveryConformanceTests.Options(streaming) with
        { Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", SystemPromptTtl = "5m", MessagesTtl = "5m" } });
        var request = BedrockRecoveryConformanceTests.Turns() with { SystemPrompt = "instructions" };
        Assert.Equal("summary", await CollectAsync(model, request));
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        Assert.Equal(new[] { false, true, false, true, true }, wire.Requests.Select(value => BedrockRecoveryConformanceTests.HasDropBlock(value.Body)));
        Assert.Equal(new[] { 4, 4, 5, 5, 5 }, wire.Requests.Select(value => value.Body.GetProperty("messages").GetArrayLength()));
        var originalTail = wire.Requests[0].Body.GetProperty("messages")[3].GetRawText();
        foreach (var sent in wire.Requests)
        {
            Assert.True(sent.Signed);
            AssertTtl(Assert.Single(Points(sent.Body.GetProperty("system"))), "5m");
            AssertTtl(Assert.Single(Points(sent.Body.GetProperty("toolConfig").GetProperty("tools"))), "1h");
            var messages = sent.Body.GetProperty("messages");
            Assert.Equal(originalTail, messages[messages.GetArrayLength() - 1].GetRawText());
            AssertTtl(Assert.Single(Points(messages[messages.GetArrayLength() - 1].GetProperty("content"))), "5m");
        }
        Assert.Equal(4, request.Messages.Count);
        Assert.Empty(request.Messages.SelectMany(message => message.Content).OfType<CachePointBlock>());
        Emit(wire, "nested-recovery");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedCacheComposesWithReasoningNovaAndExplicitNoToolsAuthority(bool streaming)
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{\"value\":1.00}");
        var result = new Strands.Agents.ToolResultBlock("u", "", Strands.Agents.ToolResultStatus.Error)
        { ContentBlocks = [new JsonBlock(json)], StructuredContent = json };
        var request = new ModelRequest([Message.User("start"), new(Role.Assistant, [new ReasoningBlock("rejected"),
            new Strands.Agents.ToolUseBlock("u", "historical", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [result])], "instructions")
        { ToolChoice = new(ToolChoiceMode.None) };
        await using var wire = new BedrockRecoveryWireFixture(new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "5m" }) with
        { Streaming = streaming, ModelId = "amazon.nova-pro", FilterRejectedReasoningContent = true, ConvertNovaToolResultJsonToText = true, IncludeToolResultStatus = true });
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var body = Assert.Single(wire.Requests).Body;
        Assert.False(body.TryGetProperty("toolConfig", out _));
        var assistant = body.GetProperty("messages")[1].GetProperty("content");
        Assert.Single(assistant.EnumerateArray());
        Assert.True(assistant[0].TryGetProperty("toolUse", out _));
        var mapped = body.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("toolResult");
        Assert.Equal("error", mapped.GetProperty("status").GetString());
        Assert.Equal(new[] { json.GetRawText(), json.GetRawText() }, mapped.GetProperty("content").EnumerateArray().Select(block => block.GetProperty("text").GetString()));
        AssertTtl(Assert.Single(Points(body.GetProperty("messages")[2].GetProperty("content"))), "5m");
        Assert.Equal(2, request.Messages[1].Content.Count);
        Assert.Equal(JsonValueKind.Number, result.StructuredContent!.Value.GetProperty("value").ValueKind);
        Emit(wire, "reasoning-nova-no-tools");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownDurationRemainsServiceValidatedWithoutRecoveryRetry(bool streaming)
    {
        const string message = "The cache TTL vendor-duration is invalid.";
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: message));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "vendor-duration" }) with
        { Streaming = streaming, RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Request()));
        Assert.Equal(message, Assert.IsType<ValidationException>(error.InnerException).Message);
        await wire.Completion;
        AssertTtl(Assert.Single(Points(Assert.Single(wire.Requests).Body.GetProperty("system"))), "vendor-duration");
        Emit(wire, "service-ttl-rejection");
    }

    [Fact]
    public async Task CanceledAdmissionPerformsNoIoAndPreservesOriginalCallerToken()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, Request(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    private static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }
    private void Emit(BedrockRecoveryWireFixture wire, string scenario) => output.WriteLine(JsonSerializer.Serialize(new
    { Phase = "b06-composition-wire", Scenario = scenario, Requests = wire.Requests }));
}
