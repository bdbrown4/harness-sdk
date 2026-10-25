using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class StructuredOutputCompatibilityTests
{
    [Fact]
    public async Task ValidatesTypedStructuredOutputThroughStandardAsset()
    {
        var tool = new StructuredOutputTool<Answer>(new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } })
        });
        var agent = new Agent(new() { Model = new TextModel() });
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 42 }), new(agent, new("id", tool.Spec.Name, JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>()));
        Assert.True(tool.HasResult);
        Assert.Equal(42, tool.Output!.Value);
    }

    [Fact]
    public async Task RetriesAndRoutesWithoutLeakingStateIntoCallerDictionary()
    {
        var state = new Dictionary<string, object?> { ["owned"] = 1 };
        var router = new RoutingModel([new(new FailingModel()), new(new TextModel())]);
        var retry = new RetryModel(router, new() { Backoff = new ConstantBackoff(TimeSpan.Zero) });
        var scoped = retry.CreateInvocationModel(state);
        var agent = new Agent(new() { Model = scoped });
        Assert.Equal("done", (await agent.InvokeAsync("go")).ToString());
        Assert.Single(state);
    }

    [Fact]
    public async Task AgentForcesThenCorrectsStructuredResponseWithoutExtraCompletionTurn()
    {
        var model = new StructuredModel();
        var agent = new Agent(new() { Model = model });
        var state = new Dictionary<string, object?> { ["caller"] = "legacy" };
        var result = await agent.InvokeStructuredAsync("answer", new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer", minimum = 1 } }, required = new[] { "value" } })
        }, new() { InvocationState = state });
        Assert.Equal(42, result.Output.Value);
        Assert.Equal(3, result.Result.Turns);
        Assert.Same(state, result.Result.InvocationState);
        Assert.Equal(new Usage(6, 3), result.Result.Usage);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(new(ToolChoiceMode.Tool, "strands_structured_output"), model.Requests[1].ToolChoice);
        Assert.Equal(ToolResultStatus.Error, model.Requests[2].Messages.Last().Content.OfType<ToolResultBlock>().Single().Status);
        Assert.Empty(agent.Tools);
        Assert.Equal(ToolResultStatus.Success, agent.Messages.Last().Content.OfType<ToolResultBlock>().Single().Status);
    }

    [Fact]
    public async Task AgentChoosesFirstSuccessfulStructuredCallInAssistantOrder()
    {
        var hooks = new HookRegistry();
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Register<BeforeToolCallEvent>(async (value, _) =>
        {
            if (value.ToolUse.ToolUseId == "first") await secondFinished.Task;
        });
        hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            if (value.ToolUse.ToolUseId == "second") secondFinished.TrySetResult(true);
            return default;
        });
        var agent = new Agent(new() { Model = new BatchModel(), Hooks = hooks });
        var result = await agent.InvokeStructuredAsync("answer", new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } })
        });
        Assert.Equal(1, result.Output.Value);
        Assert.Equal(StopReasons.ToolUse, result.Result.StopReason);
        Assert.Equal(2, agent.Messages.Last().Content.OfType<ToolResultBlock>().Count());
    }

    [Fact]
    public async Task HookRejectedStructuredOutputsCannotReturnTypedSuccess()
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            value.Result = value.Result with { Status = ToolResultStatus.Error, Content = "policy rejected" };
            return default;
        });
        var agent = new Agent(new() { Model = new BatchModel(), Hooks = hooks });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("answer", new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object" })
        }, new() { MaxTurns = 1 }));
        Assert.All(agent.Messages.Last().Content.OfType<ToolResultBlock>(), result =>
        {
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal("policy rejected", result.Content);
        });
    }

    [Fact]
    public async Task SelectsFirstStructuredOutputWhoseCommittedResultWasNotRejected()
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            if (value.ToolUse.ToolUseId == "first") value.Result = value.Result with { Status = ToolResultStatus.Error };
            return default;
        });
        var agent = new Agent(new() { Model = new BatchModel(), Hooks = hooks });
        var result = await agent.InvokeStructuredAsync("answer", new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object" })
        });
        Assert.Equal(2, result.Output.Value);
        Assert.Equal(StopReasons.ToolUse, result.Result.StopReason);
        Assert.Equal(new[] { ToolResultStatus.Error, ToolResultStatus.Success }, agent.Messages.Last().Content.OfType<ToolResultBlock>().Select(value => value.Status));
    }

    [Fact]
    public async Task BatchHookCannotReplaceProviderCallsThroughMutableListCast()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolsEvent>((value, _) =>
        {
            var calls = Assert.IsAssignableFrom<IList<ToolUseBlock>>(value.Tools);
            Assert.Throws<NotSupportedException>(() => calls[0] = calls[0] with { ToolUseId = "corrupted" });
            return default;
        });
        var agent = new Agent(new() { Model = new BatchModel(), Hooks = hooks });
        await agent.InvokeAsync("answer", new() { MaxTurns = 1 });
        Assert.Equal(new[] { "first", "second" }, agent.Messages.Last().Content.OfType<ToolResultBlock>().Select(value => value.ToolUseId));
    }

    [Fact]
    public async Task CancellationAfterTypedCommitCannotReturnStructuredSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => { cancellation.Cancel(); return default; });
        var agent = new Agent(new() { Model = new BatchModel(), Hooks = hooks });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync("answer", new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object" })
        }, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, agent.Messages.Last().Content.OfType<ToolResultBlock>().Count());
    }

    private sealed class BatchModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("first", "strands_structured_output", JsonSerializer.SerializeToElement(new { value = 1 })));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelContentBlockStartEvent(1, new("second", "strands_structured_output", JsonSerializer.SerializeToElement(new { value = 2 })));
            yield return new ModelContentBlockStopEvent(1);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            await Task.Yield();
        }
    }

    private sealed class StructuredModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Requests.Count;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (index == 1)
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "plain answer");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0, new("structured-" + index, "strands_structured_output", JsonSerializer.SerializeToElement(new { value = index == 2 ? -1 : 42 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            yield return new ModelMetadataEvent(new(2, 1));
            await Task.Yield();
        }
    }

    public sealed record Answer(int Value);
    private sealed class FailingModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new ModelException("failover");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.Yield();
        }
    }
}
