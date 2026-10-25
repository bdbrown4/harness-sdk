using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerStructuredTests
{
    [Theory]
    [InlineData("\"length\"", "assistant")]
    [InlineData("\"content_filter\"", "assistant")]
    [InlineData("42", "assistant")]
    [InlineData("null", "assistant")]
    [InlineData("\"stop\"", "user")]
    public async Task StructuredJsonDoesNotAuthorizeAnIncompleteOrInvalidNativeResponse(string reason, string role)
    {
        var response = "{\"choices\":[{\"message\":{\"role\":\"" + role + "\",\"content\":\"7\"},\"finish_reason\":" + reason + "}]}";
        using var body = new TrackingStream(Encoding.UTF8.GetBytes(response)); using var client = new StubClient(body);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        await Assert.ThrowsAsync<StructuredOutputException>(() => model.InvokeStructuredAsync(new([]), new StructuredOutputOptions<int> { Schema = JsonSerializer.SerializeToElement(new { type = "integer" }) }));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task DisposingTheModelAfterBodyReadPreventsStructuredSuccess()
    {
        SageMakerAIModel? model = null;
        using var body = new CompletionBody(() => model!.Dispose()); using var client = new StubClient(body);
        using (model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.InvokeStructuredAsync(new([]), new StructuredOutputOptions<int> { Schema = JsonSerializer.SerializeToElement(new { type = "integer" }) }));
    }

    private sealed class CompletionBody(Action completed) : MemoryStream(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"role":"assistant","content":"7"},"finish_reason":"stop"}]}"""))
    {
        private bool _completed;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_completed) return;
            _completed = true;
            completed();
        }
    }
}
