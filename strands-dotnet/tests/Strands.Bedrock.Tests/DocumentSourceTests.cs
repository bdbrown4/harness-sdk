using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class DocumentSourceTests
{
    [Fact]
    public async Task PlainTextUsesUtf8AndOrderedContentUsesTheNativeDocumentUnion()
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        await BedrockModelTests.CollectAsync(model, new([new(Role.User, [DocumentBlock.FromText("Résumé 🌍", "Plain"), DocumentBlock.FromContent([new("first"), new("second")], "Segments")])]));

        var content = Assert.Single(Assert.Single(client.Requests).Messages).Content;
        Assert.Equal("Résumé 🌍", Encoding.UTF8.GetString(content[0].Document.Source.Bytes.ToArray()));
        Assert.Equal(new[] { "first", "second" }, content[1].Document.Source.Content.Select(block => block.Text));
        Assert.Null(content[1].Document.Source.Bytes);
    }

    [Fact]
    public void MutableDocumentSegmentsAreDetachedInMessagesRichResultsAndSnapshots()
    {
        var segments = new[] { new TextBlock("original") };
        var document = DocumentBlock.FromContent(segments);
        var message = new Message(Role.User, [document, new ToolResultBlock("call", [document])]);
        segments[0] = new("changed");
        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(message))!;

        Assert.Equal("original", Assert.IsType<DocumentBlock>(message.Content[0]).Content![0].Text);
        Assert.Equal("original", Assert.IsType<DocumentBlock>(Assert.IsType<ToolResultBlock>(restored.Content[1]).ContentBlocks[0]).Content![0].Text);
    }

    [Fact]
    public void ConflictingDocumentSourcesAreRejectedBeforeAnyProviderCall()
    {
        DocumentBlock[] invalid = [new("text/plain", "AQID") { Text = "text" }, DocumentBlock.FromText("text") with { Content = [new("segment")] }, new("text/plain", "") { Content = [null!] }, new("text/plain", new S3Location("s3://media-bucket/key")) { Text = "text" }];
        Assert.All(invalid, block => Assert.Throws<ArgumentException>(() => new Message(Role.User, [block])));
    }
}
