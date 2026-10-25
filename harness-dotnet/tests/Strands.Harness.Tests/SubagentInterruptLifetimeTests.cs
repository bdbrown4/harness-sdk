using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SubagentInterruptLifetimeTests
{
    [Theory]
    [InlineData(StopReasons.Cancelled)]
    [InlineData(StopReasons.ContentFiltered)]
    [InlineData("refusal")]
    [InlineData(StopReasons.MaxTurns)]
    [InlineData(StopReasons.MaxTokens)]
    [InlineData(StopReasons.MaxTotalTokens)]
    [InlineData(StopReasons.MaxOutputTokens)]
    public async Task StreamingRetainsEveryHarnessTerminalFailurePolicy(string reason)
    {
        var tool = Subagent.Create(new()
        {
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Messages = spec.Messages,
                Model = new BridgeSupport.Events(_ =>
                [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "partial"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(reason)])
            })),
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.Text("unused") });
        var result = await tool.InvokeResultAsync(BridgeSupport.TaskInput, new(parent, new("call", "subagent", BridgeSupport.TaskInput), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("stopReason=" + reason, result.Content); Assert.Contains("Partial output:\npartial", result.Content);
    }

    [Fact]
    public async Task RestoredDepthIsCappedAndTheTemporaryReadinessHookIsRemoved()
    {
        var hooks = new HookRegistry(); var depths = new List<int>();
        var approve = new FunctionTool(new("approve", "Approve", BridgeSupport.EmptyInput), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            MaxDepth = 3,
            Tools = new Fixed(new[] { "approve" }),
            InheritedTools = ["approve"],
            Builder = (spec, _, _) =>
            {
                Agent? child = null;
                var model = new BridgeSupport.Events(request =>
                {
                    depths.Add(child!.State.Get("subagentDepth")!.Value.GetInt32());
                    return request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true ? BridgeSupport.Answer("done") : BridgeSupport.Call("child", "approve", BridgeSupport.EmptyInput);
                });
                child = new(new() { Model = model, Tools = [approve], Messages = spec.Messages, Hooks = hooks }); return new(child);
            },
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.ToolOrEcho("call", "delegate_alias", BridgeSupport.TaskInput), Tools = [approve, tool] });
        var pause = await parent.InvokeAsync("request"); Assert.Equal(2, Assert.Single(depths));
        parent.State.Set("subagentDepth", 1);
        var result = await parent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(pause.Interrupts).Id, JsonSerializer.SerializeToElement("approved"))] });
        Assert.Equal("done", result.ToString()); Assert.Equal(new[] { 2, 0 }, depths);
        parent.State.Set("subagentDepth", 0);
        await using var unrelated = new Agent(new() { Model = BridgeSupport.Text("probe"), Hooks = hooks });
        Assert.Equal("probe", (await unrelated.InvokeAsync("probe")).ToString()); Assert.Null(unrelated.State.Get("subagentDepth"));
    }

    [Fact]
    public async Task ClosingTheParentStreamJoinsItsOwnedChildAndRunsNoHiddenBody()
    {
        var disposed = 0; var continued = 0;
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new() { Model = new GatedStream(() => continued++), Messages = spec.Messages, OwnedResources = [new BridgeSupport.Resource(() => disposed++)] })),
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.ToolOrEcho("call", "delegate_alias", BridgeSupport.TaskInput), Tools = [tool] });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var iterator = parent.StreamAsync("request", cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        try
        {
            while (await iterator.MoveNextAsync())
                if (iterator.Current is ToolStreamUpdateEvent { Event.Data: ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent } }) break;
        }
        finally { await iterator.DisposeAsync(); }
        Assert.Equal(1, disposed); Assert.Equal(0, continued); Assert.DoesNotContain(parent.Messages, message => message.Role == Role.Assistant && message.Text == "unreachable");
    }

    [Fact]
    public async Task AnApplicationMetadataChangeAfterRestoreFailsAuthorityBeforeTheModelRuns()
    {
        var mutable = new MutableApproval(); var modelCalls = 0;
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(new[] { "approve" }),
            InheritedTools = ["approve"],
            Builder = (spec, _, _) =>
            {
                var child = new Agent(new()
                {
                    Model = new BridgeSupport.Events(request =>
                { modelCalls++; return BridgeSupport.Call("child", "approve", BridgeSupport.EmptyInput); }),
                    Tools = [mutable],
                    Messages = spec.Messages
                });
                child.Hooks.Register<BeforeInvocationEvent>((value, _) => { if (value.Agent.InterruptState is not null) mutable.Name = "forbidden"; return default; });
                return new(child);
            },
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.ToolOrEcho("call", "delegate_alias", BridgeSupport.TaskInput), Tools = [mutable, tool] });
        var paused = await parent.InvokeAsync("request"); Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var done = await parent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("approved"))] });
        Assert.Contains("exceeds the delegated tool subset", done.ToString()); Assert.Equal(1, modelCalls); Assert.Equal(0, mutable.Bodies);
    }

    [Fact]
    public async Task ABuilderReturningTheParentNeverTransfersItsOwnership()
    {
        await using var parent = new Agent(new() { Model = BridgeSupport.Text("still alive") });
        var tool = Subagent.Create(new() { Tools = new Fixed(Array.Empty<string>()), Builder = (_, _, _) => new(parent) });
        var result = await tool.InvokeResultAsync(BridgeSupport.TaskInput, new(parent, new("call", "subagent", BridgeSupport.TaskInput), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("fresh child", result.Content);
        Assert.Equal("still alive", (await parent.InvokeAsync("probe")).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildFailureRetainsItsExactCauseAndCleanupWithoutSelfAttachment(bool sameFailure)
    {
        var primary = new InvalidOperationException("child body failed");
        Exception cleanup = sameFailure ? primary : new IOException("child cleanup failed");
        var disposed = 0;
        var tool = Subagent.Create(new()
        {
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(new Agent(new()
            {
                Messages = spec.Messages,
                Model = new BridgeSupport.Events(_ => throw primary),
                OwnedResources = [new BridgeSupport.Resource(() => { disposed++; throw cleanup; })]
            })),
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.Text("unused") });
        var result = await tool.InvokeResultAsync(BridgeSupport.TaskInput, new(parent, new("call", "subagent", BridgeSupport.TaskInput), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal("Subagent error: child body failed", result.Content); Assert.Same(primary, result.Error); Assert.Equal(1, disposed);
        if (sameFailure) Assert.Null(primary.Data["Strands.CleanupErrors"]);
        else Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    private sealed class MutableApproval : ITool
    {
        internal string Name { get; set; } = "approve";
        internal int Bodies { get; private set; }
        public ToolSpec Spec => new(Name, "Approval", BridgeSupport.EmptyInput);
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { var answer = context.Interrupt("approval"); Bodies++; return new(answer.GetString()!); }
    }

    private sealed class GatedStream(Action continued) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "child-progress");
            await Task.Delay(Timeout.Infinite, cancellationToken); continued(); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
