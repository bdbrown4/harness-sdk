using System.Text;
using Amazon.Runtime.EventStreams;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerAwsFrameTests
{
    [Fact]
    public async Task OfficialAwsExceptionFramesRetainTheNativeServiceFailure()
    {
        var kind = new EventStreamHeader(":message-type"); kind.SetString("exception");
        var type = new EventStreamHeader(":exception-type"); type.SetString("ModelStreamError");
        var content = new EventStreamHeader(":content-type"); content.SetString("application/json");
        using var wire = new TrackingStream(new EventStreamMessage([kind, type, content], Encoding.UTF8.GetBytes("""{"Message":"container failed","OriginalMessage":"model failure","OriginalStatusCode":500}""")).ToByteArray());
        using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model));
        Assert.IsType<ModelStreamErrorException>(error.GetBaseException()); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task CorruptedAwsFrameChecksumFailsWithoutAcceptingACompletion()
    {
        using var valid = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var bytes = valid.ToArray(); bytes[bytes.Length - 1] ^= 1;
        using var wire = new TrackingStream(bytes); using var client = new StubClient(wire); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(wire.Disposed);
    }
}
