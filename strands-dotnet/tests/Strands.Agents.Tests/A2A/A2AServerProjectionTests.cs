using System.Net.Http;
using System.Runtime.CompilerServices;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerProjectionTests
{
    [Theory]
    [InlineData(false, "hel\nlo\n")]
    [InlineData(true, "hello")]
    public async Task TypescriptTextProjectionRetainsItsPinnedTaskPartAndStreamChunkSemantics(bool streaming, string expected)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new FragmentsModel() })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options());
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var remote = new A2AAgent(new(http, new(new Uri("http://local/"))), new() { Streaming = streaming, ContentMode = A2AContentMode.TextOnly });

        var result = await remote.InvokeAsync("run");

        Assert.Equal(expected, result.Message!.Text);
    }
    private sealed class FragmentsModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "hel"); yield return new ModelContentBlockDeltaEvent(0, Text: "lo");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
