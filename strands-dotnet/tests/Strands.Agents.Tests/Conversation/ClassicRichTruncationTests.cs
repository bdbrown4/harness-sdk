using System.Text;
using System.Text.Json;
using Strands.Agents.Context;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicRichTruncationTests
{
    [Theory]
    [InlineData("video-bytes")]
    [InlineData("document-text")]
    [InlineData("json-large")]
    [InlineData("image-bytes")]
    [InlineData("image-url")]
    [InlineData("image-s3")]
    [InlineData("video-s3")]
    [InlineData("document-bytes")]
    [InlineData("document-s3")]
    [InlineData("document-content")]
    public async Task RichReactiveTruncationRetainsTheExchangeInsteadOfDroppingHistory(string kind)
    {
        ContentBlock block = kind switch
        {
            "video-bytes" => new VideoBlock("video/mp4", "AQID"),
            "video-s3" => new VideoBlock("video/mp4", new S3Location("s3://bucket/video")),
            "image-bytes" => new ImageBlock("image/png", "AQID"),
            "image-url" => new ImageBlock("image/jpeg", new Uri("https://example.test/image")),
            "image-s3" => new ImageBlock("image/png", new S3Location("s3://bucket/image")),
            "document-bytes" => new DocumentBlock("application/pdf", "AQID", "report"),
            "document-s3" => new DocumentBlock("application/pdf", new S3Location("s3://bucket/report"), "report"),
            "document-text" => DocumentBlock.FromText(new string('x', 1000), "facts") with { Context = "source context", CitationsEnabled = true },
            "document-content" => DocumentBlock.FromContent([new TextBlock("small"), new TextBlock(new string('x', 1000))], "facts") with { Context = "source context", CitationsEnabled = true },
            _ => new JsonBlock(JsonSerializer.SerializeToElement(new { payload = new string('x', 1000) }))
        };
        await using var agent = Create(block);
        var original = agent.Messages;
        var manager = RichManager();
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        Assert.Equal(3, agent.Messages.Count);
        Assert.Equal(original.Select(message => message.TrackingId), agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(0, manager.RemovedMessageCount);
        var result = Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
        var content = Assert.Single(result.ContentBlocks);
        if (kind is "document-text" or "document-content")
        {
            var document = Assert.IsType<DocumentBlock>(content);
            var expected = new string('x', 200) + "\n<truncated chars=\"600\"/>\n" + new string('x', 200);
            if (kind == "document-text") Assert.Equal(expected, document.Text);
            else { Assert.Equal("small", document.Content![0].Text); Assert.Equal(expected, document.Content[1].Text); }
            Assert.True(document.CitationsEnabled);
            Assert.Equal("source context", document.Context);
            Assert.Equal("facts", document.Name);
        }
        else Assert.Equal(kind switch
        {
            "video-bytes" => "[video: mp4, source: bytes, 3 bytes]",
            "video-s3" => "[video: mp4, source: s3]",
            "image-bytes" => "[image: png, source: bytes, 3 bytes]",
            "image-url" => "[image: jpeg, source: url]",
            "image-s3" => "[image: png, source: s3]",
            "document-bytes" => "[document: report, pdf, source: bytes, 3 bytes]",
            "document-s3" => "[document: report, pdf, source: s3]",
            _ => "[json: 1014 chars]"
        }, Assert.IsType<TextBlock>(content).Text);
    }

    [Theory]
    [InlineData(450)]
    [InlineData(451)]
    public async Task TextBoundaryIs450Utf16UnitsAndACompletedPreviewCannotBeTruncatedAgain(int length)
    {
        await using var agent = Create(new TextBlock(new string('x', length)));
        var manager = RichManager();
        var overflow = new ContextWindowOverflowException("overflow");
        if (length == 450)
        {
            Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
            Assert.Equal(new string('x', length), Assert.Single(Result(agent).ContentBlocks.OfType<TextBlock>()).Text);
            return;
        }
        await manager.ReduceContextAsync(agent, overflow);
        Assert.Equal(new string('x', 200) + "\n<truncated chars=\"51\"/>\n" + new string('x', 200), Result(agent).Content);
        var accepted = agent.Messages;
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(accepted, agent.Messages);
    }

    [Fact]
    public async Task Utf16LimitsNeverPublishHalfASurrogatePair()
    {
        var emoji = char.ConvertFromUtf32(0x1f600);
        var text = new string('a', 199) + emoji + new string('x', 100) + emoji + new string('b', 199);
        await using var agent = Create(new TextBlock(text));
        await RichManager().ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        var actual = Result(agent).Content;
        Assert.Equal(new string('a', 199) + "\n<truncated chars=\"104\"/>\n" + new string('b', 199), actual);
        Assert.NotEmpty(new UTF8Encoding(false, true).GetBytes(actual));
    }

    [Fact]
    public async Task LargeJsonUsesCompactDecodedValueLengthAndSmallJsonRetainsItsType()
    {
        var large = JsonSerializer.SerializeToElement(new { payload = new string('\u00e9', 450) });
        var small = new JsonBlock(JsonSerializer.SerializeToElement(new { ok = true }));
        await using var agent = CreateResult(new ToolResultBlock("call", [small, new JsonBlock(large), new TextBlock("tail")]));
        await RichManager().ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        var result = Result(agent);
        Assert.IsType<JsonBlock>(result.ContentBlocks[0]);
        Assert.Equal("[json: 464 chars]", Assert.IsType<TextBlock>(result.ContentBlocks[1]).Text);
        Assert.Equal("tail", Assert.IsType<TextBlock>(result.ContentBlocks[2]).Text);
        Assert.True(JsonElement.DeepEquals(small.Json, Assert.IsType<JsonBlock>(result.ContentBlocks[0]).Json));
    }

    [Fact]
    public async Task ReplacementOwnsJsonAndRetainsAllResultAndMessageFacts()
    {
        var failure = new IOException("original tool failure");
        ToolResultBlock result;
        using (var producer = JsonDocument.Parse("""{"receipt":"retained"}"""))
            result = new("call", [new TextBlock("head"), new VideoBlock("video/mp4", "AQID"), new AudioBlock("audio/wav", "AQID"), new JsonBlock(JsonSerializer.SerializeToElement(new { small = true }))], ToolResultStatus.Error, JsonSerializer.SerializeToElement(new { authoritative = true }))
            { Error = failure, IteratorCleanupFailed = true, Metadata = producer.RootElement };
        await using var agent = CreateResult(result);
        var original = agent.Messages;
        await RichManager().ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        var replacement = Result(agent);
        Assert.Equal("call", replacement.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, replacement.Status);
        Assert.Same(failure, replacement.Error);
        Assert.True(replacement.IteratorCleanupFailed);
        Assert.True(replacement.StructuredContent!.Value.GetProperty("authoritative").GetBoolean());
        Assert.Equal("retained", replacement.Metadata!.Value.GetProperty("receipt").GetString());
        Assert.Equal("retained", agent.Messages[2].Metadata!.Value.GetProperty("message").GetString());
        Assert.Equal(original[2].TrackingId, agent.Messages[2].TrackingId);
        Assert.IsType<TextBlock>(replacement.ContentBlocks[1]);
        Assert.IsType<AudioBlock>(replacement.ContentBlocks[2]);
        Assert.IsType<JsonBlock>(replacement.ContentBlocks[3]);
        Assert.IsType<VideoBlock>(Assert.Single(original[2].Content.OfType<ToolResultBlock>()).ContentBlocks[1]);
        Assert.Same(original[1], agent.Messages[1]);
    }

    [Theory]
    [InlineData("video-bytes")]
    [InlineData("json")]
    [InlineData("document")]
    public async Task DefaultPythonPolicyKeepsNonImageRichBlocksUnchanged(string kind)
    {
        ContentBlock content = kind switch { "video-bytes" => new VideoBlock("video/mp4", "AQID"), "json" => new JsonBlock(JsonSerializer.SerializeToElement(new { payload = new string('x', 1000) })), _ => DocumentBlock.FromText(new string('x', 1000)) };
        await using var agent = Create(content);
        var original = agent.Messages;
        var overflow = new ContextWindowOverflowException("overflow");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await new Classic.SlidingWindowConversationManager().ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutineManagementAndDisabledTruncationDoNotProjectRichContent(bool disabled)
    {
        await using var agent = Create(new VideoBlock("video/mp4", "AQID"));
        var original = agent.Messages;
        var manager = new Classic.SlidingWindowConversationManager(new() { ShouldTruncateResults = !disabled, ToolResultTruncationPolicy = Classic.ClassicToolResultTruncationPolicy.TypeScript });
        if (!disabled) await manager.ApplyManagementAsync(agent);
        else
        {
            var overflow = new ContextWindowOverflowException("overflow");
            Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        }
        Assert.Equal(original, agent.Messages);
    }

    [Fact]
    public async Task PinnedResultsAreNeverTruncated()
    {
        await using var agent = CreateResult(new ToolResultBlock("call", [new VideoBlock("video/mp4", "AQID")]), true);
        var original = agent.Messages;
        var overflow = new ContextWindowOverflowException("overflow");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await RichManager().ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages);
    }

    [Fact]
    public async Task ANewerReducibleResultDoesNotChangeTheOldestOnlyOrPythonTrimBoundaryContract()
    {
        var input = JsonSerializer.Deserialize<JsonElement>("{}");
        await using var agent = new Agent(new()
        {
            Model = new ClassicConversationManagerTests.TextModel(),
            Messages = [Message.User("task"), new(Role.Assistant, [new ToolUseBlock("old", "read", input)]), new(Role.User, [new ToolResultBlock("old", "small")]), new(Role.Assistant, [new ToolUseBlock("new", "read", input)]), new(Role.User, [new ToolResultBlock("new", [new VideoBlock("video/mp4", "AQID")])])]
        });
        var manager = RichManager();
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        Assert.Equal(3, manager.RemovedMessageCount);
        Assert.Equal(2, agent.Messages.Count);
        Assert.Equal(Role.Assistant, agent.Messages[0].Role);
        Assert.IsType<VideoBlock>(Assert.Single(Assert.Single(agent.Messages[1].Content.OfType<ToolResultBlock>()).ContentBlocks));
    }

    [Fact]
    public async Task DefaultChildrenInheritTheExplicitTruncationPolicy()
    {
        var calls = 0;
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = () => { if (Interlocked.Increment(ref calls) == 1) throw new ContextWindowOverflowException("overflow"); return default; } };
        await using var parent = new Agent(new() { Model = model, ConversationManager = RichManager() });
        await using var child = parent.CreateChild();
        await child.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(History(new ToolResultBlock("call", [new VideoBlock("video/mp4", "AQID")]))) }));
        var result = await child.InvokeAsync(null);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("[video: mp4, source: bytes, 3 bytes]", Assert.Single(child.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        Assert.Empty(parent.Messages);
    }

    [Fact]
    public void UnknownPolicyIsRejectedAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Classic.SlidingWindowConversationManager(new() { ToolResultTruncationPolicy = (Classic.ClassicToolResultTruncationPolicy)999 }));

    private static Classic.SlidingWindowConversationManager RichManager() => new(new() { ToolResultTruncationPolicy = Classic.ClassicToolResultTruncationPolicy.TypeScript });
    private static Agent Create(ContentBlock block) => CreateResult(new ToolResultBlock("call", [block]));
    private static ToolResultBlock Result(Agent agent) => Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
    private static Agent CreateResult(ToolResultBlock result, bool pinned = false) => new(new()
    {
        Model = new ClassicConversationManagerTests.TextModel(),
        Messages = History(result, pinned)
    });
    private static Message[] History(ToolResultBlock result, bool pinned = false)
    {
        var message = new Message(Role.User, [result], metadata: JsonSerializer.SerializeToElement(new { message = "retained" }));
        return [Message.User("task"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), pinned ? MessagePins.Pin(message) : message];
    }
}
