using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class RuntimeCompatibilityTests
{
    [Fact]
    public async Task RunsTypedToolLoopAndPreservesInvocationState()
    {
        var state = new Dictionary<string, object?> { ["caller"] = "framework" };
        var tool = new FunctionTool(new("add", "Add integers", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { x = new { type = "integer" }, y = new { type = "integer" } },
            required = new[] { "x", "y" }
        })), (input, context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            return new ValueTask<string>((input.GetProperty("x").GetInt32() + input.GetProperty("y").GetInt32()).ToString());
        });
        var model = new ToolModel();
        var agent = new Agent(new() { Model = model, Tools = [tool] });

        var result = await agent.InvokeAsync("add", new() { InvocationState = state });

        Assert.Equal("5", result.ToString());
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(2, result.Turns);
        Assert.Same(state, result.InvocationState);
        Assert.Single(state);
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal(new Usage(6, 4), result.Usage);
    }

    [Fact]
    public async Task CancellationAfterHookPreventsSideEffectsAndPairsHistory()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { cancellation.Cancel(); return default; });
        var invoked = false;
        var tool = new FunctionTool(new("add", "Add", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) =>
        {
            invoked = true;
            return new ValueTask<string>("incorrect");
        });
        var agent = new Agent(new() { Model = new ToolModel(), Tools = [tool], Hooks = hooks });

        var result = await agent.InvokeAsync("add", cancellationToken: cancellation.Token);

        Assert.False(invoked);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        var last = agent.Messages[agent.Messages.Count - 1];
        Assert.Equal("call", Assert.IsType<ToolResultBlock>(Assert.Single(last.Content)).ToolUseId);
    }

    [Fact]
    public async Task StreamingDisposalReleasesGuardAndRepairsToolBatch()
    {
        var agent = new Agent(new() { Model = new ToolModel() });
        var iterator = agent.StreamAsync("go").GetAsyncEnumerator();
        try
        {
            while (await iterator.MoveNextAsync())
            {
                if (iterator.Current is MessageAddedEvent { Message.Role: Role.Assistant }) break;
            }
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("overlap"));
        }
        finally { await iterator.DisposeAsync(); }

        Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[agent.Messages.Count - 1].Content));
        Assert.Equal("Tool execution was cancelled before a result was committed.", (await agent.InvokeAsync(null)).ToString());
    }

    private sealed class ToolModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            var previous = request.Messages[request.Messages.Count - 1].Content.OfType<ToolResultBlock>().FirstOrDefault();
            if (previous is null)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", "add", JsonSerializer.SerializeToElement(new { x = 2, y = 3 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: previous.Content);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            yield return new ModelMetadataEvent(new(3, 2));
            await Task.Yield();
        }
    }
}
