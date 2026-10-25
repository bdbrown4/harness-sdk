using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockProtocolTests
{
    [Fact]
    public async Task NonAssistantResponseRoleIsRejected()
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"user"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Contains("role", error.Message);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task NativeThrottlingIsTypedAndRetainsItsCauseWithoutRetrying()
    {
        var native = new Amazon.BedrockRuntime.Model.ThrottlingException("slow down");
        using var client = new BedrockModelTests.StubClient { Failure = native };
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Same(native, error.InnerException);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"late\"}}", false)]
    [InlineData("messageStart", "{\"role\":\"assistant\"}", true)]
    [InlineData("contentBlockStop", "{\"contentBlockIndex\":0}", true)]
    [InlineData("contentBlockDelta", "{\"contentBlockIndex\":-1,\"delta\":{\"text\":\"bad\"}}", true)]
    [InlineData("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"bad\",\"toolUse\":{\"input\":\"{}\"}}}", true)]
    [InlineData("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"toolUse\":{\"input\":\"{}\"}}}", true)]
    public async Task MalformedStreamGrammarFailsAndDisposesItsResponse(string type, string json, bool start)
    {
        var frames = new List<(string, string)>();
        if (start) frames.Add(("messageStart", """{"role":"assistant"}"""));
        frames.Add((type, json));
        using var body = BedrockModelTests.Wire(frames.ToArray());
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task OpaqueReasoningBufferLimitIsEnforced()
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"redactedContent":"AQID"}}}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions { MaximumBufferedContentBytes = 2 });

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Contains("buffered", error.Message);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task NativeContextRejectionRetainsTypedCause()
    {
        var native = new Amazon.BedrockRuntime.Model.ValidationException("Input is too long for requested model");
        using var client = new BedrockModelTests.StubClient { Failure = native };
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ContextWindowOverflowException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Same(native, error.InnerException);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task ConflictingContentStartVariantsAreRejected()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"call","name":"lookup"},"image":{"format":"png"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{}"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"tool_use"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Contains("exactly one", error.Message);
        Assert.True(body.Disposed);
    }
}
