using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class AgentToolOptionsTests
{
    [Fact]
    public void TypedToolUsesCanonicalInputAndADelegateDescription()
    {
        var agent = new Agent(new() { Model = new Model(), Name = "specialist" });
        var tool = agent.AsTool(new AgentToolOptions { Delegate = true });
        Assert.True(tool.Spec.InputSchema.GetProperty("properties").TryGetProperty("input", out _));
        Assert.Contains("only tool", tool.Spec.Description);
    }

    [Fact]
    public async Task PreservedContextUsesTheBorrowedWrappedConversation()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model });
        await using var parent = new Agent(new() { Model = new Model() });
        var tool = agent.AsTool(new AgentToolOptions { PreserveContext = true });
        Assert.Equal("users=1", await Call(tool, parent, "one"));
        Assert.Equal("users=2", await Call(tool, parent, "two"));
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal("users=3", (await agent.InvokeAsync("still borrowed")).ToString());
    }

    [Fact]
    public async Task FreshContextRestoresTheCapturedBaselineRatherThanLaterExternalState()
    {
        var model = new Model();
        await using var agent = new Agent(new() { Model = model, Messages = new[] { Message.User("baseline") } });
        await using var parent = new Agent(new() { Model = new Model() });
        agent.State.Set("marker", "at creation");
        var tool = agent.AsTool(new AgentToolOptions());
        agent.State.Set("marker", "external mutation");
        await agent.InvokeAsync("external conversation");
        Assert.Equal("users=2", await Call(tool, parent, "first fresh call"));
        Assert.Equal("at creation", agent.State.Get<string>("marker"));
        Assert.Equal("users=2", await Call(tool, parent, "second fresh call"));
        Assert.DoesNotContain(agent.Messages, message => message.Text is "external conversation" or "first fresh call");
    }

    [Fact]
    public async Task FreshTypedContextRejectsSessionsWhileTheLegacyOverloadRemainsCompatible()
    {
        await using var agent = new Agent(new() { Model = new Model(), SessionStore = new Store(), SessionId = "borrowed" });
        Assert.Throws<ArgumentException>(() => agent.AsTool(new AgentToolOptions()));
        Assert.NotNull(agent.AsTool(new AgentToolOptions { PreserveContext = true }));
        Assert.NotNull(agent.AsTool());
    }

    [Fact]
    public async Task ConcurrentCallsCannotResetAnActiveWrappedConversation()
    {
        var model = new GatedModel();
        await using var agent = new Agent(new() { Model = model });
        await using var parent = new Agent(new() { Model = new Model() });
        var tool = Assert.IsAssignableFrom<IRichTool>(agent.AsTool(new AgentToolOptions()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstInput = JsonSerializer.SerializeToElement(new { input = "first" });
        var first = tool.InvokeResultAsync(firstInput, new(parent, new("first", tool.Spec.Name, firstInput), new Dictionary<string, object?>()), deadline.Token).AsTask();
        try
        {
            await Wait(model.Started.Task, deadline.Token);
            var secondInput = JsonSerializer.SerializeToElement(new { input = "second" });
            var second = await tool.InvokeResultAsync(secondInput, new(parent, new("second", tool.Spec.Name, secondInput), new Dictionary<string, object?>()), deadline.Token);
            Assert.Equal("second", second.ToolUseId); Assert.Equal(ToolResultStatus.Error, second.Status);
            Assert.Equal("first", Assert.Single(agent.Messages).Text);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("external", cancellationToken: deadline.Token));
        }
        finally { model.Release.TrySetResult(true); await first; }
        Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task DisposingToolProgressReleasesTheWrappedInvocationWithoutDisposingItsModel()
    {
        var model = new GatedModel();
        var agent = new Agent(new() { Model = model, OwnsModel = true });
        await using var parent = new Agent(new() { Model = new Model() });
        try
        {
            var tool = Assert.IsAssignableFrom<IStreamingTool>(agent.AsTool(new AgentToolOptions { PreserveContext = true }));
            var input = JsonSerializer.SerializeToElement(new { input = "partial" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var iterator = tool.StreamAsync(input, new(parent, new("partial", tool.Spec.Name, input), new Dictionary<string, object?>()), deadline.Token).GetAsyncEnumerator(deadline.Token);
            try
            {
                while (await iterator.MoveNextAsync())
                    if (iterator.Current is ToolStreamEvent { Data: ModelStreamUpdateEvent }) break;
            }
            finally { await iterator.DisposeAsync(); }
            Assert.Equal(0, model.Disposals);
            agent.AddTool(new FunctionTool(new("after_partial", "Proof of released guard", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ok")));
        }
        finally { model.Release.TrySetResult(true); await agent.DisposeAsync(); }
        Assert.Equal(1, model.Disposals);
    }

    private static async Task Wait(Task task, CancellationToken cancellationToken)
    {
        var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancelled) != task) cancellationToken.ThrowIfCancellationRequested();
        await task;
    }

    private static Task<string> Call(ITool tool, Agent parent, string text)
    {
        var input = JsonSerializer.SerializeToElement(new { input = text });
        return tool.InvokeAsync(input, new(parent, new(Guid.NewGuid().ToString(), tool.Spec.Name, input), new Dictionary<string, object?>())).AsTask();
    }

    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "users=" + request.Messages.Count(message => message.Role == Role.User));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
    }

    private sealed class Store : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new((SessionSnapshot?)null);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }

    private sealed class GatedModel : IModel, IAsyncDisposable
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            yield return new ModelMessageStartEvent();
            await Wait(Release.Task, cancellationToken);
            yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
