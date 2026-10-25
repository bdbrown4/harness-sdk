using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserMediaTests
{
    public static IEnumerable<object[]> SourceArms()
    {
        var cases = new (string Raw, string Kind, string Source, string Mime)[]
        {
            ("{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"AAEC\\n\"}}}", "image", "bytes", "image/png"),
            ("{\"image\":{\"format\":\"jpg\",\"source\":{\"url\":\"https://example.invalid/image.png\"}}}", "image", "url", "image/jpeg"),
            ("{\"image\":{\"format\":\"webp\",\"source\":{\"location\":{\"type\":\"s3\",\"uri\":\"s3://bucket/key\",\"bucketOwner\":\"123456789012\"}}}}", "image", "location", "image/webp"),
            ("{\"video\":{\"format\":\"three_gp\",\"source\":{\"bytes\":\"AAEC\"}}}", "video", "bytes", "video/3gpp"),
            ("{\"video\":{\"format\":\"mp4\",\"source\":{\"location\":{\"type\":\"s3\",\"uri\":\"s3://bucket/key\",\"bucketOwner\":\"123456789012\"}}}}", "video", "location", "video/mp4"),
            (Document("pdf", "{\"bytes\":\"AAEC\"}"), "document", "bytes", "application/pdf"),
            (Document("docx", "{\"location\":{\"type\":\"s3\",\"uri\":\"s3://bucket/key\",\"bucketOwner\":\"123456789012\"}}"), "document", "location", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            (Document("txt", "{\"text\":\"h\u00e9llo\"}"), "document", "text", "text/plain"),
            (Document("md", "{\"content\":[{\"text\":\"first\"},{\"text\":\"last\"}]}"), "document", "content", "text/markdown"),
            (Document("md", "{\"content\":[]}"), "document", "empty-content", "text/markdown"),
            ("{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"\"}}}", "image", "empty-bytes", "image/png"),
        };
        foreach (var behavior in new[] { HandoffToUserBehavior.TypeScript, HandoffToUserBehavior.Python })
            foreach (var row in cases) yield return [behavior, row.Raw, row.Kind, row.Source, row.Mime];
    }

    [Theory]
    [MemberData(nameof(SourceArms))]
    public async Task KnownJsonMediaSourcesPreserveBytesReferencesAndDocumentFields(HandoffToUserBehavior behavior, string raw, string kind, string source, string mime)
    {
        ToolResultBlock result;
        using (var document = JsonDocument.Parse(raw)) result = await Invoke(behavior, document.RootElement);
        var block = Assert.Single(result.ContentBlocks);
        Assert.Equal("current", result.ToolUseId);
        string data;
        S3Location? location;
        if (kind == "image")
        {
            var image = Assert.IsType<ImageBlock>(block);
            Assert.Equal(mime, image.MediaType);
            data = image.Data; location = image.Location;
            Assert.Equal(source == "url" ? "https://example.invalid/image.png" : null, image.Url?.AbsoluteUri);
        }
        else if (kind == "video")
        {
            var video = Assert.IsType<VideoBlock>(block);
            Assert.Equal(mime, video.MediaType);
            data = video.Data; location = video.Location;
        }
        else
        {
            var document = Assert.IsType<DocumentBlock>(block);
            Assert.Equal(mime, document.MediaType);
            data = document.Data; location = document.Location;
            Assert.Equal("report", document.Name);
            Assert.Equal("reference", document.Context);
            Assert.True(document.CitationsEnabled);
            Assert.Equal(source == "text" ? "h\u00e9llo" : null, document.Text);
            if (source is "content" or "empty-content")
            {
                Assert.NotNull(document.Content);
                Assert.Equal(source == "content" ? new[] { "first", "last" } : Array.Empty<string>(), document.Content.Select(x => x.Text));
                Assert.True(Assert.IsAssignableFrom<IList<TextBlock>>(document.Content).IsReadOnly);
            }
            else Assert.Null(document.Content);
        }
        Assert.Equal(source == "bytes" ? "AAEC" : "", data);
        Assert.Equal(source == "location" ? "s3://bucket/key" : null, location?.Uri);
        Assert.Equal(source == "location" ? "123456789012" : null, location?.BucketOwner);
    }

    public static IEnumerable<object[]> LosslessNativeProfileCases()
    {
        var cases = new[]
        {
            "{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"AAEC\"},\"unknown\":7}}",
            "{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"AAEC\",\"url\":\"https://example.invalid/p\"}}}",
            "{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"!\"}}}",
            "{\"image\":{\"format\":\"png\",\"source\":{\"url\":\"ftp://example.invalid/p\"}}}",
            "{\"image\":{\"format\":\"png\",\"source\":{\"location\":{\"type\":\"s3\",\"uri\":\"s3://bad bucket/key\"}}}}",
            "{\"document\":{\"format\":\"txt\",\"source\":{\"content\":[{\"text\":\"first\",\"other\":1}]}}}",
            "{\"document\":{\"format\":\"txt\",\"source\":{\"text\":\"first\"},\"context\":null}}",
            "{\"document\":{\"format\":\"txt\",\"source\":{\"text\":\"first\"},\"citations\":null}}",
            "{\"image\":{\"format\":\"future\",\"source\":{\"bytes\":\"AAEC\"}}}",
        };
        foreach (var behavior in new[] { HandoffToUserBehavior.TypeScript, HandoffToUserBehavior.Python })
            foreach (var raw in cases) yield return [behavior, raw];
    }

    [Theory]
    [MemberData(nameof(LosslessNativeProfileCases))]
    public async Task UnrepresentableFieldsAreNotPartiallyAcceptedOrSilentlyDropped(HandoffToUserBehavior behavior, string raw)
    {
        var value = Json(raw);
        if (behavior == HandoffToUserBehavior.Python) await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Invoke(behavior, value));
        else Assert.True(JsonElement.DeepEquals(value, Assert.IsType<JsonBlock>(Assert.Single((await Invoke(behavior, value)).ContentBlocks)).Json));
        Assert.Equal(raw, value.GetRawText());
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript)]
    [InlineData(HandoffToUserBehavior.Python)]
    public async Task MixedTypedMediaArrayKeepsOrderWithoutFetchingAnyReference(HandoffToUserBehavior behavior)
    {
        var value = Json("[{\"text\":\"first\"},{\"image\":{\"format\":\"png\",\"source\":{\"url\":\"https://example.invalid/p\"}}},{\"json\":null},{\"text\":\"last\"}]");
        var result = await Invoke(behavior, value, array: true);
        Assert.Collection(result.ContentBlocks,
            b => Assert.Equal("first", Assert.IsType<TextBlock>(b).Text),
            b => Assert.Equal("https://example.invalid/p", Assert.IsType<ImageBlock>(b).Url!.AbsoluteUri),
            b => Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonBlock>(b).Json.ValueKind),
            b => Assert.Equal("last", Assert.IsType<TextBlock>(b).Text));
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript)]
    [InlineData(HandoffToUserBehavior.Python)]
    public async Task LaterInvalidMediaPreventsPartialTypedArrayPublication(HandoffToUserBehavior behavior)
    {
        var value = Json("[{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"AAEC\"}}},{\"document\":{\"format\":\"txt\",\"source\":{\"text\":\"last\"},\"unknown\":5}}]");
        if (behavior == HandoffToUserBehavior.Python) await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Invoke(behavior, value, array: true));
        else Assert.True(JsonElement.DeepEquals(value, Assert.IsType<JsonBlock>(Assert.Single((await Invoke(behavior, value, array: true)).ContentBlocks)).Json.GetProperty("$value")));
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript)]
    [InlineData(HandoffToUserBehavior.Python)]
    public async Task RegisteredAgentResumesWithTypedMediaCorrelatedToTheCurrentQuestion(HandoffToUserBehavior behavior)
    {
        var tool = HandoffToUser.Create(new() { Behavior = behavior });
        var model = new QuestionThenTextModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var paused = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        var media = Json("{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"AAEC\"}}}");
        var answer = behavior == HandoffToUserBehavior.Python ? JsonSerializer.SerializeToElement(new { status = "success", content = new[] { media }, toolUseId = "spoof" }) : media;
        var complete = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, answer)] });
        Assert.Equal(StopReasons.EndTurn, complete.StopReason);
        Assert.Null(agent.InterruptState);
        Assert.Equal(2, model.Calls);
        var result = Assert.Single(agent.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>());
        Assert.Equal("current", result.ToolUseId);
        Assert.Equal("AAEC", Assert.IsType<ImageBlock>(Assert.Single(result.ContentBlocks)).Data);
    }

    private static string Document(string format, string source) => "{\"document\":{\"format\":\"" + format + "\",\"source\":" + source + ",\"name\":\"report\",\"context\":\"reference\",\"citations\":{\"enabled\":true}}}";
    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static ValueTask<ToolResultBlock> Invoke(HandoffToUserBehavior behavior, JsonElement value, bool array = false)
    {
        var reply = behavior == HandoffToUserBehavior.Python ? JsonSerializer.SerializeToElement(new { status = "success", content = array ? value : JsonSerializer.SerializeToElement(new[] { value }) }) : value;
        var context = new ToolContext(null!, new("current", "handoff_to_user", Json("{}")), new Dictionary<string, object?>()) { InterruptHandler = (_, _, _) => reply };
        return HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Json("{\"message\":\"Q\"}"), context);
    }
    private sealed class QuestionThenTextModel : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Calls++ == 0;
            yield return new ModelMessageStartEvent();
            if (first) yield return new ModelContentBlockStartEvent(0, new("current", "handoff_to_user", Json("{\"message\":\"Answer?\"}")));
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "complete"); }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
    }
}
