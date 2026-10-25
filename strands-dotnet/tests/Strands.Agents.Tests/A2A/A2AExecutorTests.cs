using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AExecutorTests
{
    [Fact]
    public async Task NativeExecutionRetainsEachContextAndPublishesIncrementalArtifactsBeforeCompletion()
    {
        var agents = new Dictionary<string, Agent>();
        await using var executor = new A2AExecutor(new()
        {
            AgentFactory = (id, _) => { var agent = new Agent(new() { Model = new HistoryModel() }); agents.Add(id, agent); return new(agent); }
        });
        var first = new Sink(); var second = new Sink(); var separate = new Sink();

        await executor.ExecuteAsync(Context("task-1", "first", "one"), first);
        await executor.ExecuteAsync(Context("task-2", "first", "two"), second);
        await executor.ExecuteAsync(Context("task-3", "second", "three"), separate);

        Assert.Equal(2, agents.Count);
        Assert.Equal(4, agents["first"].Messages.Count);
        Assert.Equal(2, agents["second"].Messages.Count);
        Assert.Equal("task", first.Events[0].GetProperty("kind").GetString());
        Assert.Equal("working", first.Events[0].GetProperty("status").GetProperty("state").GetString());
        Assert.Contains(first.Events, item => item.GetProperty("kind").GetString() == "artifact-update" && item.GetProperty("artifact").GetProperty("parts")[0].GetProperty("text").GetString() == "history=1");
        Assert.Contains(second.Events, item => item.GetProperty("kind").GetString() == "artifact-update" && item.GetProperty("artifact").GetProperty("parts")[0].GetProperty("text").GetString() == "history=3");
        Assert.Equal("completed", first.Events.Last().GetProperty("status").GetProperty("state").GetString());
        Assert.True(first.Events.Last().GetProperty("final").GetBoolean());
    }

    [Fact]
    public async Task SinkFailureRetainsNativeLifecycleCleanupWithoutAttachingThePrimaryToItself()
    {
        var primary = new IOException("event delivery");
        var cleanup = new InvalidOperationException("native after invocation");
        var hooks = new HookRegistry(); hooks.Register<AfterInvocationEvent>((_, _) => throw cleanup);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new HistoryModel(), Hooks = hooks })) });

        var error = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(Context("task", "context", "prompt"), new FailingSink(primary)));

        Assert.Same(primary, error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    internal static A2ARequestContext Context(string taskId, string contextId, string prompt) => new(taskId, contextId,
        JsonSerializer.SerializeToElement(new { kind = "message", messageId = Guid.NewGuid().ToString("N"), role = "user", parts = new[] { new { kind = "text", text = prompt } } }));
    internal sealed class Sink : IA2AEventSink
    {
        internal List<JsonElement> Events { get; } = [];
        public ValueTask PublishAsync(JsonElement update, CancellationToken cancellationToken = default) { Events.Add(update.Clone()); return default; }
    }
    private sealed class FailingSink(Exception error) : IA2AEventSink
    {
        public ValueTask PublishAsync(JsonElement update, CancellationToken cancellationToken = default)
        { if (update.GetProperty("kind").GetString() != "task") throw error; return default; }
    }
    private sealed class HistoryModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "history=" + request.Messages.Count);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
