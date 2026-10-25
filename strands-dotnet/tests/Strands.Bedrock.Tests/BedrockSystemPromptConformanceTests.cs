using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockSystemPromptConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void LegacyConstructorDeconstructAndDefaultJsonShapeRemainAvailable()
    {
        var messages = new[] { Message.User("question") };
        var tools = new[] { new ToolSpec("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" })) };
        var request = new ModelRequest(messages, "legacy 雪", tools);
        var (actualMessages, actualPrompt, actualTools) = request;
        Assert.Same(messages, actualMessages);
        Assert.Same(tools, actualTools);
        Assert.Equal("legacy 雪", actualPrompt);
        Assert.Null(request.SystemPromptContent);
        var constructor = Assert.Single(typeof(ModelRequest).GetConstructors());
        Assert.Equal(new[] { typeof(IReadOnlyList<Message>), typeof(string), typeof(IReadOnlyList<ToolSpec>) }, constructor.GetParameters().Select(value => value.ParameterType));
        Assert.Equal(3, Assert.Single(typeof(ModelRequest).GetMethods(), value => value.Name == "Deconstruct").GetParameters().Length);
        var json = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("legacy 雪", json.GetProperty("systemPrompt").GetString());
        Assert.False(json.TryGetProperty("systemPromptContent", out _));
        var restored = JsonSerializer.Deserialize<ModelRequest>(json.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(restored.SystemPromptContent);
        Assert.Equal("legacy 雪", restored.SystemPrompt);
        Assert.Equal("replacement", (request with { SystemPrompt = "replacement" }).SystemPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedWithExpressionRequiresExplicitRepresentationSwitch(bool clear)
    {
        var request = new ModelRequest([], "old") { SystemPromptContent = [new TextBlock("accepted"), new CachePointBlock("5m")] };
        var updated = request with { SystemPrompt = "replacement", SystemPromptContent = clear ? null : request.SystemPromptContent };
        Assert.Equal(clear ? "replacement" : "accepted", updated.SystemPrompt);
        Assert.Equal("accepted", request.SystemPrompt);
        Assert.Equal(clear, updated.SystemPromptContent is null);
    }

    [Fact]
    public async Task CompleteMembershipAndBlocksAreDetachedBeforeTransportCanObserveThem()
    {
        var originalText = new TextBlock("accepted 雪");
        var originalPoint = new CachePointBlock("caller");
        var supplied = new List<ContentBlock> { originalText, originalPoint, new TextBlock("tail") };
        var request = new ModelRequest([Message.User("question")], "ignored") { SystemPromptContent = supplied };
        supplied[0] = new TextBlock("mutated");
        supplied.RemoveAt(1);
        supplied.Add(new CachePointBlock("late"));
        Assert.NotSame(supplied, request.SystemPromptContent);
        Assert.Throws<NotSupportedException>(() => ((IList<ContentBlock>)request.SystemPromptContent!).Clear());
        var body = await SendAsync(request, Profile());
        var system = body.GetProperty("system");
        Assert.Equal(3, system.GetArrayLength());
        Assert.Equal("accepted 雪", system[0].GetProperty("text").GetString());
        Assert.Equal("caller", system[1].GetProperty("cachePoint").GetProperty("ttl").GetString());
        Assert.Equal("tail", system[2].GetProperty("text").GetString());
        Assert.Equal("accepted 雪\ntail", request.SystemPrompt);
    }

    [Fact]
    public async Task TypedJsonRoundtripKeepsOrderedBlocksAndIndependentReadonlyMembership()
    {
        var request = Typed([new TextBlock("first"), new CachePointBlock("5m"), new TextBlock("second")]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(request, options);
        var copy = JsonSerializer.Deserialize<ModelRequest>(json, options)!;
        Assert.Equal("first\nsecond", copy.SystemPrompt);
        Assert.NotSame(request.SystemPromptContent, copy.SystemPromptContent);
        Assert.Throws<NotSupportedException>(() => ((IList<ContentBlock>)copy.SystemPromptContent!).Clear());
        var body = await SendAsync(copy, Profile());
        Assert.Equal(3, body.GetProperty("system").GetArrayLength());
        Assert.Equal("5m", body.GetProperty("system")[1].GetProperty("cachePoint").GetProperty("ttl").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("tool")]
    [InlineData("reasoning")]
    [InlineData("null-text")]
    [InlineData("custom")]
    public async Task MalformedOrOutOfSliceContentIsRejectedBeforeAnyOfficialRequest(string shape)
    {
        ContentBlock block = shape switch
        {
            "null" => null!,
            "tool" => new ToolUseBlock("u", "lookup", JsonSerializer.SerializeToElement(new { })),
            "reasoning" => new ReasoningBlock("private"),
            "null-text" => new TextBlock(null!),
            _ => new UnknownSystemBlock()
        };
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await CollectAsync(model, Typed([block])));
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task SupportedSystemGuardContentReachesTheOfficialNonStreamingTransport()
    {
        var body = await SendAsync(Typed([new GuardContentBlock("selected")]), Profile());
        var guard = Assert.Single(body.GetProperty("system").EnumerateArray(), value => value.TryGetProperty("guardContent", out _)).GetProperty("guardContent");
        Assert.Equal("selected", guard.GetProperty("text").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("null", 1, "legacy")]
    [InlineData("empty", 0, null)]
    [InlineData("cache-only", 1, null)]
    [InlineData("empty-text", 2, "")]
    [InlineData("two-text", 3, "first\nsecond")]
    public async Task TypedPresenceAndEmptyLegacyStringsHaveDistinctWireSemantics(string shape, int expectedBlocks, string? expectedText)
    {
        IReadOnlyList<ContentBlock>? content = shape switch
        {
            "null" => null,
            "empty" => [],
            "cache-only" => [new CachePointBlock("caller")],
            "empty-text" => [new TextBlock("")],
            _ => [new TextBlock("first"), new TextBlock("second")]
        };
        var request = new ModelRequest([Message.User("question")], "legacy") { SystemPromptContent = content };
        Assert.Equal(expectedText, request.SystemPrompt);
        var profile = shape == "null" ? Profile() with { Cache = null } : Profile();
        var body = await SendAsync(request, profile);
        if (expectedBlocks == 0) Assert.False(body.TryGetProperty("system", out _));
        else Assert.Equal(expectedBlocks, body.GetProperty("system").GetArrayLength());
        if (shape == "cache-only") Assert.Equal("caller", body.GetProperty("system")[0].GetProperty("cachePoint").GetProperty("ttl").GetString());
    }

    [Theory]
    [InlineData(null, "1h", "1h", null, "1h")]
    [InlineData("", "1h", "1h", null, "1h")]
    [InlineData(null, "1h", "5m", null, null)]
    [InlineData(null, "5m", "1h", null, null)]
    [InlineData(null, "1h", "5m", "1h", "1h")]
    [InlineData("", "1h", "5m", "", "1h")]
    [InlineData(null, null, null, null, null)]
    [InlineData("", null, null, null, null)]
    [InlineData("caller", "1h", "5m", null, "caller")]
    [InlineData("5m", "1h", "1h", "1h", "5m")]
    [InlineData(null, "vendor-duration", "vendor-duration", null, "vendor-duration")]
    [InlineData(" ", "1h", "1h", null, " ")]
    public async Task OnlyMissingCallerTtlUsesTheEnabledProfileAndToolsStanddown(string? caller, string? shared, string? tools, string? system, string? expected)
    {
        var point = new CachePointBlock(caller);
        var request = Typed([new TextBlock("stable"), point, new TextBlock("tail")]) with { Tools = Tools() };
        var body = await SendAsync(request, Profile() with
        {
            Cache = new()
            {
                Strategy = BedrockCacheStrategy.Anthropic,
                Ttl = shared,
                ToolsTtl = tools,
                SystemPromptTtl = system
            }
        });
        var mapped = body.GetProperty("system");
        Assert.Equal(3, mapped.GetArrayLength());
        AssertTtl(mapped[1].GetProperty("cachePoint"), expected);
        Assert.Equal(caller, point.Ttl);
    }

    [Theory]
    [InlineData("section-off", null)]
    [InlineData("section-off", "")]
    [InlineData("section-off", "caller")]
    [InlineData("unsupported-auto", null)]
    [InlineData("unsupported-auto", "")]
    [InlineData("unsupported-auto", "caller")]
    [InlineData("legacy-off", null)]
    [InlineData("legacy-off", "")]
    [InlineData("legacy-off", "caller")]
    public async Task DisabledManagementKeepsCallerCheckpointAuthority(string mode, string? caller)
    {
        var options = mode switch
        {
            "section-off" => Profile() with { Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", SystemPrompt = false } },
            "unsupported-auto" => Profile() with { Cache = new() { Strategy = BedrockCacheStrategy.Auto, Ttl = "1h" } },
            _ => Profile() with { Cache = null }
        };
        var body = await SendAsync(Typed([new TextBlock("stable"), new CachePointBlock(caller)]), options);
        Assert.Equal(2, body.GetProperty("system").GetArrayLength());
        AssertTtl(body.GetProperty("system")[1].GetProperty("cachePoint"), caller);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("one")]
    [InlineData("several")]
    [InlineData("leading")]
    public async Task LegacyFlagsDoNotDuplicateTypedCallerPoints(string shape)
    {
        IReadOnlyList<ContentBlock> content = shape switch
        {
            "none" => [new TextBlock("one"), new TextBlock("two")],
            "one" => [new TextBlock("one"), new CachePointBlock("caller"), new TextBlock("two")],
            "several" => [new TextBlock("one"), new CachePointBlock("first"), new TextBlock("two"), new CachePointBlock("second")],
            _ => [new CachePointBlock("leading"), new TextBlock("one")]
        };
        var body = await SendAsync(Typed(content), Profile() with { Cache = null, CacheSystemPrompt = true, CacheTtl = "1h" });
        var points = body.GetProperty("system").EnumerateArray().Where(value => value.TryGetProperty("cachePoint", out _)).Select(value => value.GetProperty("cachePoint").GetProperty("ttl").GetString()).ToArray();
        string[] expected = shape switch { "none" => ["1h"], "one" => ["caller"], "several" => ["first", "second"], _ => ["leading"] };
        Assert.Equal(expected, points);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("nonstream")]
    [InlineData("count")]
    public async Task OfficialOperationsUseTheSameTypedSystemGraph(string operation)
    {
        var request = Typed([new TextBlock("first"), new CachePointBlock("caller"), new TextBlock("second")]);
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile() with { Streaming = operation == "stream", UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        if (operation == "count") Assert.Equal(9, await model.CountTokensAsync(request));
        else Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var sent = Assert.Single(wire.Requests); Assert.True(sent.Signed);
        var graph = operation == "count" ? sent.Body.GetProperty("input").GetProperty("converse") : sent.Body;
        Assert.Equal(3, graph.GetProperty("system").GetArrayLength());
        AssertTtl(graph.GetProperty("system")[1].GetProperty("cachePoint"), "caller");
        Emit(wire, operation);
    }

    [Fact]
    public async Task LocalEstimateUsesTheNewlineTextProjectionAndDoesNotSendCachePoints()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Profile());
        var request = Typed([new TextBlock("first 雪"), new CachePointBlock("caller"), new TextBlock("second")]);
        var legacy = new ModelRequest(request.Messages, "first 雪\nsecond");
        Assert.Equal(await model.CountTokensAsync(legacy), await model.CountTokensAsync(request));
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    internal static BedrockModelOptions Profile() => new()
    { ModelId = "opaque-model", Streaming = false, RequestTimeout = TimeSpan.FromSeconds(15), Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h" } };
    internal static ModelRequest Typed(IReadOnlyList<ContentBlock> content) => new([Message.User("question")], "ignored legacy") { SystemPromptContent = content };
    internal static ToolSpec[] Tools() => [new("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))];
    internal static void AssertTtl(JsonElement point, string? ttl)
    {
        Assert.Equal("default", point.GetProperty("type").GetString());
        if (ttl is null) Assert.False(point.TryGetProperty("ttl", out _));
        else Assert.Equal(ttl, point.GetProperty("ttl").GetString());
    }
    internal static async Task<string> CollectAsync(BedrockModel model, ModelRequest request, CancellationToken cancellation = default)
    {
        var answer = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, cancellation)) if (item is ModelContentBlockDeltaEvent { Text: { } delta }) answer.Append(delta);
        return answer.ToString();
    }
    private async Task<JsonElement> SendAsync(ModelRequest request, BedrockModelOptions options)
    {
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, options);
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        Emit(wire, "conformance");
        return Assert.Single(wire.Requests).Body;
    }
    private void Emit(BedrockRecoveryWireFixture wire, string scenario) => output.WriteLine(JsonSerializer.Serialize(new
    { Phase = "b07-conformance-wire", Scenario = scenario, Requests = wire.Requests }));
    private sealed record UnknownSystemBlock : ContentBlock;
}
