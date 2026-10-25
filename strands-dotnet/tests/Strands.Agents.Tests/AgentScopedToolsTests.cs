using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentScopedToolsTests
{
    [Fact]
    public async Task CancellationDrainsTheInvocationToolAndLeavesTheRegistryUnchanged()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var tool = new FunctionTool(Tool("coordination").Spec, async (_, _, token) =>
        {
            try { started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unexpected"; }
            finally { disposed = true; }
        });
        await using var agent = new Agent(new() { Model = new ScriptedModel(Call("coordination")) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocation = agent.InvokeAsync("task", new() { EphemeralTools = [tool] }, cancellation.Token);
        try { Assert.Same(started.Task, await Task.WhenAny(started.Task, invocation)); }
        finally { cancellation.Cancel(); }

        var result = await invocation;

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.True(disposed);
        Assert.Empty(agent.Tools);
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(agent.Messages.Last().Content.Single()).Status);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("plugin")]
    [InlineData("invocation")]
    public async Task DuplicateNamesFailBeforeModelIoAndDoNotRegisterTheBorrowedTool(string source)
    {
        var tool = Tool("coordination");
        var model = new ScriptedModel(Text("unexpected"));
        await using var agent = new Agent(new()
        {
            Model = model,
            Tools = source == "application" ? [tool] : [],
            Plugins = source == "plugin" ? [new Plugin(tool)] : []
        });
        var options = new InvocationOptions { EphemeralTools = source == "invocation" ? [tool, tool] : [tool] };

        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("task", options));

        Assert.Empty(model.Requests);
        Assert.Empty(agent.Messages);
        Assert.Equal(source == "invocation" ? 0 : 1, agent.Tools.Count);
    }

    [Fact]
    public async Task StructuredOutputReservedNameCannotBeReplacedByAnInvocationTool()
    {
        var model = new ScriptedModel(Text("unexpected"));
        await using var agent = new Agent(new() { Model = model });

        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeStructuredAsync("task", new StructuredOutputOptions<JsonElement>
        { Schema = JsonSerializer.SerializeToElement(new { type = "object" }) }, new() { EphemeralTools = [Tool(StructuredOutputTool<JsonElement>.ToolName)] }));

        Assert.Empty(model.Requests);
        Assert.Empty(agent.Messages);
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task ToolListIsCopiedBeforeAwaitedInitialization()
    {
        var tools = new List<ITool> { Tool("coordination") };
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>(async (_, _) => { await Task.Yield(); tools.Clear(); });
        var model = new ScriptedModel(Call("coordination"), Text("done"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("task", new() { EphemeralTools = tools });

        Assert.Empty(tools);
        Assert.Equal("coordination", Assert.Single(model.Requests[0].Tools!).Name);
        Assert.Equal(ToolResultStatus.Success, Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content.Single()).Status);
    }

    [Fact]
    public async Task RegistrationFilterAlsoControlsInvocationCapabilities()
    {
        var calls = 0;
        var model = new ScriptedModel(Call("coordination"), Text("done"));
        var tool = new FunctionTool(Tool("coordination").Spec, (_, _, _) => { calls++; return new("unexpected"); });
        await using var agent = new Agent(new() { Model = model, ToolFilter = _ => false });

        await agent.InvokeAsync("task", new() { EphemeralTools = [tool] });

        Assert.Empty(model.Requests[0].Tools!);
        Assert.Equal(0, calls);
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content.Single()).Status);
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task CompletedInvocationDoesNotLeakItsToolIntoLaterInvocationsOrChildren()
    {
        var calls = 0;
        var tool = new BorrowedTool(() => calls++);
        var parentModel = new ScriptedModel(Call("coordination"), Text("first"), Call("coordination"), Text("second"));
        var childModel = new ScriptedModel(Call("coordination"), Text("child"));
        await using (var parent = new Agent(new() { Model = parentModel }))
        {
            await parent.InvokeAsync("first", new() { EphemeralTools = [tool] });
            await parent.InvokeAsync("second");
            await using var child = parent.CreateChild(options => options with { Model = childModel });
            await child.InvokeAsync("child");
            Assert.Empty(parent.Tools);
            Assert.Empty(child.Tools);
            Assert.Empty(parentModel.Requests[2].Tools!);
            Assert.Empty(childModel.Requests[0].Tools!);
        }

        Assert.Equal(1, calls);
        Assert.Equal(0, tool.Disposals);
    }

    [Fact]
    public async Task RebindingTheSameBorrowedToolResumesItsDurableInterruptWithoutReplayingEffects()
    {
        var effects = 0;
        var caller = new Dictionary<string, object?> { ["caller"] = 7 };
        var tool = new FunctionTool(Tool("coordination").Spec, (_, context, _) =>
        {
            Assert.Same(caller, context.InvocationState);
            Assert.True(context.Interrupt("approval").GetBoolean());
            effects++;
            return new("approved");
        });
        var model = new ScriptedModel(Call("coordination"), Text("done"));
        await using var agent = new Agent(new() { Model = model });
        var options = new InvocationOptions { EphemeralTools = [tool], InvocationState = caller };
        var paused = await agent.InvokeAsync("task", options);
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);

        var result = await agent.InvokeAsync(null, options with { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))] });

        Assert.Equal("done", result.ToString());
        Assert.Equal(1, effects);
        Assert.Null(agent.InterruptState);
        Assert.Empty(agent.Tools);
        Assert.Equal("coordination", Assert.Single(model.Requests[1].Tools!).Name);
    }

    [Fact]
    public async Task BorrowedStreamingToolRetainsCorrelatedProgressAndRichTerminalContent()
    {
        var tool = new StreamingFunctionTool(Tool("coordination").Spec, (_, context, token) => Stream(context, token));
        var model = new ScriptedModel(Call("coordination"), Text("done"));
        await using var agent = new Agent(new() { Model = model });
        var progress = new List<ToolStreamUpdateEvent>();
        await foreach (var item in agent.StreamAsync("task", new() { EphemeralTools = [tool] }))
            if (item is ToolStreamUpdateEvent update) progress.Add(update);

        Assert.Equal("call", Assert.Single(progress).ToolUse.ToolUseId);
        var committed = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content.Single());
        Assert.Equal("call", committed.ToolUseId);
        Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(committed.ContentBlocks)).Json.GetInt32());
        Assert.Empty(agent.Tools);

        static async IAsyncEnumerable<IToolStreamEvent> Stream(ToolContext context, [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ToolStreamEvent("working");
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, [new JsonBlock(JsonSerializer.SerializeToElement(42))]));
        }
    }

    [Fact]
    public async Task InvocationAdvertisesAndExecutesItsBorrowedToolWithoutRegisteringIt()
    {
        var calls = 0;
        var model = new ScriptedModel(Call("coordination"), Text("done"));
        var tool = new FunctionTool(new("coordination", "Coordinate this invocation", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { calls++; return new("accepted"); });
        await using var agent = new Agent(new() { Model = model });

        var result = await agent.InvokeAsync("task", new() { EphemeralTools = [tool] });

        Assert.Equal("coordination", Assert.Single(model.Requests[0].Tools!).Name);
        Assert.Equal("done", result.ToString());
        Assert.Equal(1, calls);
        Assert.Empty(agent.Tools);
        var committed = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content.Single());
        Assert.Equal("call", committed.ToolUseId);
        Assert.Equal("accepted", committed.Content);
    }

    private static ModelStreamEvent[] Call(string name) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call", name, JsonSerializer.SerializeToElement(new { }))),
        new ModelContentBlockDeltaEvent(0, ToolInput: "{}"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static FunctionTool Tool(string name) => new(new(name, "Coordinate this invocation", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("accepted"));
    private sealed class Plugin(ITool tool) : IPlugin
    {
        public string Name => "scoped-tools-test";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => default;
        public IReadOnlyList<ITool> GetTools() => [tool];
    }
    private sealed class BorrowedTool(Action callback) : ITool, IAsyncDisposable
    {
        public ToolSpec Spec { get; } = Tool("coordination").Spec;
        internal int Disposals { get; private set; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { callback(); return new("accepted"); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
    }
}
