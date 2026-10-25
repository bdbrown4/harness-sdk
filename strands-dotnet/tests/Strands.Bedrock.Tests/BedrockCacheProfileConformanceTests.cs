using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockCacheProfileConformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(BedrockCacheStrategy.Auto, "anthropic.claude-test", true)]
    [InlineData(BedrockCacheStrategy.Auto, "us.anthropic.claude-test", true)]
    [InlineData(BedrockCacheStrategy.Auto, "arn:aws:bedrock:region:account:profile/claude", true)]
    [InlineData(BedrockCacheStrategy.Auto, "opaque-anthropic-alias", true)]
    [InlineData(BedrockCacheStrategy.Auto, "ANTHROPIC.CLAUDE-test", false)]
    [InlineData(BedrockCacheStrategy.Auto, "us.Anthropic.Claude-test", false)]
    [InlineData(BedrockCacheStrategy.Auto, "arn:aws:bedrock:region:account:profile/opaque", false)]
    [InlineData(BedrockCacheStrategy.Auto, "amazon.nova-pro", false)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "anthropic.claude-test", true)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "ANTHROPIC.CLAUDE-test", true)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "us.AnThRoPiC.ClAuDe-test", true)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "ARN:AWS:BEDROCK:region:account:profile/CLAUDE", true)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "opaque", false)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "amazon.nova-pro", false)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "unicode-\u0130-ANTHROPIC", true)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "anthrop\u0130c-model", false)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "anthrop\u0131c-model", false)]
    [InlineData(BedrockCacheStrategy.AutoIgnoreCase, "\u0430nthropic-model", false)]
    [InlineData(BedrockCacheStrategy.Anthropic, "opaque", true)]
    [InlineData(BedrockCacheStrategy.Anthropic, "amazon.nova-pro", true)]
    [InlineData(BedrockCacheStrategy.Anthropic, "ANTHROPIC.CLAUDE-test", true)]
    public async Task ModelEligibilityKeepsExactAndAsciiProfilesDistinct(BedrockCacheStrategy strategy, string modelId, bool enabled)
    {
        var body = await SendAsync(Options(new() { Strategy = strategy, Ttl = "1h" }) with { ModelId = modelId }, Request());
        Assert.Equal(enabled ? 1 : 0, Points(body.GetProperty("system")).Length);
        Assert.Equal(enabled ? 1 : 0, Points(body.GetProperty("toolConfig").GetProperty("tools")).Length);
        Assert.Equal(enabled ? 1 : 0, Points(body.GetProperty("messages")[0].GetProperty("content")).Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExplicitSectionsOverrideEveryLegacyFlag(bool tools, bool system, bool messages)
    {
        var body = await SendAsync(Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Tools = tools, SystemPrompt = system, Messages = messages }) with
        { CacheTools = true, CacheSystemPrompt = true, CacheLastMessage = true, CacheTtl = "1h" }, Request());
        Assert.Equal(tools ? 1 : 0, Points(body.GetProperty("toolConfig").GetProperty("tools")).Length);
        Assert.Equal(system ? 1 : 0, Points(body.GetProperty("system")).Length);
        Assert.Equal(messages ? 1 : 0, Points(body.GetProperty("messages")[0].GetProperty("content")).Length);
        foreach (var point in Points(body.GetProperty("system"))) Assert.False(point.TryGetProperty("ttl", out _));
    }

    [Theory]
    [InlineData("1h", "5m", null, null, "5m", null, "1h")]
    [InlineData("5m", "1h", null, null, "1h", null, "5m")]
    [InlineData("1h", "1h", null, null, "1h", "1h", "1h")]
    [InlineData("1h", "5m", "1h", "5m", "5m", "1h", "5m")]
    [InlineData("1h", "5m", "", null, "5m", "1h", "1h")]
    [InlineData("1h", "", "", "", "1h", "1h", "1h")]
    [InlineData("", "", "", "", null, null, null)]
    [InlineData(null, null, null, null, null, null, null)]
    [InlineData("vendor-duration", null, null, null, "vendor-duration", "vendor-duration", "vendor-duration")]
    [InlineData(" ", null, " ", " ", " ", " ", " ")]
    public async Task OpenTtlFallbackAndSystemStanddownAreWireObservable(string? shared, string? tools, string? system, string? messages,
        string? expectedTools, string? expectedSystem, string? expectedMessages)
    {
        var body = await SendAsync(Options(new()
        {
            Strategy = BedrockCacheStrategy.Anthropic,
            Ttl = shared,
            ToolsTtl = tools,
            SystemPromptTtl = system,
            MessagesTtl = messages
        }), Request());
        AssertTtl(Assert.Single(Points(body.GetProperty("toolConfig").GetProperty("tools"))), expectedTools);
        AssertTtl(Assert.Single(Points(body.GetProperty("system"))), expectedSystem);
        AssertTtl(Assert.Single(Points(body.GetProperty("messages")[0].GetProperty("content"))), expectedMessages);
    }

    [Theory]
    [InlineData("no-tools")]
    [InlineData("null-system")]
    [InlineData("empty-system")]
    [InlineData("none-choice")]
    [InlineData("unsupported")]
    public async Task AbsentSectionsAndUnsupportedProfilesDoNotInventWireFields(string scenario)
    {
        var request = Request();
        request = scenario switch
        {
            "no-tools" => request with { Tools = [] },
            "null-system" => request with { SystemPrompt = null },
            "empty-system" => request with { SystemPrompt = "" },
            "none-choice" => request with { ToolChoice = new(ToolChoiceMode.None) },
            _ => request
        };
        var cache = new BedrockCacheOptions
        {
            Strategy = scenario == "unsupported" ? BedrockCacheStrategy.Auto : BedrockCacheStrategy.Anthropic,
            Ttl = "1h",
            ToolsTtl = "5m"
        };
        var body = await SendAsync(Options(cache), request);
        if (scenario is "no-tools" or "none-choice")
        {
            Assert.False(body.TryGetProperty("toolConfig", out _));
            AssertTtl(Assert.Single(Points(body.GetProperty("system"))), "1h");
        }
        if (scenario == "null-system") Assert.False(body.TryGetProperty("system", out _));
        if (scenario == "empty-system")
        {
            Assert.Empty(Points(body.GetProperty("system")));
            Assert.Equal("", body.GetProperty("system")[0].GetProperty("text").GetString());
        }
        if (scenario == "unsupported")
        {
            Assert.Empty(Points(body.GetProperty("system")));
            Assert.Empty(Points(body.GetProperty("toolConfig").GetProperty("tools")));
        }
    }

    [Theory]
    [InlineData("dynamic", 2)]
    [InlineData("pdf", 3)]
    [InlineData("text-document", 2)]
    [InlineData("leading", 2)]
    [InlineData("duplicates", 1)]
    [InlineData("assistant-tail", 2)]
    [InlineData("no-user", -1)]
    [InlineData("all-dynamic", -1)]
    public async Task ExistingMessageBoundaryPolicyIsReused(string scenario, int expectedIndex)
    {
        var oldPoint = new CachePointBlock("caller-old");
        var first = new Message(Role.User, [new TextBlock("old"), oldPoint]);
        var content = new List<ContentBlock> { new TextBlock("stable"), new TextBlock("stable-two") };
        if (scenario == "dynamic") content.Add(new TextBlock("dynamic"));
        if (scenario is "pdf" or "text-document") content.Add(new DocumentBlock(scenario == "pdf" ? "application/pdf" : "text/plain", "", "fixture") { Text = "document" });
        if (scenario == "leading") content.Insert(0, new CachePointBlock("discarded"));
        if (scenario == "duplicates") { content.Insert(1, new CachePointBlock("first")); content.Add(new CachePointBlock("duplicate")); }
        var last = new Message(Role.User, content);
        var messages = new List<Message> { first, last };
        if (scenario == "assistant-tail") messages.Add(new(Role.Assistant, [new TextBlock("tail"), new CachePointBlock("tail-old")]));
        if (scenario == "no-user") messages = [new(Role.Assistant, [new TextBlock("tail"), oldPoint])];
        var request = new ModelRequest(messages) { DynamicTrailingBlocks = scenario == "dynamic" ? 1 : scenario == "all-dynamic" ? 2 : 0 };
        var body = await SendAsync(Options(new() { Strategy = BedrockCacheStrategy.Anthropic, MessagesTtl = "5m" }), request);
        var sent = body.GetProperty("messages");
        var selected = scenario == "no-user" ? 0 : 1;
        var mapped = sent[selected].GetProperty("content");
        var indices = mapped.EnumerateArray().Select((item, index) => (item, index)).Where(value => value.item.TryGetProperty("cachePoint", out _)).Select(value => value.index).ToArray();
        Assert.Equal(expectedIndex < 0 ? Array.Empty<int>() : [expectedIndex], indices);
        foreach (var point in Points(mapped)) AssertTtl(point, "5m");
        for (var index = 0; index < sent.GetArrayLength(); index++) if (index != selected) Assert.Empty(Points(sent[index].GetProperty("content")));
        Assert.Same(oldPoint, first.Content[1]);
        Assert.Equal("caller-old", oldPoint.Ttl);
        Assert.Equal(scenario == "duplicates" ? 4 : scenario is "leading" or "dynamic" or "pdf" or "text-document" ? 3 : 2, last.Content.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStrategyIsRejectedBeforePublicationAndIo(bool update)
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        var valid = Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h" });
        var invalid = valid with { Cache = new() { Strategy = (BedrockCacheStrategy)912 } };
        if (!update) Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockModel(client, invalid));
        else
        {
            using var model = new BedrockModel(client, valid);
            Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(_ => invalid));
            Assert.Equal(valid.Cache, model.GetConfig().Cache);
            Assert.NotSame(valid.Cache, model.GetConfig().Cache);
        }
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("nonstream")]
    [InlineData("count")]
    public async Task OfficialOperationsShareTheSameAcceptedCacheGraph(string operation)
    {
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", SystemPromptTtl = "5m" }) with
        { Streaming = operation == "stream", UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        if (operation == "count") Assert.Equal(9, await model.CountTokensAsync(Request()));
        else Assert.Equal("summary", await CollectAsync(model, Request()));
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        Assert.True(sent.Signed);
        var graph = operation == "count" ? sent.Body.GetProperty("input").GetProperty("converse") : sent.Body;
        AssertTtl(Assert.Single(Points(graph.GetProperty("system"))), "5m");
        AssertTtl(Assert.Single(Points(graph.GetProperty("toolConfig").GetProperty("tools"))), "1h");
        AssertTtl(Assert.Single(Points(graph.GetProperty("messages")[0].GetProperty("content"))), "1h");
        Emit(wire, operation);
    }

    [Fact]
    public async Task LocalTokenEstimateDoesNotAcquireTheBorrowedClient()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "unknown" }));
        using var legacy = new BedrockModel(client, Options(null));
        Assert.Equal(await legacy.CountTokensAsync(Request()), await model.CountTokensAsync(Request()));
        await wire.Completion;
        Assert.Empty(wire.Requests);
    }

    internal static BedrockModelOptions Options(BedrockCacheOptions? cache) => new()
    { ModelId = "opaque-model", Cache = cache, Streaming = false, RequestTimeout = TimeSpan.FromSeconds(15) };
    internal static ModelRequest Request() => new([new(Role.User, [new TextBlock("question")])], "instructions",
        [new("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))]);
    internal static JsonElement[] Points(JsonElement blocks) => blocks.EnumerateArray().Where(block => block.TryGetProperty("cachePoint", out _)).Select(block => block.GetProperty("cachePoint")).ToArray();
    internal static void AssertTtl(JsonElement point, string? ttl)
    {
        Assert.Equal("default", point.GetProperty("type").GetString());
        if (ttl is null) Assert.False(point.TryGetProperty("ttl", out _));
        else Assert.Equal(ttl, point.GetProperty("ttl").GetString());
    }
    internal static async Task<string> CollectAsync(BedrockModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, cancellationToken)) if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }
    private async Task<JsonElement> SendAsync(BedrockModelOptions options, ModelRequest request)
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
    { Phase = "b06-conformance-wire", Scenario = scenario, Requests = wire.Requests }));
}
