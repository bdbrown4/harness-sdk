using System.Text;
using System.Text.Json;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerContractTests
{
    private const string End = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";

    [Fact]
    public async Task PayloadKeepsAllAssistantTextAndToolResultUnicodeAndUsesRealRoutingArguments()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes(End));
        using var client = new StubClient(wire);
        using var model = new SageMakerAIModel(client, new()
        {
            EndpointName = "endpoint",
            InferenceComponentName = "component",
            TargetVariant = "variant",
            AdditionalArguments = JsonSerializer.SerializeToElement(new { CustomAttributes = "trace", InferenceId = "id" }),
            Payload = new() { MaxTokens = 42, AdditionalArguments = JsonSerializer.SerializeToElement(new { top_k = 7 }) }
        });
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var history = new Strands.Agents.Message[]
        {
            new(Role.Assistant, [new TextBlock("first"), new TextBlock("second"), new ToolUseBlock("call", "lookup", schema)]),
            new(Role.User, [new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(new { city = "東京" }))])])
        };
        await CollectAsync(model, new(history, "system", [new("lookup", "lookup", schema)]) { ToolChoice = new(ToolChoiceMode.None) });
        var sent = Assert.Single(client.StreamRequests);
        Assert.Equal("component", sent.InferenceComponentName); Assert.Equal("variant", sent.TargetVariant);
        Assert.Equal("trace", sent.CustomAttributes); Assert.Equal("id", sent.InferenceId);
        using var payload = JsonDocument.Parse(sent.Body.ToArray());
        Assert.Equal("auto", payload.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(42, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(7, payload.RootElement.GetProperty("top_k").GetInt32());
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(2, messages[1].GetProperty("content").GetArrayLength());
        Assert.Contains("東京", messages[2].GetProperty("content").GetString());
        Assert.Equal("call", messages[2].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task RequestedToolResultUserConversionActuallyChangesTheTransmittedMessage()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes(End)); using var client = new StubClient(wire);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", Payload = new() { ToolResultsAsUserMessages = true } });
        await CollectAsync(model, new([new(Role.User, [new ToolResultBlock("c", "done")])]));
        using var body = JsonDocument.Parse(Assert.Single(client.StreamRequests).Body.ToArray());
        var message = body.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Tool call ID 'c' returned: done", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReasoningClosesOnceWhenUsageArrivesAfterTheFinish()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"answer\",\"reasoning\":\"plan\"},\"finish_reason\":\"stop\"}]}\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var events = await CollectAsync(model);
        var starts = events.OfType<ModelContentBlockStartEvent>().Select(item => item.Index).ToArray();
        Assert.Equal(starts.OrderBy(index => index), events.OfType<ModelContentBlockStopEvent>().Select(item => item.Index).OrderBy(index => index));
        Assert.Equal(2, starts.Length);
    }

    [Fact]
    public async Task PayloadEventBoundsApplyToACompleteOversizedEventToo()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 400) + "\"},\"finish_reason\":\"stop\"}]}\n"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", MaximumResponseBytes = 100 });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model));
        Assert.True(wire.Disposed);
    }

    [Theory]
    [InlineData("data: {broken\n")]
    [InlineData("data: {\"choices\":")]
    public async Task StrictMalformedEventPolicyThrowsAndDisposes(string invalid)
    {
        using var wire = Wire(Encoding.UTF8.GetBytes(invalid)); using var client = new StubClient(wire);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", MalformedEventPolicy = SageMakerMalformedEventPolicy.Throw });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task DefaultParserSkipsMalformedSseAndPreservesMultilineBareJson()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("data: {broken\n: keepalive\nevent: ignored\n"), Encoding.UTF8.GetBytes("{\n\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"));
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        Assert.Equal("ok", string.Concat((await CollectAsync(model)).OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
    }

    [Fact]
    public async Task EnteredStreamRetainsConfigAndPublishedOptionsDetachJsonAndLists()
    {
        using var first = Wire(Encoding.UTF8.GetBytes(End)); using var second = Wire(Encoding.UTF8.GetBytes(End)); using var client = new StubClient(first, second);
        var stop = new List<string> { "stop" };
        using var model = new SageMakerAIModel(client, new() { EndpointName = "before", Payload = new() { Stop = stop } });
        await using var entered = model.StreamAsync(new([Strands.Agents.Message.User("hello")])).GetAsyncEnumerator();
        Assert.True(await entered.MoveNextAsync());
        using (var document = JsonDocument.Parse("{\"max_tokens\":99}"))
            model.UpdateConfig(options => options with { EndpointName = "after", Payload = options.Payload with { AdditionalArguments = document.RootElement } });
        stop[0] = "changed";
        while (await entered.MoveNextAsync()) { }
        await CollectAsync(model);
        Assert.Equal(new[] { "before", "after" }, client.StreamRequests.Select(request => request.EndpointName));
        Assert.Equal("stop", Assert.Single(model.GetConfig().Payload.Stop!));
        Assert.Equal(99, model.Metadata.MaxOutputTokens);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaximumResponseBytes = 0 }));
        Assert.Equal("after", model.GetConfig().EndpointName);
    }

    [Fact]
    public async Task StructuredOutputUsesNonStreamingSchemaValidationAndLeavesConfigurationUnchanged()
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"{\"value\":7}"},"finish_reason":"stop"}]}"""));
        using var client = new StubClient(body); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var result = await model.InvokeStructuredAsync(new([Strands.Agents.Message.User("value")]), new StructuredOutputOptions<int> { Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } }), Deserializer = value => value.GetProperty("value").GetInt32() });
        Assert.Equal(7, result); Assert.Empty(client.StreamRequests); Assert.True(model.GetConfig().Payload.Stream);
        using var sent = JsonDocument.Parse(Assert.Single(client.Requests).Body.ToArray());
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task StructuredOutputAppliesSchemaAfterAnExplicitAwsBodyOverride()
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"7"},"finish_reason":"stop"}]}"""));
        using var client = new StubClient(body); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", AdditionalArguments = JsonSerializer.SerializeToElement(new { Body = "{\"messages\":[],\"stream\":true}" }) });
        Assert.Equal(7, await model.InvokeStructuredAsync(new([]), new StructuredOutputOptions<int> { Schema = JsonSerializer.SerializeToElement(new { type = "integer" }) }));
        using var sent = JsonDocument.Parse(Assert.Single(client.Requests).Body.ToArray());
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(sent.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task EarlyConsumerDisposalClosesTheAwsResponseWithoutOwningTheClient()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes(End)); using var client = new StubClient(wire); var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await using (var stream = model.StreamAsync(new([Strands.Agents.Message.User("hello")])).GetAsyncEnumerator()) Assert.True(await stream.MoveNextAsync());
        Assert.True(wire.Disposed); model.Dispose(); Assert.False(client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => CollectAsync(model));
    }
}
