using System.Text.Json;
using Strands.Agents.Tests.Cancellation;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests;

public sealed class LegacyAgentToolCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledChildCannotCommitASuccessfulParentToolResult(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var hooks = new HookRegistry();
        AgentResult? childResult = null;
        hooks.Register<AfterModelCallEvent>((value, _) =>
        {
            if (cancelCaller) caller.Cancel();
            else value.Agent.Cancel();
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) => { childResult = value.Result; return default; });
        var childModel = new CancellationTestModel(Text("partial child output"));
        await using var child = new Agent(new() { Model = childModel, Hooks = hooks });
        var tool = child.AsTool("specialist");
        var store = new Store();
        var parentModel = new CancellationTestModel(Call(("child-call", "specialist", "{\"task\":\"request\"}")), Text("handled"));
        await using (var parent = new Agent(new() { Model = parentModel, Tools = [tool], SessionStore = store, SessionId = "parent" }))
        {
            var result = await parent.InvokeAsync("delegate", cancellationToken: caller.Token);

            Assert.Equal(StopReasons.Cancelled, Assert.IsType<AgentResult>(childResult).StopReason);
            Assert.Equal("partial child output", childResult!.Message!.Text);
            Assert.Equal(cancelCaller ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
            Assert.Equal(cancelCaller ? 1 : 2, parentModel.Requests.Count);
            AssertErrorResult(parent.Messages);
            AssertErrorResult((await store.LoadAsync("parent"))!.Messages);
        }

        var resumedModel = new CancellationTestModel(Text("restored"));
        await using var resumed = new Agent(new() { Model = resumedModel, Tools = [tool], SessionStore = store, SessionId = "parent" });
        Assert.Equal("restored", (await resumed.InvokeAsync(null)).ToString());
        AssertErrorResult(Assert.Single(resumedModel.Requests).Messages);
        Assert.Single(childModel.Requests);
        Assert.Empty(child.Messages);
    }

    [Fact]
    public async Task CompletedChildStillCommitsItsTextAsASuccessfulParentToolResult()
    {
        var childModel = new CancellationTestModel(Text("completed child output"));
        await using var child = new Agent(new() { Model = childModel });
        var store = new Store();
        var parentModel = new CancellationTestModel(Call(("child-call", "specialist", "{\"task\":\"request\"}")), Text("handled"));
        await using var parent = new Agent(new() { Model = parentModel, Tools = [child.AsTool("specialist")], SessionStore = store, SessionId = "parent" });

        Assert.Equal("handled", (await parent.InvokeAsync("delegate")).ToString());

        foreach (var messages in new[] { parent.Messages, (await store.LoadAsync("parent"))!.Messages })
        {
            var result = Assert.Single(messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Equal("child-call", result.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal("completed child output", result.Content);
        }
        Assert.Single(childModel.Requests);
        Assert.Empty(child.Messages);
    }

    private static void AssertErrorResult(IReadOnlyList<Message> messages)
    {
        Assert.Equal("child-call", Assert.Single(messages.SelectMany(message => message.Content).OfType<ToolUseBlock>()).ToolUseId);
        var result = Assert.Single(messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("child-call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.DoesNotContain("partial child output", result.Content);
    }

    private sealed class Store : ISessionStore
    {
        private byte[]? _snapshot;

        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_snapshot is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(_snapshot));
        }

        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            return default;
        }
    }
}
