using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiAgentContentTests
{
    [Fact]
    public async Task ProgressUsesTheEffectiveHookSelectedCallWithTheOriginalProviderIdentifier()
    {
        ToolUseBlock? observed = null;
        var replacement = new StreamingFunctionTool(new("replacement", "Stream replacement data", JsonSerializer.SerializeToElement(new { type = "object" })), Stream);
        var original = new FunctionTool(new("original", "Original tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => throw new InvalidOperationException("The hook must replace this tool."));
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [original, replacement] });
        host.Hooks.Register<BeforeToolCallEvent>((value, _) => { value.ToolUse = new("forged", "replacement", JsonSerializer.SerializeToElement(new { value = 9 })); return default; });
        host.Hooks.Register<ToolStreamUpdateEvent>((value, _) => { observed = value.ToolUse; return default; });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "original", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var item in agent.ReceiveAsync(safety.Token)) if (item is ToolResultEvent) break;
        Assert.NotNull(observed); Assert.Equal("call", observed.ToolUseId); Assert.Equal("replacement", observed.Name); Assert.Equal(9, observed.Input.GetProperty("value").GetInt32());
        await Within(model.Results.Task); await agent.StopAsync();
        static async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { yield return new ToolStreamEvent("working"); await Task.CompletedTask; yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "done")); }
    }

    [Fact]
    public async Task CompleteContentIsRecordedBeforeCompletedBlockEventsAndPreservesTheSpeaker()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel(new BidiTextStartEvent("text"), new BidiTextDeltaEvent("hello", "text"), new BidiTextStopEvent("text"),
            new BidiReasoningStartEvent("reason"), new BidiReasoningDeltaEvent("reasoned", "reason"), new BidiReasoningStopEvent("reason"),
            new BidiTranscriptStartEvent(Role.User, "speech"), new BidiTranscriptDeltaEvent("spoken", Role.User, "speech"), new BidiTranscriptStopEvent(Role.User, "speech"), new BidiResponseStopEvent("r"));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        var output = new List<IAgentStreamEvent>();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var item in agent.ReceiveAsync(safety.Token))
        {
            output.Add(item);
            if (item is BidiResponseStopEvent) break;
        }
        Assert.Equal(new[] { "hello", "", "spoken" }, agent.Messages.Select(message => message.Text));
        Assert.Equal("reasoned", Assert.IsType<ReasoningBlock>(Assert.Single(agent.Messages[1].Content)).Text);
        Assert.Equal(Role.User, agent.Messages[2].Role);
        Assert.IsType<BidiTextBlockEvent>(output[output.FindIndex(item => item is BidiTextStopEvent) + 1]);
        Assert.IsType<BidiReasoningBlockEvent>(output[output.FindIndex(item => item is BidiReasoningStopEvent) + 1]);
        Assert.IsType<BidiTranscriptBlockEvent>(output[output.FindIndex(item => item is BidiTranscriptStopEvent) + 1]);
        Assert.All(agent.Messages, message => Assert.Equal("complete", message.Metadata!.Value.GetProperty("custom").GetProperty("bidi").GetProperty("status").GetString()));
        await agent.StopAsync();
    }

    [Fact]
    public async Task StopFinalizesPartialTextAndUnavailableTranscriptsInPlace()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel(new BidiTextStartEvent("text"), new BidiTextDeltaEvent("partial", "text"), new BidiTranscriptStartEvent(Role.User, "speech"));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        for (var index = 0; index < 4; index++) Assert.True(await reader.MoveNextAsync());
        await agent.StopAsync();
        Assert.Equal(new[] { "partial", "[Transcript unavailable.]" }, agent.Messages.Select(message => message.Text));
        Assert.All(agent.Messages, message => Assert.Equal("incomplete", message.Metadata!.Value.GetProperty("custom").GetProperty("bidi").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task StreamingToolsUseTheSharedRunnerAndAwaitProgressHooksBeforeAfterHooks()
    {
        var order = new List<string>();
        var tool = new StreamingFunctionTool(new("stream", "Stream a result", JsonSerializer.SerializeToElement(new { type = "object" })), Stream);
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        host.Hooks.Register<BeforeToolCallEvent>((_, _) => { order.Add("before"); return default; });
        host.Hooks.Register<ToolStreamUpdateEvent>((_, _) => { order.Add("progress"); return default; });
        host.Hooks.Register<AfterToolCallEvent>((_, _) => { order.Add("after"); return default; });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "stream", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        var output = new List<IAgentStreamEvent>();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var item in agent.ReceiveAsync(safety.Token)) { output.Add(item); if (item is ToolResultEvent) break; }
        Assert.Equal(new[] { "before", "progress", "after" }, order);
        Assert.Equal("working", Assert.IsType<ToolStreamUpdateEvent>(output[output.FindIndex(item => item is ToolResultEvent) - 1]).Event.Data);
        Assert.Equal("complete", Assert.IsType<ToolResultBlock>(Assert.Single((await Within(model.Results.Task)).Content)).Content);
        await agent.StopAsync();
        static async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { yield return new ToolStreamEvent("working"); await Task.CompletedTask; yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "complete")); }
    }
}
