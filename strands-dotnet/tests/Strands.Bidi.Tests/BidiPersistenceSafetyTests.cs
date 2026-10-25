using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiPersistenceTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiPersistenceSafetyTests
{
    [Fact]
    public async Task PublicContentCannotOvertakeItsAwaitedMessageObserver()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(new BidiTextStartEvent("text"), new BidiTextDeltaEvent("one", "text"), new BidiTextStopEvent("text")); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiMessageAddedEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; });
        await agent.StartAsync(); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        var reading = ReadAsync(); await Within(entered.Task);
        try { Assert.NotSame(reading, await Task.WhenAny(reading, Task.Delay(100))); }
        finally { release.TrySetResult(true); }
        Assert.True(await Within(reading)); Assert.IsType<BidiTextStartEvent>(reader.Current); await agent.StopAsync();
        async Task<bool> ReadAsync() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task RestoredHistoricalIdentifiersCannotExecuteTheSameToolAgain()
    {
        var call = new ToolUseBlock("historical", "work", JsonSerializer.SerializeToElement(new { })); var uses = new Message(Role.Assistant, [call]); var results = new Message(Role.User, [new ToolResultBlock("historical", "already done")]);
        var store = new MemoryStore { Snapshot = new(1, [uses, results, uses, results]) }; var bodies = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("duplicate"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [work] }); await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([call])); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        await agent.StartAsync();
        await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var value in agent.ReceiveAsync()) if (value is ToolResultEvent) return;
        });
        Assert.Equal(0, bodies); Assert.Equal(4, agent.Messages.Count); await agent.StopAsync();
    }

    [Fact]
    public async Task CrashTailBecomesAnExplicitUnknownResultWithoutResumingItsBody()
    {
        var call = new ToolUseBlock("unfinished", "work", JsonSerializer.SerializeToElement(new { }));
        var store = new MemoryStore { Snapshot = new(1, [new Message(Role.Assistant, [call])]) };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        await agent.StartAsync(); var result = Assert.Single(model.Session!.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("unfinished", result.ToolUseId); Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("unknown", result.Content, StringComparison.OrdinalIgnoreCase); await agent.StopAsync();
    }

    [Fact]
    public async Task StopPreservesTheModelFailureAndStillAttemptsHookAndSessionCleanup()
    {
        var failure = new IOException("model stop"); var saveFailure = new IOException("session save"); var store = new MemoryStore { SaveError = saveFailure }; var stopped = false;
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel { StopError = failure }; await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SessionSaveStrategy = BidiSessionSaveStrategy.Stop });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { stopped = true; return default; });
        await agent.StartAsync(); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.StopAsync()));
        Assert.True(stopped); Assert.Contains(saveFailure, Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])); Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task ARealFileStorePreservesRepeatedHistoricalIdsAndCommitsANewBatchAsOnePair()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-bidi-history-" + Guid.NewGuid().ToString("N")); var store = new FileSessionStore(directory); const string sessionId = "repeated-history";
        var prior = new ToolUseBlock("historical", "work", JsonSerializer.SerializeToElement(new { }));
        await store.SaveAsync(sessionId, new(1, [new Message(Role.Assistant, [prior]), new Message(Role.User, [new ToolResultBlock("historical", "first")]), new Message(Role.Assistant, [prior]), new Message(Role.User, [new ToolResultBlock("historical", "second")])]));
        var bodies = 0; var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("new result"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] }); await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([prior with { ToolUseId = "new" }])); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = new FileSessionStore(directory), SessionId = sessionId });
        agent.Hooks.Register<BidiMessageAddedEvent>((_, _) => { Assert.Equal(6, agent.Messages.Count); Assert.Equal("new", Assert.Single(agent.Messages[4].Content.OfType<ToolUseBlock>()).ToolUseId); Assert.Equal("new", Assert.Single(agent.Messages[5].Content.OfType<ToolResultBlock>()).ToolUseId); return default; });
        await agent.StartAsync(); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        while (await reader.MoveNextAsync()) if (reader.Current is ToolResultEvent) break;
        await Within(model.Results.Task); await agent.StopAsync(); var restored = await new FileSessionStore(directory).LoadAsync(sessionId);
        Assert.Equal(1, bodies); Assert.Equal(6, restored!.Messages.Count); Assert.Equal(new[] { "historical", "historical", "new" }, restored.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Select(value => value.ToolUseId)); Assert.Equal("new result", Assert.Single(restored.Messages.Last().Content.OfType<ToolResultBlock>()).Content);
    }
}
