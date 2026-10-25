using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.EventParity;

public sealed class EventRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalPositionalEventEqualityAndHashCodesArePreserved(bool resultEvent)
    {
        if (resultEvent)
        {
            var value = new AgentResult(StopReasons.EndTurn, null, new(), 0, new Dictionary<string, object?>());
            var first = new AgentResultEvent(value); var second = new AgentResultEvent(value);
            Assert.Equal(first, second); Assert.Equal(first.GetHashCode(), second.GetHashCode()); Assert.Equal(second, first with { });
        }
        else
        {
            var value = new ModelMessageStartEvent(); var first = new ModelStreamUpdateEvent(value); var second = new ModelStreamUpdateEvent(value);
            Assert.Equal(first, second); Assert.Equal(first.GetHashCode(), second.GetHashCode()); Assert.Equal(second, first with { });
            if (first.InvocationState is IDictionary<string, object?> mutable) Assert.Throws<NotSupportedException>(() => mutable.Add("unbound", "must not leak"));
            Assert.Empty(new ModelStreamUpdateEvent(value).InvocationState);
        }
    }

    [Fact]
    public async Task AllEventInterfaceIncludesModelAndResultWhileTheRecordBaseKeepsItsExistingMatchSet()
    {
        var hooks = new HookRegistry(); var all = new List<IAgentStreamEvent>(); var records = new List<HookEvent>();
        hooks.Register<IAgentStreamEvent>((value, _) => { all.Add(value); return default; });
        hooks.Register<HookEvent>((value, _) => { records.Add(value); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        await agent.InvokeAsync("question");
        Assert.Equal(5, all.OfType<ModelStreamUpdateEvent>().Count()); Assert.Single(all.OfType<AgentResultEvent>());
        Assert.Equal(2, all.OfType<MessageAddedEvent>().Count()); Assert.Single(all.OfType<AfterInvocationEvent>());
        Assert.Equal(all.OfType<HookEvent>(), records); Assert.Single(records.OfType<ContentBlockEvent>()); Assert.Single(records.OfType<ModelMessageEvent>());
    }

    [Fact]
    public async Task UnboundOldConstructorAndForeignContextAreReboundOnceAfterMultipleMiddlewareLayers()
    {
        var state = new Dictionary<string, object?>(); var foreignState = new Dictionary<string, object?>();
        await using var foreign = new Agent(new() { Model = new EventTestModel() });
        var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Pass); registry.RegisterWrap(MiddlewareStages.InvokeModel, Synthetic);
        var hooks = new HookRegistry(); var seen = new List<ModelStreamUpdateEvent>();
        hooks.Register<ModelStreamUpdateEvent>((value, _) => { Assert.Same(state, value.InvocationState); seen.Add(value); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(), Middleware = registry, Hooks = hooks });
        await agent.InvokeAsync("question", new() { InvocationState = state });
        Assert.Equal(2, seen.Count); Assert.All(seen, value => Assert.Same(agent, value.Agent));
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Synthetic(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(new ModelMessageStartEvent()));
            yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(foreign, new ModelMetadataEvent(new()), foreignState));
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new()));
        }
    }

    [Fact]
    public async Task PriorityAndDisposalUseTheStableRegistrationSnapshotForEachModelUpdate()
    {
        var hooks = new HookRegistry(); var calls = new List<string>(); IDisposable? later = null;
        hooks.Register<ModelStreamUpdateEvent>((_, _) => { calls.Add("early"); later!.Dispose(); return default; }, order: -1);
        later = hooks.Register<ModelStreamUpdateEvent>((_, _) => { calls.Add("late"); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        await agent.InvokeAsync("question");
        Assert.Equal(new[] { "early", "late", "early", "early", "early", "early" }, calls);
    }

    [Fact]
    public void OriginalPositionalConstructorsAndDeconstructionRemainUsable()
    {
        var update = new ModelStreamUpdateEvent(new ModelMessageStartEvent()); update.Deconstruct(out var raw);
        Assert.Same(raw, (update with { }).Event); Assert.Null(update.Agent); Assert.Empty(update.InvocationState);
        var state = new Dictionary<string, object?>(); var completed = new AgentResultEvent(new(StopReasons.EndTurn, null, new(), 0, state)); completed.Deconstruct(out var result);
        Assert.Same(result, (completed with { }).Result); Assert.Null(completed.Agent); Assert.Same(state, completed.InvocationState);
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Pass(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await foreach (var item in next(context)) yield return item; }
}
