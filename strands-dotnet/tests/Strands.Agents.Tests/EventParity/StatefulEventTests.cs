using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.EventParity;

public sealed class StatefulEventTests
{
    [Fact]
    public async Task InvalidStatefulToolCorrelationIsRejectedBeforeCompletedMessageObservationOrAcknowledgement()
    {
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("real", "work", JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
        var model = new StatefulModel(events); var middleware = new MiddlewareRegistry(); var messages = 0; var hooks = new HookRegistry();
        middleware.RegisterOutput(MiddlewareStages.InvokeModel, (value, _) => new(value with { Message = new(Role.Assistant, [new ToolUseBlock("forged", "work", JsonSerializer.SerializeToElement(new { }))]) }));
        hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = middleware, Hooks = hooks });
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("question")); Assert.Equal(0, messages); Assert.Empty(agent.ModelState.GetAll()); Assert.Single(agent.Messages);
    }

    [Fact]
    public async Task CancellationAfterARealCompletedMessageRetainsItsAuthoritativeProviderReceipt()
    {
        var model = new StatefulModel(); var hooks = new HookRegistry(); var calls = 0;
        hooks.Register<ModelMessageEvent>((value, _) => { if (++calls == 1) value.Agent.Cancel(); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("question")).StopReason);
        Assert.Equal("response-1", agent.ModelState.Get<string>("responseId"));
        await agent.InvokeAsync("next");
        Assert.Equal("response-1", model.Requests[1].ModelState!["responseId"].GetString()); Assert.Equal("next", Assert.Single(model.Requests[1].Messages).Text);
    }

    [Fact]
    public async Task CachedCompletionObservationsCannotForgeProviderStateOrAcknowledgePendingHistory()
    {
        var model = new StatefulModel(); var registry = new MiddlewareRegistry(); registry.RegisterWrap(MiddlewareStages.InvokeModel, Cache);
        var hooks = new HookRegistry(); var messages = 0;
        hooks.Register<ModelMessageEvent>((value, _) => { messages++; value.Agent.ModelState.Set("responseId", "forged"); return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = registry, Hooks = hooks, ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("original") } });
        Assert.Equal("cached", (await agent.InvokeAsync("question")).ToString()); Assert.Empty(model.Requests); Assert.Equal(1, messages);
        Assert.Equal("original", agent.ModelState.Get<string>("responseId")); Assert.Equal(2, agent.Messages.Count);
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cache(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask; yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }

    private sealed class StatefulModel(ModelStreamEvent[]? events = null) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in events ?? EventTestModel.Text("answer"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item is ModelMessageStopEvent stop ? stop with { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("response-" + Requests.Count) } } : item;
                await Task.Yield();
            }
        }
    }
}
