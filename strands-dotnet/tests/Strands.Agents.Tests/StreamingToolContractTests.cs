using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class StreamingToolContractTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("progress_only")]
    [InlineData("duplicate_result")]
    [InlineData("progress_after_result")]
    [InlineData("null_event")]
    [InlineData("unknown_event")]
    [InlineData("wrong_id")]
    [InlineData("invalid_status")]
    [InlineData("null_stream")]
    [InlineData("null_iterator")]
    public async Task MalformedStreamsAreRejectedAndAcquiredIteratorsAreDisposed(string kind)
    {
        var terminal = new ToolStreamResultEvent(new("call", "done"));
        IToolStreamEvent[] events = kind switch
        {
            "empty" => [],
            "progress_only" => [new ToolStreamEvent("working")],
            "duplicate_result" => [terminal, terminal],
            "progress_after_result" => [terminal, new ToolStreamEvent("late")],
            "null_event" => [null!],
            "unknown_event" => [new UnknownEvent()],
            "wrong_id" => [new ToolStreamResultEvent(new("other", "done"))],
            "invalid_status" => [new ToolStreamResultEvent(new("call", "done", (ToolResultStatus)99))],
            _ => []
        };
        var stream = new Sequence(events);
        var tool = Tool((_, _, _) => kind switch { "null_stream" => null!, "null_iterator" => new NullIterator(), _ => stream });
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeResultAsync(Json("{}"), Context(agent)).AsTask());

        Assert.Equal(kind is "null_stream" or "null_iterator" ? 0 : 1, stream.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalFailureAndCleanupIdentitySurviveDirectAndAgentExecution(bool throughAgent)
    {
        var primary = new ApplicationException("body failure");
        var cleanup = new IOException("dispose failure");
        var stream = new Sequence([], primary, cleanup);
        var tool = Tool((_, _, _) => stream);
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}")), AgentTests.Text("recovered")), Tools = [tool] });

        if (throughAgent)
        {
            Assert.Equal("recovered", (await agent.InvokeAsync("go")).ToString());
            var result = Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]);
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Same(primary, result.Error);
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.False(serialized.RootElement.TryGetProperty(nameof(ToolResultBlock.Error), out _));
        }
        else Assert.Same(primary, await Assert.ThrowsAsync<ApplicationException>(() => tool.InvokeResultAsync(Json("{}"), Context(agent)).AsTask()));

        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, stream.Disposals);
    }

    [Fact]
    public async Task RichTerminalAndProgressJsonSurviveProducerDocumentDisposalAndHookRewriting()
    {
        var before = 0;
        var after = 0;
        var state = new Dictionary<string, object?>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { before++; value.ToolUse = value.ToolUse with { ToolUseId = "forged", Input = Json("{\"value\":7}") }; return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { after++; value.Result = value.Result with { ToolUseId = "forged" }; return default; });
        var tool = Tool((input, context, _) =>
        {
            Assert.Equal(7, input.GetProperty("value").GetInt32());
            Assert.Same(state, context.InvocationState);
            using var document = JsonDocument.Parse("{\"value\":42}");
            var progress = new ToolStreamEvent(document);
            var result = new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, [new TextBlock("display"), new JsonBlock(document.RootElement), new ImageBlock("image/png", "aGVsbG8=")], structuredContent: document.RootElement));
            return new Sequence([progress, result]);
        });
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });
        ToolStreamUpdateEvent? update = null;
        ToolResultEvent? terminal = null;

        await foreach (var item in agent.StreamAsync("go", new() { InvocationState = state }))
        {
            if (item is ToolStreamUpdateEvent progress) update = progress;
            if (item is ToolResultEvent result) terminal = result;
        }

        Assert.Equal(1, before);
        Assert.Equal(1, after);
        Assert.NotNull(update);
        Assert.Equal("call", update.ToolUse.ToolUseId);
        Assert.Equal(42, Assert.IsType<JsonElement>(update.Event.Data).GetProperty("value").GetInt32());
        Assert.NotNull(terminal);
        Assert.Equal("call", terminal.Result.ToolUseId);
        Assert.Equal(42, terminal.Result.StructuredContent!.Value.GetProperty("value").GetInt32());
        var committed = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]);
        Assert.Equal("display", committed.Content);
        Assert.Equal(42, Assert.IsType<JsonBlock>(committed.ContentBlocks[1]).Json.GetProperty("value").GetInt32());
        Assert.IsType<ImageBlock>(committed.ContentBlocks[2]);
        Assert.Throws<ArgumentNullException>(() => new ToolStreamResultEvent(null!));
    }

    [Fact]
    public async Task ConcurrentProgressInterleavesButCommittedResultsRetainModelOrder()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = Tool((_, context, token) => Concurrent(context.ToolUse.ToolUseId, release.Task, token));
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("a", "stream", "{}"), ("b", "stream", "{}"), ("c", "stream", "{}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], MaxParallelTools = 2 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var terminals = new List<string>();
        try
        {
            await foreach (var item in agent.StreamAsync("go", cancellationToken: deadline.Token))
            {
                if (item is not ToolResultEvent terminal) continue;
                Assert.Single(model.Requests);
                terminals.Add(terminal.Result.ToolUseId);
                if (terminal.Result.ToolUseId == "c") release.TrySetResult(true);
            }
        }
        finally { release.TrySetResult(true); }

        Assert.Equal(["b", "c", "a"], terminals);
        Assert.Equal(["a", "b", "c"], model.Requests[1].Messages[2].Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
        Assert.All(model.Requests[1].Messages[2].Content.OfType<ToolResultBlock>(), result => Assert.Equal(ToolResultStatus.Success, result.Status));
    }

    [Fact]
    public async Task EarlyTerminalDisposalCommitsCompletedSiblingsAndReleasesTheInvocationGuard()
    {
        var streamDisposed = false;
        var tool = Tool((_, context, token) => context.ToolUse.ToolUseId == "a" ? Waiting(() => streamDisposed = true, token) : Immediate("b"));
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("a", "stream", "{}"), ("b", "stream", "{}")), AgentTests.Text("continued"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        await using (var iterator = agent.StreamAsync("go").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync())
                if (iterator.Current is ToolResultEvent { Result.ToolUseId: "b" }) break;

        Assert.True(streamDisposed);
        var results = agent.Messages[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["a", "b"], results.Select(result => result.ToolUseId));
        Assert.Equal(ToolResultStatus.Error, results[0].Status);
        Assert.Equal("b", results[1].Content);
        Assert.Equal(ToolResultStatus.Success, results[1].Status);
        Assert.Equal("continued", (await agent.InvokeAsync(null)).ToString());
    }

    [Fact]
    public async Task ProgressHookFailureCancelsAndDisposesTheProducerWithoutMaskingCleanupFailure()
    {
        var primary = new InvalidOperationException("progress hook");
        var cleanup = new IOException("after-tool hook");
        var hooks = new HookRegistry();
        hooks.Register<ToolStreamUpdateEvent>((_, _) => throw primary);
        hooks.Register<AfterToolCallEvent>((_, _) => throw cleanup);
        var stream = new Sequence([new ToolStreamEvent("working"), new ToolStreamResultEvent(new("call", "done"))]);
        var tool = Tool((_, _, _) => stream);
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}"))), Tools = [tool], Hooks = hooks });

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go")));

        Assert.Equal(1, stream.Disposals);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal("call", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).ToolUseId);
    }

    [Fact]
    public async Task ConcurrentAfterHookFailuresRetainTheInputOrderedPrimaryAndAllSiblingCleanupErrors()
    {
        var primary = new InvalidOperationException("first after-hook");
        var secondary = new IOException("second after-hook");
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) => throw (value.ToolUse.ToolUseId == "a" ? primary : secondary));
        var tool = Tool((_, context, _) => Immediate(context.ToolUse.ToolUseId));
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("a", "stream", "{}"), ("b", "stream", "{}"))), Tools = [tool], Hooks = hooks });

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go")));

        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(["a", "b"], agent.Messages[2].Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
        Assert.All(agent.Messages[2].Content.OfType<ToolResultBlock>(), result => Assert.Equal(ToolResultStatus.Success, result.Status));
    }

    private static StreamingFunctionTool Tool(Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback) => new(new("stream", "Stream progress", Json("{\"type\":\"object\"}")), callback);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static ToolContext Context(Agent agent) => new(agent, new("call", "stream", Json("{}")), new Dictionary<string, object?>());

    private static async IAsyncEnumerable<IToolStreamEvent> Concurrent(string id, Task release, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (id != "b") yield return new ToolStreamEvent(id);
        if (id == "a") await release.WaitAsync(cancellationToken);
        yield return new ToolStreamResultEvent(new(id, id));
    }

    private static async IAsyncEnumerable<IToolStreamEvent> Immediate(string id)
    {
        yield return new ToolStreamResultEvent(new(id, id));
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IToolStreamEvent> Waiting(Action onDispose, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try { yield return new ToolStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, cancellationToken); }
        finally { onDispose(); }
    }

    private sealed record UnknownEvent : IToolStreamEvent;
    private sealed class NullIterator : IAsyncEnumerable<IToolStreamEvent>
    {
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => null!;
    }
    private sealed class Sequence(IReadOnlyList<IToolStreamEvent> events, Exception? primary = null, Exception? cleanup = null) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private int _index = -1;
        public int Disposals { get; private set; }
        public IToolStreamEvent Current => events[_index];
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (++_index < events.Count) return new(true);
            if (primary is not null) throw primary;
            return new(false);
        }
        public ValueTask DisposeAsync() { Disposals++; if (cleanup is not null) throw cleanup; return default; }
    }
}
