using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class SubagentStreamingTests
{
    [Fact]
    public async Task DelegationForwardsTheActualChildStreamExactlyOnce()
    {
        Agent? built = null;
        var tool = Subagent.Create(new()
        {
            Name = "delegate_alias",
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => new(built = new Agent(new() { Model = BridgeSupport.Text("child-text"), Messages = spec.Messages })),
        });
        Assert.IsAssignableFrom<IStreamingTool>(tool);
        await using var parent = new Agent(new() { Model = BridgeSupport.ToolOrEcho("parent-call", "delegate_alias", BridgeSupport.TaskInput), Tools = [tool] });
        var events = new List<IAgentStreamEvent>();
        await foreach (var value in parent.StreamAsync("request")) events.Add(value);
        var forwarded = events.OfType<ToolStreamUpdateEvent>().Select(value => value.Event.Data).OfType<ModelStreamUpdateEvent>().Select(value => value.Event).OfType<ModelContentBlockDeltaEvent>().ToArray();
        Assert.Equal("child-text", Assert.Single(forwarded).Text);
        Assert.Equal("child-text", Assert.Single(events.OfType<AgentResultEvent>()).Result.ToString());
        Assert.NotNull(built);
        Assert.All(events.OfType<ToolStreamUpdateEvent>().Select(value => value.Event.Data).OfType<ModelStreamUpdateEvent>(), value => Assert.Same(built, value.Agent));

    }

    [Fact]
    public async Task AllContextUsesItsPreparedHistoryWithoutAnExtraInputPropertyOrTask()
    {
        ModelRequest? observed = null;
        AgentSpec? prepared = null;
        var tool = Subagent.Create(new()
        {
            Context = new Fixed("all"),
            Tools = new Fixed(Array.Empty<string>()),
            Builder = (spec, _, _) => { prepared = spec; return new(new Agent(new() { Model = BridgeSupport.Text("done", request => observed = request), Messages = spec.Messages })); },
        });
        await using var parent = new Agent(new() { Model = BridgeSupport.Text("unused"), Messages = [new(Role.User, [new TextBlock("prior conversation")])] });
        var input = JsonSerializer.SerializeToElement(new { task = "focused-task" });
        var result = await tool.InvokeResultAsync(input, new(parent, new("call", "subagent", input), new Dictionary<string, object?>()));
        Assert.Equal("done", result.Content); Assert.NotNull(prepared); Assert.Null(prepared.Prompt); Assert.NotNull(observed);
        var text = string.Join("\n", observed.Messages.Select(message => message.Text));
        Assert.Contains("prior conversation", text); Assert.Equal(1, text.Split(new[] { "focused-task" }, StringSplitOptions.None).Length - 1);
    }
}

internal static class BridgeSupport
{
    internal static readonly JsonElement EmptyInput = JsonSerializer.SerializeToElement(new { });
    internal static readonly JsonElement TaskInput = JsonSerializer.SerializeToElement(new { task = "work" });
    internal static IModel Text(string text, Action<ModelRequest>? observe = null) => new Events(request => { observe?.Invoke(request); return Answer(text); });
    internal static IModel ToolOrEcho(string id, string name, JsonElement input) => new Events(request =>
    {
        var result = request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().LastOrDefault();
        return result is null ? Call(id, name, input) : Answer(result.Content);
    });
    internal static ModelStreamEvent[] Answer(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    internal static ModelStreamEvent[] Call(string id, string tool, JsonElement input) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new(id, tool, input)), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
    internal static IReadOnlyList<ModelStreamEvent> BatchApprovals(ModelRequest request)
    {
        if (request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true) return Answer("finished");
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        var names = new[] { "save", "approve", "approve" };
        for (var index = 0; index < names.Length; index++)
        { events.Add(new ModelContentBlockStartEvent(index, new("child/" + index, names[index], EmptyInput))); events.Add(new ModelContentBlockStopEvent(index)); }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse)); return events;
    }
    internal sealed class Events(Func<ModelRequest, IReadOnlyList<ModelStreamEvent>> events) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var item in events(request)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; } await Task.CompletedTask; }
    }
    internal sealed class Resource(Action dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() { dispose(); return default; } }
}
