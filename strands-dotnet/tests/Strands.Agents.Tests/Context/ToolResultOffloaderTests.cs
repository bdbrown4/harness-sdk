using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class ToolResultOffloaderTests
{
    [Fact]
    public async Task ProjectsLargeResultWithoutChangingDurableHistory()
    {
        var text = new string('x', 8_000);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = History(new("call", text)), Plugins = [new ToolResultOffloader()] });

        await agent.InvokeAsync(null);

        var projected = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.True(projected.Content.Length < text.Length);
        Assert.Contains("[Truncated:", projected.Content);
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
        Assert.Empty(agent.State.GetAll());
    }

    [Theory]
    [InlineData(5_996, false)]
    [InlineData(6_000, false)]
    [InlineData(6_004, true)]
    public async Task AppliesDefaultThresholdOnlyWhenExceeded(int length, bool offloaded)
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = History(new("call", new string('x', length))), Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        Assert.Equal(offloaded, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content.Contains("[Truncated:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreservesHeadTailStatusAndCorrelationAcrossRepeatedCalls()
    {
        var text = new string('h', 1_800) + new string('m', 5_000) + new string('t', 1_200);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("one"), AgentTests.Text("two"));
        var agent = new Agent(new() { Model = model, Messages = History(new("call", text)), Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        await agent.InvokeAsync("continue");
        var first = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        var second = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]);
        Assert.Contains(new string('h', 1_800), first.Content);
        Assert.Contains(new string('t', 1_200), first.Content);
        Assert.DoesNotContain(new string('m', 100), first.Content);
        Assert.Equal("call", first.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, first.Status);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal("result-message", model.Requests[0].Messages[2].TrackingId);
    }

    [Fact]
    public async Task LeavesErrorsStructuredResultsAndOpaqueOnlyResultsUntouched()
    {
        var value = new string('x', 8_000);
        var results = new[]
        {
            new ToolResultBlock("call", value, ToolResultStatus.Error),
            new ToolResultBlock("call", value) { StructuredContent = JsonSerializer.SerializeToElement(new { value }) },
            new ToolResultBlock("call", value) { ContentBlocks = [new ImageBlock("image/png", "AA==")] }
        };
        foreach (var result in results)
        {
            var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
            var agent = new Agent(new() { Model = model, Messages = History(result), Plugins = [new ToolResultOffloader()] });
            await agent.InvokeAsync(null);
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(model.Requests[0].Messages[2].Content[0], typeof(ToolResultBlock)));
        }
    }

    [Fact]
    public async Task RetainsOpaqueContentAlongsideTextAndJsonPreview()
    {
        ContentBlock[] opaque = [new ImageBlock("image/png", "AA=="), new AudioBlock("audio/wav", "AQ=="), new DocumentBlock("application/pdf", "Ag==", "report"), new ResourceLinkBlock("https://example.test", "resource")];
        var result = new ToolResultBlock("call", new ContentBlock[] { new TextBlock(new string('a', 4_000)), new JsonBlock(JsonSerializer.SerializeToElement(new { value = new string('b', 4_000) })) }.Concat(opaque).ToArray());
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = History(result), Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        var projected = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.Single(projected.ContentBlocks.OfType<TextBlock>());
        Assert.Empty(projected.ContentBlocks.OfType<JsonBlock>());
        Assert.Equal(opaque, projected.ContentBlocks.Skip(1));
        Assert.True(JsonSerializer.Serialize<ContentBlock>(projected).Length < JsonSerializer.Serialize<ContentBlock>(result).Length);
    }

    [Fact]
    public async Task SkipsPreviewWhenReferenceOverheadWouldNotShrinkIt()
    {
        var result = new ToolResultBlock("call", "small");
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = History(result), Plugins = [new ToolResultOffloader(new() { ThresholdTokens = 1, PreviewTokens = 0 })] });
        await agent.InvokeAsync(null);
        Assert.Equal("small", Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task DoesNotProjectJsonWhenNativeTypedEncodingWouldGrow()
    {
        var values = new[] { new string('a', 474) }.Concat(Enumerable.Repeat("", 50)).Append(new string('b', 315)).ToArray();
        var json = JsonSerializer.SerializeToElement(values);
        var original = new ToolResultBlock("call", [new JsonBlock(json)]);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new()
        {
            Model = model,
            Messages = History(original),
            Plugins = [new ToolResultOffloader(new() { ThresholdTokens = 201, PreviewTokens = 200 })]
        });

        await agent.InvokeAsync(null);

        var projected = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        static int NativeSize(ToolResultBlock result) => JsonSerializer.SerializeToUtf8Bytes(result.ContentBlocks.Select(block => block switch
        {
            JsonBlock value => (object)new { json = value.Json },
            TextBlock text => new { text = text.Text },
            _ => throw new InvalidOperationException("Unexpected content in native JSON fixture.")
        }).ToArray()).Length;
        Assert.True(NativeSize(projected) <= NativeSize(original), "Offloading must not expand native JSON tool content into a larger text preview.");
        Assert.IsType<JsonBlock>(Assert.Single(projected.ContentBlocks));
    }

    [Fact]
    public async Task DoesNotExpandJsonWhenNativeObjectsRetainOnlyTheLastDuplicateProperty()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{\"value\":\"" + new string('x', 8_000) + "\",\"value\":\"short\"}");
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = History(new("call", [new JsonBlock(json)])), Plugins = [new ToolResultOffloader()] });

        await agent.InvokeAsync(null);

        var projected = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        var originalNativeSize = JsonSerializer.SerializeToUtf8Bytes(new { json = JsonSerializer.Deserialize<Dictionary<string, string>>(json.GetRawText()) }).Length;
        var projectedNativeSize = JsonSerializer.SerializeToUtf8Bytes(Assert.Single(projected.ContentBlocks) switch
        {
            JsonBlock value => (object)new { json = JsonSerializer.Deserialize<Dictionary<string, string>>(value.Json.GetRawText()) },
            TextBlock text => new { text = text.Text },
            _ => throw new InvalidOperationException("Unexpected content in native object fixture.")
        }).Length;
        Assert.True(projectedNativeSize <= originalNativeSize, "Native object normalization must not make the original smaller than its replacement.");
    }

    [Fact]
    public async Task ExcludesRetrievalResultsByPositionWhenProviderReusesCallIds()
    {
        var text = new string('x', 8_000);
        var initial = History(new("reused", text));
        Message[] history = [.. initial, new(Role.Assistant, [new ToolUseBlock("reused", "retrieve_context", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("reused", text)])];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = history, Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        Assert.Contains("[Truncated:", Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content);
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[4].Content[0]).Content);
    }

    [Fact]
    public async Task DuplicateTrackingAndToolIdsKeepDistinctReferencesByMessagePosition()
    {
        var first = new string('a', 8_000);
        var second = new string('b', 8_000);
        Message[] history = [.. History(new("call", first)), new(Role.Assistant, [new ToolUseBlock("call", "large", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", second)], "result-message")];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = history, Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        var firstReference = Reference(model.Requests[0]);
        var secondReference = Regex.Match(Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[4].Content[0]).Content, @"\[(ctx_[^\]]+)\]").Groups[1].Value;
        Assert.NotEqual(firstReference, secondReference);
        Assert.Equal(first, (await Retrieve(agent, firstReference)).Content);
        Assert.Equal(second, (await Retrieve(agent, secondReference)).Content);
    }

    [Fact]
    public async Task CancelsBeforeProjectionWithoutCallingModelOrChangingHistory()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => { cancellation.Cancel(); return default; }, order: -1);
        var model = new AgentTests.ScriptedModel();
        var agent = new Agent(new() { Model = model, Messages = History(new("call", new string('x', 8_000))), Plugins = [new ToolResultOffloader()], Hooks = hooks });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync(null, cancellationToken: cancellation.Token)).StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal(8_000, Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content.Length);
    }

    [Fact]
    public async Task RestoredSessionKeepsStableReferencesAndFullOriginals()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-context-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            var text = new string('x', 8_000);
            var firstModel = new AgentTests.ScriptedModel(AgentTests.Text("done"));
            var first = new Agent(new() { Model = firstModel, Messages = History(new("call", text)), Plugins = [new ToolResultOffloader()], SessionStore = store, SessionId = "context" });
            await first.InvokeAsync(null);
            var reference = Reference(firstModel.Requests[0]);
            var secondModel = new AgentTests.ScriptedModel(AgentTests.Text("continued"));
            var second = new Agent(new() { Model = secondModel, Plugins = [new ToolResultOffloader()], SessionStore = store, SessionId = "context" });
            await second.InvokeAsync("continue");
            Assert.Equal(reference, Reference(secondModel.Requests[0]));
            Assert.Equal(text, (await Retrieve(second, reference)).Content);
            Assert.Equal(text, Assert.IsType<ToolResultBlock>((await store.LoadAsync("context"))!.Messages[2].Content[0]).Content);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SharedPluginDoesNotExposeParentHistoryToFreshChild()
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("parent"));
        var parent = new Agent(new() { Model = model, Messages = History(new("call", new string('x', 8_000))), Plugins = [new ToolResultOffloader()] });
        await parent.InvokeAsync(null);
        var reference = Reference(model.Requests[0]);
        var child = parent.CreateChild();
        await child.InitializeAsync();
        Assert.Equal(ToolResultStatus.Error, (await Retrieve(child, reference)).Status);
        Assert.Equal(8_000, (await Retrieve(parent, reference)).Content.Length);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, -1, 1)]
    [InlineData(2, 1, 0)]
    public void RejectsInvalidBudgets(int threshold, int preview, int retrieval) => Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultOffloader(new() { ThresholdTokens = threshold, PreviewTokens = preview, MaxRetrievalTokens = retrieval }));

    internal static string Reference(ModelRequest request) => Regex.Match(Assert.IsType<ToolResultBlock>(request.Messages[2].Content[0]).Content, @"\[(ctx_[^\]]+)\]").Groups[1].Value is { Length: > 0 } reference ? reference : throw new InvalidOperationException("No context reference was emitted.");

    internal static ValueTask<ToolResultBlock> Retrieve(Agent agent, string reference, object? filter = null, CancellationToken cancellationToken = default)
    {
        var input = filter is null ? JsonSerializer.SerializeToElement(new { reference }) : JsonSerializer.SerializeToElement(filter);
        var tool = Assert.IsAssignableFrom<IRichTool>(agent.Tools.Single(value => value.Spec.Name == "retrieve_context"));
        return tool.InvokeResultAsync(input, new(agent, new("retrieve", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }

    internal static Message[] History(ToolResultBlock result, string name = "large") =>
    [Message.User("read"), new(Role.Assistant, [new ToolUseBlock(result.ToolUseId, name, JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [result], "result-message")];
}
