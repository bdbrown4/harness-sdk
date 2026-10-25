using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Strands.Agents.Models;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class RestoredIdentityAndWriterUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationUsesTheRestoredStableIdentityRegardlessOfInitializationTiming(bool initializeFirst)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Id = "construction-id", Model = new TextModel(), SessionStore = new RestoredStore(), SessionId = "session" });
        if (initializeFirst) await agent.InitializeAsync();
        await agent.InvokeAsync("continue");
        Assert.Equal("persisted-id", agent.Id);
        var invocation = Assert.Single(spans, value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(agent.Id, invocation.GetTagItem("gen_ai.agent.id"));
    }

    [Fact]
    public async Task APluginFailureAfterRestorationRetainsThePersistedIdentityAndOriginalFailure()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var failure = new IOException("plugin initialization failed");
        await using var agent = new Agent(new() { Id = "construction-id", Model = new TextModel(), SessionStore = new RestoredStore(), SessionId = "session", Plugins = [new FailingPlugin(failure)] });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("continue")));
        var invocation = Assert.Single(spans, value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal("persisted-id", invocation.GetTagItem("gen_ai.agent.id"));
        Assert.Equal(ActivityStatusCode.Error, invocation.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriterZeroPlaceholderRetainsWhetherUsageWasActuallyReported(bool reported)
    {
        using var transport = new WriterTransport(reported);
        using var http = new HttpClient(transport);
        var model = new WriterModel(http, new("palmyra-x4", "key"));
        var metadata = new List<ModelMetadataEvent>();
        await foreach (var value in model.StreamAsync(new([Message.User("question")])))
            if (value is ModelMetadataEvent measurement) metadata.Add(measurement);
        Assert.Equal(reported, Assert.Single(metadata).UsageReported);
        Assert.Equal(0, metadata[0].Usage.TotalTokens);
        await using var agent = new Agent(new() { Model = model });
        var result = await agent.InvokeAsync("question");
        Assert.Equal(reported ? (long?)0 : null, result.Metrics!.LatestContextSize);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class RestoredStore : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
            => new(new SessionSnapshot(1, []) { AgentId = "persisted-id" });
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }

    private sealed class FailingPlugin(Exception failure) : IPlugin
    {
        public string Name => "test:initialization-failure";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => new(Task.FromException(failure));
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class WriterTransport(bool reported) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var wire = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n";
            if (reported) wire += "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}\n\n";
            wire += "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wire, Encoding.UTF8, "text/event-stream") });
        }
    }
}
