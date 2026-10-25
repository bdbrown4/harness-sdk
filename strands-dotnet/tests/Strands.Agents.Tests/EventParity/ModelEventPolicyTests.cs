using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.EventParity;

public sealed class ModelEventPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedMessageObservesOutputMiddlewareBeforeAfterHookTransformsAndCommit(bool cached)
    {
        var model = new EventTestModel(EventTestModel.Text("provider")); var registry = new MiddlewareRegistry(); var hooks = new HookRegistry();
        var seen = new List<ModelMessageEvent>(); var blocks = 0; var state = new Dictionary<string, object?>();
        if (cached) registry.RegisterWrap(MiddlewareStages.InvokeModel, Cache);
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { Message = Message.Assistant("output middleware") }));
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; });
        hooks.Register<ModelMessageEvent>((value, _) => { seen.Add(value); Assert.Same(state, value.InvocationState); Assert.Single(value.Agent.Messages); return default; });
        hooks.Register<AfterModelCallEvent>((value, _) => { Assert.Single(seen); Assert.NotSame(seen[0].Message, value.Message); value.Message = Message.Assistant("after hook"); return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = registry, Hooks = hooks });
        Assert.Equal("after hook", (await agent.InvokeAsync("question", new() { InvocationState = state })).ToString());
        Assert.Equal("output middleware", Assert.Single(seen).Message.Text); Assert.Equal(cached ? 0 : 1, blocks); Assert.Equal(cached ? 0 : 1, model.Requests.Count);
    }

    [Fact]
    public async Task RetriesObserveEveryCompletedAttemptButOnlyCommitTheFinalMessage()
    {
        var model = new EventTestModel(EventTestModel.Text("rejected"), EventTestModel.Text("accepted")); var hooks = new HookRegistry(); var seen = new List<string>(); var after = 0;
        hooks.Register<ModelMessageEvent>((value, _) => { seen.Add(value.Message.Text); return default; });
        hooks.Register<AfterModelCallEvent>((value, _) => { value.Retry = ++after == 1; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var result = await agent.InvokeAsync("question");
        Assert.Equal(new[] { "rejected", "accepted" }, seen); Assert.Equal("accepted", result.ToString());
        Assert.Equal(2, agent.Messages.Count); Assert.Equal("accepted", agent.Messages[1].Text);
    }

    [Fact]
    public async Task DeniedBeforeModelCallsKeepTheirAfterHookWithoutInventingModelCompletion()
    {
        var model = new EventTestModel(); var hooks = new HookRegistry(); var updates = 0; var blocks = 0; var messages = 0; var after = 0;
        hooks.Register<BeforeModelCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        hooks.Register<ModelStreamUpdateEvent>((_, _) => { updates++; return default; });
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; });
        hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        hooks.Register<AfterModelCallEvent>((_, _) => { after++; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Equal("denied", (await agent.InvokeAsync("question")).ToString());
        Assert.Empty(model.Requests); Assert.Equal(0, updates); Assert.Equal(0, blocks); Assert.Equal(0, messages); Assert.Equal(1, after);
    }

    [Fact]
    public async Task WrappersCanFilterProviderObservationsWithoutLosingTheCompletedMiddlewareMessage()
    {
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Filter);
        var hooks = new HookRegistry(); var updates = 0; var blocks = 0; var messages = 0;
        hooks.Register<ModelStreamUpdateEvent>((_, _) => { updates++; return default; });
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; });
        hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Middleware = registry, Hooks = hooks });
        await agent.InvokeAsync("question"); Assert.Equal(0, updates); Assert.Equal(0, blocks); Assert.Equal(1, messages);
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cache(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask; yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }

    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Filter(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await foreach (var item in next(context)) if (item is MiddlewareResultEvent<InvokeModelResult>) yield return item; }
}
