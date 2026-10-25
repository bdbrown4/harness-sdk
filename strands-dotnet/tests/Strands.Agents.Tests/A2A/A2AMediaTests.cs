using System.Net.Http;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AMediaTests
{
    [Fact]
    public async Task ImageUrlsAndInlineVideosUseDistinctFileSourcesAndVideosRemainTypedOnReturn()
    {
        using var http = new HttpClient(new A2AAgentTests.ProtocolHandler((request, id) =>
        {
            var parts = request.GetProperty("params").GetProperty("message").GetProperty("parts");
            Assert.Equal("https://images.test/image.png", parts[0].GetProperty("file").GetProperty("uri").GetString());
            Assert.False(parts[0].GetProperty("file").TryGetProperty("bytes", out _));
            Assert.Equal("QQ==", parts[1].GetProperty("file").GetProperty("bytes").GetString());
            return A2AAgentTests.Frame(id, new { kind = "message", messageId = "response", role = "agent", parts = new[] { new { kind = "file", file = new { mimeType = "video/mp4", bytes = "Qg==" } } } });
        }));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));

        var result = await agent.InvokeContentAsync([new ImageBlock("image/png", new Uri("https://images.test/image.png")), new VideoBlock("video/mp4", "QQ==")]);

        var video = Assert.IsType<VideoBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal("Qg==", video.Data);
    }

    public static IEnumerable<object[]> S3Sources()
    {
        var source = new S3Location("s3://bucket/key", "123456789012");
        yield return [new ImageBlock("image/png", source)];
        yield return [new AudioBlock("audio/wav", source)];
        yield return [new DocumentBlock("application/pdf", source)];
        yield return [new VideoBlock("video/mp4", source)];
    }
    [Theory]
    [MemberData(nameof(S3Sources))]
    public void S3SourcesCannotSilentlyBecomeEmptyByteFiles(ContentBlock block)
    {
        using var http = new HttpClient();
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        Assert.Throws<NotSupportedException>(() => agent.StreamContentAsync([block]));
    }
    [Fact]
    public void GuardrailSelectionsCannotSilentlyBecomeOrdinaryText()
    {
        using var http = new HttpClient();
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        Assert.Throws<NotSupportedException>(() => agent.StreamContentAsync([new GuardContentBlock("protected", qualifiers: [GuardQualifier.GuardContent])]));
    }
}
