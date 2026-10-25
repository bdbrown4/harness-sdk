using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Context;

public sealed class ContextOffloadIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProviderRequestBodiesShrinkOnlyWhenEffectiveResultShrinks(bool anthropic, bool structured)
    {
        var text = new string('x', 6_004);
        var result = new ToolResultBlock("call", text) { StructuredContent = structured ? JsonSerializer.SerializeToElement(new { text }) : null };
        var history = ToolResultOffloaderTests.History(result);
        var recording = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = recording, Messages = history, Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        var projected = recording.Requests[0];
        using var handler = new RecordingHandler(anthropic);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await foreach (var _ in model.StreamAsync(projected with { Messages = history })) { }
        await foreach (var _ in model.StreamAsync(projected)) { }
        Assert.Equal(2, handler.Bodies.Count);
        if (structured) Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        else
        {
            Assert.True(Encoding.UTF8.GetByteCount(handler.Bodies[1]) < Encoding.UTF8.GetByteCount(handler.Bodies[0]));
            Assert.Contains("[Truncated:", handler.Bodies[1]);
            Assert.DoesNotContain(new string('x', 4_000), handler.Bodies[1]);
        }
    }

    [Fact]
    public async Task ExecutesRetrievalThroughAgentLoopWithoutOffloadingItsResultAgain()
    {
        var model = new RetrievingModel();
        var text = new string('x', 8_000);
        var agent = new Agent(new() { Model = model, Messages = ToolResultOffloaderTests.History(new("call", text)), Plugins = [new ToolResultOffloader()] });
        Assert.Equal("done", (await agent.InvokeAsync(null)).ToString());
        Assert.Equal(2, model.Requests.Count);
        var retrieved = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[4].Content[0]);
        Assert.Equal("lookup", retrieved.ToolUseId);
        Assert.Equal(text, retrieved.Content);
        Assert.Equal(ToolResultStatus.Success, retrieved.Status);
        Assert.Contains("[Truncated:", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]).Content);
    }

    private sealed class RetrievingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var events = Requests.Count == 1 ? AgentTests.Call(("lookup", "retrieve_context", JsonSerializer.Serialize(new { reference = ToolResultOffloaderTests.Reference(request) }))) : AgentTests.Text("done");
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingHandler(bool anthropic) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var text = anthropic
                ? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"done\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
        }
    }
}
