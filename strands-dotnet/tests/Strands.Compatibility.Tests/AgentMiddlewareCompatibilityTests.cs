using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Middleware;

namespace Strands.Compatibility.Tests;

public sealed class AgentMiddlewareCompatibilityTests
{
    [Fact]
    public async Task CacheAndOutputTransformationStayWithinOneHookPairOnThePortableAsset()
    {
        var model = new Model(Text("unused")); var middleware = new MiddlewareRegistry(); var order = new List<string>(); var state = new Dictionary<string, object?>();
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
        middleware.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => { order.Add("output"); return new(result with { Message = Message.Assistant("rewritten"), Usage = new(4, 5) }); });
        var hooks = new HookRegistry(); hooks.Register<BeforeModelCallEvent>((_, _) => { order.Add("before"); return default; });
        hooks.Register<AfterModelCallEvent>((value, _) => { order.Add("after"); Assert.Equal("rewritten", value.Message!.Text); return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = middleware, Hooks = hooks });

        var result = await agent.InvokeAsync("question", new() { InvocationState = state });

        Assert.Equal("rewritten", result.ToString()); Assert.Equal(new Usage(4, 5), result.Usage); Assert.Empty(model.Requests);
        Assert.Same(state, result.InvocationState); Assert.Empty(state); Assert.Equal(new[] { "before", "cache", "output", "after" }, order);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; order.Add("cache"); yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }
    }

    [Fact]
    public async Task ToolInputAndOutputPreserveModelCorrelationAndEmitOneTerminal()
    {
        var middleware = new MiddlewareRegistry(); var bodies = 0; var after = 0; var results = 0;
        middleware.RegisterInput(MiddlewareStages.ExecuteTool, (context, _) => new(context with { ToolUse = context.ToolUse with { ToolUseId = "invalid", Input = JsonSerializer.SerializeToElement(new { value = 2 }) } }));
        middleware.RegisterOutput(MiddlewareStages.ExecuteTool, (result, _) => new(result with { Result = new("invalid", "output") }));
        var hooks = new HookRegistry(); hooks.Register<AfterToolCallEvent>((value, _) => { after++; Assert.Equal("call", value.Result.ToolUseId); return default; });
        hooks.Register<ToolResultEvent>((value, _) => { results++; Assert.Equal("output", value.Result.Content); return default; });
        var tool = new FunctionTool(Spec("echo"), (input, context, _) => { bodies++; Assert.Equal(2, input.GetProperty("value").GetInt32()); Assert.Equal("call", context.ToolUse.ToolUseId); return new("body"); });
        await using var agent = new Agent(new() { Model = new Model(Call("echo"), Text("done")), Middleware = middleware, Hooks = hooks, Tools = new[] { tool } });
        Assert.Equal("done", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(1, bodies); Assert.Equal(1, after); Assert.Equal(1, results);
        Assert.Equal("output", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task FilteredStreamingToolsCanInjectOrdinaryEventsWithoutBypassingTheValidatedRunner()
    {
        var middleware = new MiddlewareRegistry(); middleware.RegisterWrap(MiddlewareStages.ExecuteTool, Filter);
        var tool = new StreamingFunctionTool(Spec("stream"), (_, context, token) => Stream(context.ToolUse.ToolUseId, token));
        await using var agent = new Agent(new() { Model = new Model(Call("stream"), Text("done")), Middleware = middleware, Tools = new[] { tool } });
        var events = new List<IAgentStreamEvent>(); await foreach (var item in agent.StreamAsync("question")) events.Add(item);
        Assert.Single(events.OfType<CustomUpdate>()); Assert.DoesNotContain(events, item => item is ToolStreamUpdateEvent); Assert.Single(events.OfType<ToolResultEvent>());
        Assert.Equal("complete", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Filter(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { yield return new MiddlewareStreamEvent<ExecuteToolResult>(new CustomUpdate()); await foreach (var item in next(context)) if (item is MiddlewareResultEvent<ExecuteToolResult>) yield return item; }
        async IAsyncEnumerable<IToolStreamEvent> Stream(string id, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new ToolStreamEvent("working"); yield return new ToolStreamResultEvent(new(id, "complete")); }
    }

    [Fact]
    public async Task CancellationNeverReplacesTheResultOfAnAlreadyCompletedSideEffect()
    {
        using var cancellation = new CancellationTokenSource(); var effects = 0; var after = 0;
        var hooks = new HookRegistry(); hooks.Register<AfterToolCallEvent>((value, _) => { after++; Assert.Equal("saved", value.Result.Content); return default; });
        var tool = new FunctionTool(Spec("save"), (_, _, _) => { effects++; cancellation.Cancel(); return new("saved"); });
        await using var agent = new Agent(new() { Model = new Model(Call("save")), Hooks = hooks, Tools = new[] { tool } });
        var result = await agent.InvokeAsync("question", cancellationToken: cancellation.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason); Assert.Equal(1, effects); Assert.Equal(1, after);
        Assert.Equal("saved", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task PluginRegistrationsAreReinitializedForChildrenAndRemovedOnlyFromTheirOwner()
    {
        var application = new MiddlewareRegistry(); var appCalls = 0; var plugin = new Plugin();
        application.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { appCalls++; return new(context); });
        var parent = new Agent(new() { Model = new Model(Text("answer")), Middleware = application, Plugins = new[] { plugin } });
        await parent.InvokeAsync("question"); await using var child = parent.CreateChild(); await child.InvokeAsync("question");
        Assert.Equal(2, appCalls); Assert.Equal(2, plugin.Calls); Assert.Equal(2, plugin.Initializations);
        await parent.DisposeAsync(); await child.InvokeAsync("another");
        Assert.Equal(3, appCalls); Assert.Equal(3, plugin.Calls);
    }

    [Fact]
    public async Task MiddlewareErrorsKeepTheirIdentityAndSecondaryInvocationCleanup()
    {
        var primary = new IOException("middleware"); var secondary = new InvalidOperationException("after invocation"); var middleware = new MiddlewareRegistry();
        middleware.RegisterOutput<InvokeModelContext, InvokeModelResult>(MiddlewareStages.InvokeModel, (_, _) => throw primary);
        var hooks = new HookRegistry(); hooks.Register<AfterInvocationEvent>((_, _) => throw secondary);
        await using var agent = new Agent(new() { Model = new Model(Text("uncommitted")), Middleware = middleware, Hooks = hooks });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])); Assert.Single(agent.Messages);
    }

    [Theory]
    [InlineData(123)]
    [InlineData(-1)]
    public async Task InputProjectionUsesTheModelCapabilityAndLeavesFailedEstimatesOptional(long estimate)
    {
        var model = new CountingModel(estimate); var middleware = new MiddlewareRegistry();
        middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { Assert.Equal(estimate >= 0 ? estimate : (long?)null, context.ProjectedInputTokens); return new(context); });
        await using var agent = new Agent(new() { Model = model, Middleware = middleware });
        Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString()); Assert.Equal(1, model.Counts);
    }

    private sealed record CustomUpdate : IAgentStreamEvent;
    private sealed class CountingModel(long estimate) : IModel, IModelTokenCounter
    {
        public int Counts { get; private set; }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) { Counts++; return new(estimate); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; foreach (var item in Text("answer")) yield return item; }
    }
    private sealed class Plugin : IPlugin
    {
        public int Calls, Initializations;
        public string Name => "test:portable-middleware";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { Initializations++; agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { Calls++; return new(context); }); return default; }
        public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
    }
    private static ToolSpec Spec(string name) => new(name, "Test tool", JsonSerializer.SerializeToElement(new { type = "object" }));
    private static ModelStreamEvent[] Text(string value) => new ModelStreamEvent[] { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn) };
    private static ModelStreamEvent[] Call(string name) => new ModelStreamEvent[] { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call", name, JsonSerializer.SerializeToElement(new { value = 1 }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse) };
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; Requests.Add(request); foreach (var item in turns[Math.Min(Requests.Count - 1, turns.Length - 1)]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; } }
    }
}
