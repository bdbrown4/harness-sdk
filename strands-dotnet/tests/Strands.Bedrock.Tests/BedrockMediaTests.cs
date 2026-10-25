using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockMediaTests
{
    [Fact]
    public void MediaAndGuardContentRoundTripThroughMessageSnapshots()
    {
        var qualifiers = new[] { GuardQualifier.GroundingSource, GuardQualifier.Query };
        var location = new S3Location("s3://media-bucket/a%20b.mp4", "123456789012");
        var message = new Message(Role.User, [new VideoBlock("video/mp4", location), new GuardContentBlock("guarded", qualifiers: qualifiers), new GuardContentBlock(new ImageBlock("image/png", "AQID"))]);
        qualifiers[0] = GuardQualifier.GuardContent;

        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(message))!;

        Assert.Equal(location, Assert.IsType<VideoBlock>(restored.Content[0]).Location);
        Assert.Equal(new[] { GuardQualifier.GroundingSource, GuardQualifier.Query }, Assert.IsType<GuardContentBlock>(restored.Content[1]).Qualifiers);
        Assert.Equal("AQID", Assert.IsType<GuardContentBlock>(restored.Content[2]).Image!.Data);
    }

    [Theory]
    [InlineData("https://bucket/key", null)]
    [InlineData("s3://UPPER/key", null)]
    [InlineData("s3://media-bucket/key", "123")]
    [InlineData("s3://media-bucket/key", "１２３４５６７８９０１２")]
    public void InvalidS3ReferencesAreRejectedWithoutChangingTheirOriginalRepresentation(string uri, string? owner)
    {
        Assert.Throws<ArgumentException>(() => new S3Location(uri, owner));
    }

    [Fact]
    public void GuardSelectionRejectsAmbiguousSourcesAndUnsupportedImageSemantics()
    {
        Assert.Throws<ArgumentException>(() => new GuardContentBlock(text: null));
        Assert.Throws<ArgumentException>(() => new GuardContentBlock("text", new ImageBlock("image/png", "AQID")));
        Assert.Throws<ArgumentException>(() => new GuardContentBlock(new ImageBlock("image/gif", "AQID")));
        Assert.Throws<ArgumentException>(() => new GuardContentBlock(new ImageBlock("image/png", new S3Location("s3://media-bucket/key"))));
        Assert.Throws<ArgumentException>(() => new GuardContentBlock("text", qualifiers: [(GuardQualifier)999]));
        Assert.Throws<ArgumentException>(() => new GuardContentBlock(null, new ImageBlock("image/png", "AQID"), [GuardQualifier.Query]));
    }

    [Fact]
    public void ExistingAndRemoteMediaRetainAllSourcesAndDocumentOptionsInSnapshots()
    {
        var location = new S3Location("s3://media-bucket/key", "123456789012");
        var message = new Message(Role.User, [new ImageBlock("image/png", location), new AudioBlock("audio/wav", location), new DocumentBlock("application/pdf", location, "Evidence") { CitationsEnabled = true, Context = "context" }, new ImageBlock("image/png", new Uri("https://example.com/image.png")), new VideoBlock("video/mp4", "AQID")]);

        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(message))!;

        Assert.Equal(location, Assert.IsType<ImageBlock>(restored.Content[0]).Location);
        Assert.Equal(location, Assert.IsType<AudioBlock>(restored.Content[1]).Location);
        var document = Assert.IsType<DocumentBlock>(restored.Content[2]);
        Assert.Equal(location, document.Location);
        Assert.True(document.CitationsEnabled);
        Assert.Equal("context", document.Context);
        Assert.Equal("Evidence", document.Name);
        Assert.Equal(new Uri("https://example.com/image.png"), Assert.IsType<ImageBlock>(restored.Content[3]).Url);
        Assert.Equal("AQID", Assert.IsType<VideoBlock>(restored.Content[4]).Data);
    }

    [Fact]
    public async Task S3MediaSourcesReachTheNativeRequestWithoutEmptyByteSubstitutes()
    {
        var location = new S3Location("s3://media-bucket/original", "123456789012");
        using var body = Response();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());

        await BedrockModelTests.CollectAsync(model, new([new(Role.User, [new ImageBlock("image/png", location), new AudioBlock("audio/wav", location), new DocumentBlock("application/pdf", location, "Evidence"), new VideoBlock("video/mp4", location)])]));

        var content = Assert.Single(Assert.Single(client.Requests).Messages).Content;
        Assert.Equal(location.Uri, content[0].Image.Source.S3Location.Uri);
        Assert.Null(content[0].Image.Source.Bytes);
        Assert.Equal(location.BucketOwner, content[1].Audio.Source.S3Location.BucketOwner);
        Assert.Null(content[1].Audio.Source.Bytes);
        Assert.Equal(location.Uri, content[2].Document.Source.S3Location.Uri);
        Assert.Null(content[2].Document.Source.Bytes);
        Assert.Equal("Evidence", content[2].Document.Name);
        Assert.Equal(location.Uri, content[3].Video.Source.S3Location.Uri);
        Assert.Null(content[3].Video.Source.Bytes);
    }

    [Theory]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/3gpp", "three_gp")]
    [InlineData("video/3g2", "three_gp")]
    [InlineData("video/x-matroska", "mkv")]
    public async Task InlineVideoAndRichToolVideoUseNativeFormats(string mediaType, string format)
    {
        using var body = Response();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        var video = new VideoBlock(mediaType, "AQID");
        await BedrockModelTests.CollectAsync(model, new([new(Role.User, [video]), new(Role.User, [new ToolResultBlock("call", [video])])]));

        var messages = Assert.Single(client.Requests).Messages;
        Assert.Equal(format, Assert.Single(messages[0].Content).Video.Format.Value);
        Assert.Equal(format, Assert.Single(Assert.Single(messages[1].Content).ToolResult.Content).Video.Format.Value);
    }

    [Fact]
    public async Task SelectivelyGuardedTextAndImageRetainTheirExactMeaning()
    {
        using var body = Response();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        await BedrockModelTests.CollectAsync(model, new([new(Role.User, [new TextBlock("ordinary"), new GuardContentBlock("selected", qualifiers: [GuardQualifier.GroundingSource, GuardQualifier.Query, GuardQualifier.GuardContent]), new GuardContentBlock(new ImageBlock("image/jpeg", "AQID"))])]));

        var content = Assert.Single(Assert.Single(client.Requests).Messages).Content;
        Assert.Equal("ordinary", content[0].Text);
        Assert.Null(content[0].GuardContent);
        Assert.Equal("selected", content[1].GuardContent.Text.Text);
        Assert.Equal(new[] { "grounding_source", "query", "guard_content" }, content[1].GuardContent.Text.Qualifiers);
        Assert.Equal("jpeg", content[2].GuardContent.Image.Format.Value);
    }

    private static BedrockModelTests.TrackingStream Response() => BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
}
