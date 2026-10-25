using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Middleware;

public sealed class AgentMiddlewareTests
{
    [Fact]
    public async Task ModelTransformationsAreAuthoritativeAndRemainInsideOneHookPair()
    {
        var model = new ScriptModel(Text("provider")); var order = new List<string>(); var state = new Dictionary<string, object?>();
        var registry = new MiddlewareRegistry();
        registry.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { order.Add("input"); Assert.Same(state, context.InvocationState); return new(context with { SystemPrompt = "modified" }); });
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Wrap);
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => { order.Add("output"); return new(result with { Message = Message.Assistant("authoritative"), Usage = new(2, 3) }); });
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => { order.Add("before"); return default; });
        hooks.Register<AfterModelCallEvent>((value, _) => { order.Add("after"); Assert.Equal("authoritative", value.Message!.Text); return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = registry, Hooks = hooks });

        var result = await agent.InvokeAsync("question", new() { InvocationState = state });

        Assert.Equal("authoritative", result.ToString()); Assert.Equal(new Usage(2, 3), result.Usage);
        Assert.Equal("modified", Assert.Single(model.Requests).SystemPrompt);
        Assert.Equal("authoritative", agent.Messages.Last().Text); Assert.Same(state, result.InvocationState); Assert.Empty(state);
        Assert.Equal(new[] { "before", "input", "wrap-before", "wrap-after", "output", "after" }, order);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Wrap(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { order.Add("wrap-before"); await foreach (var item in next(context)) yield return item; order.Add("wrap-after"); }
    }

    [Fact]
    public async Task ModelCacheSkipsTheProviderAndStillRunsBothHooks()
    {
        var model = new ScriptModel(Text("must not run")); var before = 0; var after = 0; var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => { before++; return default; });
        hooks.Register<AfterModelCallEvent>((_, _) => { after++; return default; });
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
        await using var agent = new Agent(new() { Model = model, Middleware = registry, Hooks = hooks });
        Assert.Equal("cached", (await agent.InvokeAsync("question")).ToString());
        Assert.Empty(model.Requests); Assert.Equal(1, before); Assert.Equal(1, after);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }
    }

    [Fact]
    public async Task ExplicitMiddlewareRetryCallsTheProviderTwiceButHooksOnce()
    {
        var transient = new IOException("retryable"); var model = new ScriptModel(Text("recovered")) { FirstFailure = transient };
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Retry);
        var before = 0; var after = 0; var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => { before++; return default; }); hooks.Register<AfterModelCallEvent>((_, _) => { after++; return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = registry, Hooks = hooks });

        Assert.Equal("recovered", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(2, model.Requests.Count); Assert.Equal(1, before); Assert.Equal(1, after);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Retry(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using (var first = next(context).GetAsyncEnumerator(cancellationToken))
                Assert.Same(transient, await Assert.ThrowsAsync<IOException>(() => first.MoveNextAsync().AsTask()));
            await foreach (var item in next(context)) yield return item;
        }
    }

    [Fact]
    public async Task PerCallModelReplacementBorrowsTheReplacementWithoutMutatingTheAgent()
    {
        var main = new ScriptModel(Text("main")); var replacement = new ScriptModel(Text("replacement"));
        var registry = new MiddlewareRegistry(); registry.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new(context with { Model = replacement }));
        await using var agent = new Agent(new() { Model = main, Middleware = registry });
        Assert.Equal("replacement", (await agent.InvokeAsync("question")).ToString());
        Assert.Same(main, agent.Model); Assert.Empty(main.Requests); Assert.Single(replacement.Requests);
        await agent.DisposeAsync(); Assert.False(replacement.Disposed); Assert.False(main.Disposed);
    }

    [Fact]
    public async Task OutputCanPreventDispatchOfModelRequestedTools()
    {
        var calls = 0; var registry = new MiddlewareRegistry();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { StopReason = StopReasons.EndTurn }));
        var tool = new FunctionTool(Spec("side_effect"), (_, _, _) => { calls++; return new("done"); });
        await using var agent = new Agent(new() { Model = new ScriptModel(Call("side_effect")), Tools = [tool], Middleware = registry });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("question")).StopReason);
        Assert.Equal(0, calls); Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task ToolTransformsAndShortCircuitRetainOriginalCorrelationAndOneHookAndResultPair()
    {
        var calls = 0; var afterCalls = 0; var resultEvents = 0;
        var registry = new MiddlewareRegistry();
        registry.RegisterInput(MiddlewareStages.ExecuteTool, (context, _) => new(context with { ToolUse = context.ToolUse with { ToolUseId = "rewritten", Input = JsonSerializer.SerializeToElement(new { value = 2 }) } }));
        registry.RegisterOutput(MiddlewareStages.ExecuteTool, (result, _) => new(result with { Result = new("forged", "authoritative") }));
        var hooks = new HookRegistry(); hooks.Register<AfterToolCallEvent>((value, _) => { afterCalls++; Assert.Equal("original", value.Result.ToolUseId); Assert.Equal("authoritative", value.Result.Content); return default; });
        hooks.Register<ToolResultEvent>((value, _) => { resultEvents++; Assert.Equal("original", value.Result.ToolUseId); return default; });
        var tool = new FunctionTool(Spec("echo"), (input, context, _) => { calls++; Assert.Equal(2, input.GetProperty("value").GetInt32()); Assert.Equal("original", context.ToolUse.ToolUseId); return new("raw"); });
        await using var agent = new Agent(new() { Model = new ScriptModel(Call("echo"), Text("done")), Tools = [tool], Middleware = registry, Hooks = hooks });
        Assert.Equal("done", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(1, calls); Assert.Equal(1, afterCalls); Assert.Equal(1, resultEvents);
        Assert.Equal("authoritative", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task ToolCacheSkipsSchemaAndBodyButKeepsHookBoundaries()
    {
        var calls = 0; var before = 0; var after = 0; var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.ExecuteTool, Cached);
        var hooks = new HookRegistry(); hooks.Register<BeforeToolCallEvent>((_, _) => { before++; return default; }); hooks.Register<AfterToolCallEvent>((_, _) => { after++; return default; });
        var tool = new FunctionTool(Spec("echo"), (_, _, _) => { calls++; return new("raw"); });
        await using var agent = new Agent(new() { Model = new ScriptModel(Call("echo"), Text("done")), Tools = [tool], Middleware = registry, Hooks = hooks });
        await agent.InvokeAsync("question");
        Assert.Equal(0, calls); Assert.Equal(1, before); Assert.Equal(1, after);
        Assert.Equal("cached", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Cached(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(context.ToolUse.ToolUseId, "cached"))); }
    }

    [Fact]
    public async Task ProviderStateIsHiddenFrozenAndCommittedOnlyAfterSuccessfulMiddleware()
    {
        var model = new StatefulScript(); var registry = new MiddlewareRegistry(); var failure = new IOException("reject");
        var output = registry.RegisterOutput(MiddlewareStages.InvokeModel, (_, _) => throw failure);
        registry.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { context.Agent.ModelState.Set("id", "forged"); return new(context); });
        await using var agent = new Agent(new() { Model = model, Middleware = registry, ModelState = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement("initial") } });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("first")));
        Assert.Equal("initial", agent.ModelState.Get<string>("id"));
        Assert.Equal("initial", Assert.Single(model.Requests).ModelState!["id"].GetString());
        output.Dispose();
        await agent.InvokeAsync(null);
        Assert.Equal("accepted", agent.ModelState.Get<string>("id"));
    }

    [Fact]
    public async Task PluginMiddlewareIsOwnedLocallyWhileApplicationRegistrationsReachFreshChildren()
    {
        var application = new MiddlewareRegistry(); var appCalls = 0; var plugin = new MiddlewarePlugin();
        application.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { appCalls++; return new(context); });
        var model = new ScriptModel(Text("parent"), Text("child"));
        var parent = new Agent(new() { Model = model, Middleware = application, Plugins = [plugin] });
        await parent.InvokeAsync("question");
        await using var child = parent.CreateChild(); await child.InvokeAsync("question");
        Assert.Equal(2, appCalls); Assert.Equal(2, plugin.Calls); Assert.Equal(2, plugin.Initializations);
        await parent.DisposeAsync();
        await MiddlewareRegistryTests.Collect(parent.Middleware.InvokeAsync(MiddlewareStages.InvokeModel, MiddlewareRegistryTests.Context(), MiddlewareRegistryTests.Done));
        Assert.Equal(3, appCalls); Assert.Equal(2, plugin.Calls);
    }

    [Fact]
    public async Task FilteringProviderEventsDoesNotChangeTheAuthoritativeAssembledResponse()
    {
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Filter);
        await using var agent = new Agent(new() { Model = new ScriptModel(Text("still assembled")), Middleware = registry });
        var events = new List<IAgentStreamEvent>(); await foreach (var item in agent.StreamAsync("question")) events.Add(item);
        Assert.DoesNotContain(events, item => item is ModelStreamUpdateEvent);
        Assert.Equal("still assembled", Assert.Single(events.OfType<AgentResultEvent>()).Result.ToString());
        Assert.Equal("still assembled", agent.Messages.Last().Text);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Filter(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var item in next(context)) if (item is MiddlewareResultEvent<InvokeModelResult>) yield return item; }
    }

    [Fact]
    public async Task ToolMiddlewareCanFilterProgressAndInjectAnOrdinaryStreamEventWithoutDuplicatingLifecycle()
    {
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.ExecuteTool, Filter);
        var progressHooks = 0; var afterHooks = 0; var results = 0; var hooks = new HookRegistry();
        hooks.Register<ToolStreamUpdateEvent>((_, _) => { progressHooks++; return default; });
        hooks.Register<AfterToolCallEvent>((_, _) => { afterHooks++; return default; });
        hooks.Register<ToolResultEvent>((_, _) => { results++; return default; });
        var tool = new StreamingFunctionTool(Spec("stream"), (_, context, token) => Updates(context.ToolUse.ToolUseId, token));
        await using var agent = new Agent(new() { Model = new ScriptModel(Call("stream"), Text("done")), Middleware = registry, Hooks = hooks, Tools = [tool] });
        var events = new List<IAgentStreamEvent>(); await foreach (var item in agent.StreamAsync("question")) events.Add(item);
        Assert.Single(events.OfType<CustomUpdate>()); Assert.DoesNotContain(events, item => item is ToolStreamUpdateEvent);
        Assert.Equal(0, progressHooks); Assert.Equal(1, afterHooks); Assert.Equal(1, results);
        Assert.Equal("tool result", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);

        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Filter(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MiddlewareStreamEvent<ExecuteToolResult>(new CustomUpdate("injected"));
            await foreach (var item in next(context)) if (item is MiddlewareResultEvent<ExecuteToolResult>) yield return item;
        }
        async IAsyncEnumerable<IToolStreamEvent> Updates(string id, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new ToolStreamEvent("hidden"); yield return new ToolStreamResultEvent(new(id, "tool result")); }
    }

    [Fact]
    public async Task ExplicitToolRetryReexecutesOnlyByCallerChoiceAndKeepsOneAfterHook()
    {
        var calls = 0; var afterHooks = 0; var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.ExecuteTool, Retry);
        var hooks = new HookRegistry(); hooks.Register<AfterToolCallEvent>((_, _) => { afterHooks++; return default; });
        var tool = new FunctionTool(Spec("retry"), (_, _, _) => ++calls == 1 ? throw new IOException("first attempt") : new("second attempt"));
        await using var agent = new Agent(new() { Model = new ScriptModel(Call("retry"), Text("done")), Middleware = registry, Hooks = hooks, Tools = [tool] });
        await agent.InvokeAsync("question");
        Assert.Equal(2, calls); Assert.Equal(1, afterHooks);
        Assert.Equal("second attempt", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Retry(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next(context)) if (item is MiddlewareResultEvent<ExecuteToolResult> result) Assert.IsType<IOException>(result.Result.Result.Error);
            await foreach (var item in next(context)) yield return item;
        }
    }

    [Fact]
    public async Task MiddlewareFailureBeforeCommitKeepsItsIdentityWhenInvocationCleanupAlsoFails()
    {
        var primary = new IOException("middleware failed"); var secondary = new InvalidOperationException("invocation cleanup");
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Failure);
        var hooks = new HookRegistry(); hooks.Register<AfterInvocationEvent>((_, _) => throw secondary);
        await using var agent = new Agent(new() { Model = new ScriptModel(Text("uncommitted")), Middleware = registry, Hooks = hooks });
        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));
        Assert.Same(primary, actual); Assert.Contains(secondary, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));
        Assert.Single(agent.Messages); Assert.Equal(Role.User, agent.Messages[0].Role);
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Failure(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var item in next(context)) yield return item; throw primary; }
    }

    [Fact]
    public async Task StatefulSyntheticResultsRemainPendingUntilARealProviderAcknowledgesTheirInput()
    {
        var registry = new MiddlewareRegistry(); var cache = registry.RegisterWrap(MiddlewareStages.InvokeModel, Cached); var model = new StatefulScript();
        await using var agent = new Agent(new() { Model = model, Middleware = registry });
        Assert.Equal("cached", (await agent.InvokeAsync("first")).ToString());
        Assert.Empty(model.Requests); Assert.Equal(2, agent.Messages.Count); Assert.Empty(agent.ModelState.GetAll());
        cache.Dispose(); await agent.InvokeAsync("second");
        Assert.Equal(new[] { "first", "cached", "second" }, Assert.Single(model.Requests).Messages.Select(message => message.Text));
        Assert.Empty(agent.Messages); Assert.Equal("accepted", agent.ModelState.Get<string>("id"));
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }
    }

    [Fact]
    public async Task AfterModelRetryDoesNotReplayAProviderWhoseCleanupFailed()
    {
        var failure = new IOException("provider disposal"); var model = new DisposalFailureModel(failure); var hooks = new HookRegistry(); var attempts = 0;
        hooks.Register<AfterModelCallEvent>((value, _) => { value.Retry = ++attempts == 1; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        Assert.Equal(1, model.Calls); Assert.Single(agent.Messages);
    }

    [Theory]
    [InlineData(123)]
    [InlineData(-1)]
    public async Task InputReceivesAnAvailableProjectionWhileFailedEstimatesRemainOptional(long estimate)
    {
        var model = new CountingModel(estimate); var middleware = new MiddlewareRegistry(); var observed = false;
        middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { observed = true; Assert.Equal(estimate >= 0 ? estimate : (long?)null, context.ProjectedInputTokens); return new(context); });
        await using var agent = new Agent(new() { Model = model, Middleware = middleware });
        Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString());
        Assert.True(observed); Assert.Equal(1, model.Counts);
    }

    private sealed record CustomUpdate(string Value) : IAgentStreamEvent;
    private sealed class CountingModel(long estimate) : IModel, IModelTokenCounter
    {
        public int Counts { get; private set; }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) { Counts++; return new(estimate); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; foreach (var item in Text("answer")) yield return item; }
    }
    private sealed class DisposalFailureModel(IOException failure) : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return new Sequence(failure); }
        private sealed class Sequence(IOException failure) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly ModelStreamEvent[] _events = Text("uncommitted"); private int _index = -1;
            public ModelStreamEvent Current => _events[_index];
            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() => new(++_index < _events.Length);
            public ValueTask DisposeAsync() => throw failure;
        }
    }

    private sealed class MiddlewarePlugin : IPlugin
    {
        public int Calls, Initializations;
        public string Name => "test:middleware";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { Initializations++; agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { Calls++; return new(context); }); return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class StatefulScript : IModel, IStatefulModel
    {
        public bool Stateful => true;
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; Requests.Add(request); foreach (var item in Text("reply")) yield return item is ModelMessageStopEvent stop ? stop with { ModelState = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement("accepted") } } : item; }
    }
    private sealed class ScriptModel(params ModelStreamEvent[][] turns) : IModel, IDisposable
    {
        public List<ModelRequest> Requests { get; } = [];
        public Exception? FirstFailure { get; init; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request); if (Requests.Count == 1 && FirstFailure is { } failure) throw failure;
            foreach (var item in turns[Math.Min(Requests.Count - 1, turns.Length - 1)]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
    private static ToolSpec Spec(string name) => new(name, "Test tool", JsonSerializer.SerializeToElement(new { type = "object" }));
    private static ModelStreamEvent[] Text(string value) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(string name) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("original", name, JsonSerializer.SerializeToElement(new { value = 1 }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
}
