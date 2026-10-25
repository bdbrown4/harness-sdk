using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Amazon.SageMakerRuntime;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents;
using Strands.Agents.SageMaker;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerModelTests
{
    [Fact]
    public async Task NativePayloadPartsDecodeFragmentedUtf8AndSseWithTrailingUsage()
    {
        var data = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"雪\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\ndata: [DONE]\n");
        var snow = Array.IndexOf(data, (byte)0xE9);
        using var wire = Wire(data.Take(snow + 1).ToArray(), data.Skip(snow + 1).ToArray());
        using var client = new StubClient(wire);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var events = await CollectAsync(model);
        Assert.Equal("雪", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.Equal(new Usage(5, 2, ReportedTotalTokens: 7), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal("endpoint", Assert.Single(client.StreamRequests).EndpointName);
        Assert.True(wire.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task NonStreamingInvokesTheAwsOperationAndPreservesReasoningAndObjectToolArguments()
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"answer","reasoning":"plan","tool_calls":{"id":"call","function":{"name":"lookup","arguments":{"q":"雪"}}}},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}"""));
        using var client = new StubClient(body);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "plain", Payload = new() { Stream = false } });
        var events = await CollectAsync(model);
        Assert.Single(client.Requests);
        Assert.Empty(client.StreamRequests);
        Assert.Contains(events.OfType<ModelContentBlockDeltaEvent>(), value => value.Reasoning == "plan");
        Assert.Contains(events.OfType<ModelContentBlockStartEvent>(), value => value.ToolUse?.Name == "lookup");
        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.True(body.Disposed);
    }

    internal static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, ModelRequest? request = null, CancellationToken cancellationToken = default)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request ?? new([Strands.Agents.Message.User("hello")]), cancellationToken)) result.Add(item);
        return result;
    }

    internal static TrackingStream Wire(params byte[][] parts)
    {
        using var data = new MemoryStream();
        foreach (var part in parts)
        {
            var messageType = new EventStreamHeader(":message-type"); messageType.SetString("event");
            var eventType = new EventStreamHeader(":event-type"); eventType.SetString("PayloadPart");
            var contentType = new EventStreamHeader(":content-type"); contentType.SetString("application/octet-stream");
            var bytes = new EventStreamMessage([messageType, eventType, contentType], part).ToByteArray();
            data.Write(bytes, 0, bytes.Length);
        }
        return new(data.ToArray());
    }

    internal sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal sealed class StubClient(params Stream[] bodies) : AmazonSageMakerRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        internal List<InvokeEndpointRequest> Requests { get; } = [];
        internal List<InvokeEndpointWithResponseStreamRequest> StreamRequests { get; } = [];
        internal bool Disposed { get; private set; }
        public override Task<InvokeEndpointResponse> InvokeEndpointAsync(InvokeEndpointRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new InvokeEndpointResponse { Body = (MemoryStream)bodies[Requests.Count - 1] });
        }
        public override Task<InvokeEndpointWithResponseStreamResponse> InvokeEndpointWithResponseStreamAsync(InvokeEndpointWithResponseStreamRequest request, CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            return Task.FromResult(new InvokeEndpointWithResponseStreamResponse { Body = new ResponseStream(bodies[StreamRequests.Count - 1]) });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
