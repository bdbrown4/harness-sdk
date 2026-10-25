using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerMediaTests
{
    [Fact]
    public async Task InlineVideoUsesThePinnedRawVideoUrlContractBesideOtherMedia()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await CollectAsync(model, new([new(Role.User, [new VideoBlock("video/mp4", "dmlkZW8="), new AudioBlock("audio/wav", "YXVkaW8="), new DocumentBlock("application/pdf", "cGRm", "paper.pdf"), new ImageBlock("image/png", "") { Url = new("https://example.org/image.png") }])]));
        using var body = JsonDocument.Parse(Assert.Single(client.StreamRequests).Body.ToArray());
        var parts = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("video_url", parts[0].GetProperty("type").GetString());
        Assert.Equal("dmlkZW8=", parts[0].GetProperty("video_url").GetProperty("url").GetString());
        Assert.Equal("auto", parts[0].GetProperty("video_url").GetProperty("detail").GetString());
        Assert.Equal("YXVkaW8=", parts[1].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal("data:application/pdf;base64,cGRm", parts[2].GetProperty("file").GetProperty("file_data").GetString());
        Assert.Equal("https://example.org/image.png", parts[3].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("document")]
    [InlineData("video")]
    [InlineData("tool_audio")]
    public async Task UnsupportedS3MediaFailsBeforeAwsIoRatherThanSendingEmptyBytes(string kind)
    {
        var location = new S3Location("s3://bucket/object");
        ContentBlock block = kind switch
        {
            "image" => new ImageBlock("image/png", location),
            "audio" => new AudioBlock("audio/wav", location),
            "document" => new DocumentBlock("application/pdf", location),
            "video" => new VideoBlock("video/mp4", location),
            _ => new ToolResultBlock("call", [new AudioBlock("audio/wav", location)])
        };
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model, new([new(Role.User, [block])])));
        Assert.Empty(client.StreamRequests); Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("document")]
    [InlineData("video")]
    public async Task ConflictingByteAndLocationSourcesAreRejectedAtomically(string kind)
    {
        var location = new S3Location("s3://bucket/object");
        ContentBlock block = kind switch
        {
            "audio" => new AudioBlock("audio/wav", "YQ==") { Location = location },
            "document" => new DocumentBlock("application/pdf", "YQ==") { Location = location },
            _ => new VideoBlock("video/mp4", "YQ==") { Location = location }
        };
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(model, new([new(Role.User, [block])])));
        Assert.Empty(client.StreamRequests);
    }

    [Fact]
    public async Task GuardContentIsAnExplicitUnsupportedFeature()
    {
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model, new([new(Role.User, [new GuardContentBlock("protected")])])));
        Assert.Empty(client.StreamRequests);
    }

    [Fact]
    public async Task GuardedRichToolContentCannotBeSilentlyConvertedToText()
    {
        using var client = new StubClient(); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model, new([new(Role.User, [new ToolResultBlock("call", [new GuardContentBlock("protected")])])])));
        Assert.Empty(client.StreamRequests);
    }
}
