using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class AgentDelegationTests
{
    [Fact]
    public async Task SuccessfulDelegationReturnsRichContentWithoutAnotherParentModelCall()
    {
        var childModel = new Model(Array.Empty<ToolUseBlock>(), new ContentBlock[] { new TextBlock("specialist"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 7 })) });
        await using var child = new Agent(new() { Model = childModel, Name = "specialist" });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true });
        var model = new Model(new[] { Call("delegate", tool.Spec.Name) });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("delegate", cancellationToken: deadline.Token);
        Assert.Equal(1, model.Calls); Assert.Equal("specialist", result.ToString());
        Assert.Equal(7, Assert.Single(result.Message!.Content.OfType<JsonBlock>()).Json.GetProperty("value").GetInt32());
        Assert.Equal(Role.Assistant, parent.Messages.Last().Role);
        Assert.Single(parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task MixedDelegationBatchDoesNotExecuteAnyBody(ToolExecutionStrategy strategy)
    {
        var childModel = new Model(Array.Empty<ToolUseBlock>());
        await using var child = new Agent(new() { Model = childModel });
        var delegated = child.AsTool(new AgentToolOptions { Name = "specialist", Delegate = true });
        var bodies = 0;
        var ordinary = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("ordinary"); });
        var model = new Model(new[] { Call("delegate", delegated.Spec.Name), Call("ordinary", ordinary.Spec.Name) });
        await using var parent = new Agent(new() { Model = model, Tools = new ITool[] { delegated, ordinary }, ToolExecution = strategy });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("mixed", cancellationToken: deadline.Token);
        Assert.Equal("parent fallback", result.ToString()); Assert.Equal(0, childModel.Calls); Assert.Equal(0, bodies);
        var results = parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "delegate", "ordinary" }, results.Select(value => value.ToolUseId));
        Assert.All(results, value => Assert.Equal(ToolResultStatus.Error, value.Status));
    }

    [Fact]
    public async Task BeforeToolsCanDenyAnEntireBatchWithCorrelatedResults()
    {
        var bodies = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("ordinary"); });
        var model = new Model(new[] { Call("one", "work"), Call("two", "work") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { work } });
        parent.Hooks.Register<BeforeToolsEvent>((value, _) => { value.CancelReason = "batch denied"; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await parent.InvokeAsync("work", cancellationToken: deadline.Token);
        Assert.Equal(0, bodies);
        var results = parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(2, results.Length); Assert.All(results, value => { Assert.Equal(ToolResultStatus.Error, value.Status); Assert.Equal("batch denied", value.Content); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterToolsCanClearOrReplaceDelegationContent(bool clear)
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var model = new Model(new[] { Call("delegate", "specialist") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool } });
        parent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = clear ? null : new ContentBlock[] { new TextBlock("replacement") }; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("delegate", cancellationToken: deadline.Token);
        Assert.Equal(clear ? 2 : 1, model.Calls); Assert.Equal(clear ? "parent fallback" : "replacement", result.ToString());
    }

    [Fact]
    public async Task StatefulParentsRejectInitialAndRuntimeDelegationAtomically()
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true });
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new StatefulModel(), Tools = new[] { tool } }));
        await using var parent = new Agent(new() { Model = new StatefulModel() });
        Assert.Throws<ArgumentException>(() => parent.AddTools(new[] { tool })); Assert.Empty(parent.Tools);
    }

    [Fact]
    public async Task DelegateModelEventsAppearNativelyWithoutADuplicateChildFinalResult()
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var model = new Model(new[] { Call("delegate", "specialist") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool } });
        var events = new List<IAgentStreamEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in parent.StreamAsync("delegate", cancellationToken: deadline.Token)) events.Add(item);
        Assert.Equal(2, events.OfType<ModelStreamUpdateEvent>().Count(value => value.Event is ModelMessageStartEvent));
        Assert.Single(events.OfType<AgentResultEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndFailedDelegationContinueTheParentModel(bool fail)
    {
        IModel childModel = fail ? new FailureModel() : new Model(Array.Empty<ToolUseBlock>(), new ContentBlock[] { new TextBlock("") });
        await using var child = new Agent(new() { Model = childModel });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var model = new Model(new[] { Call("delegate", "specialist") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("delegate", cancellationToken: deadline.Token);
        Assert.Equal(2, model.Calls); Assert.Equal("parent fallback", result.ToString());
        Assert.Equal(fail ? ToolResultStatus.Error : ToolResultStatus.Success, Assert.Single(parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryUsesTheFinalEffectiveToolRatherThanAStaleDelegationMark(bool finalDelegate)
    {
        var childModel = new Model(Array.Empty<ToolUseBlock>(), new ContentBlock[] { new TextBlock("specialist") });
        await using var child = new Agent(new() { Model = childModel });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var work = new FunctionTool(new("router", "Router", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ordinary"));
        var model = new Model(new[] { Call("call", "router") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { work } });
        var attempt = 0;
        parent.Hooks.Register<BeforeToolCallEvent>((value, _) => { value.SelectedTool = (++attempt == 2) == finalDelegate ? tool : work; return default; });
        parent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = attempt == 1; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeAsync("route", cancellationToken: deadline.Token);
        Assert.Equal(finalDelegate ? 1 : 2, model.Calls); Assert.Equal(finalDelegate ? "specialist" : "parent fallback", result.ToString());
        Assert.Equal(2, attempt); Assert.Equal(1, childModel.Calls);
        Assert.Single(parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Fact]
    public async Task InvalidEndTurnCannotAppendUnpairedToolProtocolContent()
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        await using var parent = new Agent(new() { Model = new Model(new[] { Call("delegate", "specialist") }), Tools = new[] { tool } });
        parent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = new ContentBlock[] { new ToolResultBlock("forged", "bad") }; return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ArgumentException>(() => parent.InvokeAsync("delegate", cancellationToken: deadline.Token));
        Assert.Equal(Role.User, parent.Messages.Last().Role);
        Assert.Equal("delegate", Assert.Single(parent.Messages.Last().Content.OfType<ToolResultBlock>()).ToolUseId);
    }

    [Fact]
    public async Task DelegationAlwaysRemainsForegroundEvenUnderAnAlwaysWildcard()
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var model = new Model(new[] { Call("delegate", "specialist") });
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool }, BackgroundTasks = new() { Always = new[] { "*" } } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await parent.InvokeAsync("delegate", cancellationToken: deadline.Token);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Content.Contains("Background task dispatched"));
    }

    [Fact]
    public async Task StructuredParentsRequireTheirOwnValidatedOutputAfterDelegation()
    {
        await using var child = new Agent(new() { Model = new Model(Array.Empty<ToolUseBlock>()) });
        var tool = child.AsTool(new AgentToolOptions { Delegate = true, Name = "specialist" });
        var model = new FormatModel();
        await using var parent = new Agent(new() { Model = model, Tools = new[] { tool } });
        var after = 0;
        parent.Hooks.Register<AfterToolsEvent>((value, _) => { if (++after == 1) Assert.Null(value.EndTurn); return default; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeStructuredAsync<Output>("format", new()
        {
            Schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { value = new { type = "integer" } },
                required = new[] { "value" },
                additionalProperties = false
            })
        }, cancellationToken: deadline.Token);
        Assert.Equal(9, result.Output.Value); Assert.Equal(2, model.Calls);
    }

    public sealed record Output(int Value);

    private static ToolUseBlock Call(string id, string tool) => new(id, tool, JsonSerializer.SerializeToElement(new { input = "task" }));

    private sealed class StatefulModel : IStatefulModel
    {
        public bool Stateful => true;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not called");
    }

    private sealed class FailureModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("child failed");
    }

    private sealed class FormatModel : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            var call = Calls == 1 ? Call("delegate", "specialist") : new ToolUseBlock("format", "strands_structured_output", JsonSerializer.SerializeToElement(new { value = 9 }));
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0, call);
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class Model(IReadOnlyList<ToolUseBlock> calls, IReadOnlyList<ContentBlock>? content = null) : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1 && calls.Count > 0)
            {
                for (var index = 0; index < calls.Count; index++)
                { yield return new ModelContentBlockStartEvent(index, calls[index]); yield return new ModelContentBlockStopEvent(index); }
            }
            else
            {
                var blocks = content ?? new ContentBlock[] { new TextBlock("parent fallback") };
                for (var index = 0; index < blocks.Count; index++)
                { yield return new ModelContentBlockStartEvent(index) { ContentBlock = blocks[index] }; yield return new ModelContentBlockStopEvent(index); }
            }
            yield return new ModelMessageStopEvent(Calls == 1 && calls.Count > 0 ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }
}
