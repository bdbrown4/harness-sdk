using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests;

public sealed class ModelRedactionTests
{
    [Fact]
    public async Task CompletedTurnRedactsHistoryAndSessionWhileRetainingIdentityUsageAndOutputMetadata()
    {
        var original = new Message(Role.User, [new TextBlock("blocked input")], "input-id");
        var metadata = JsonSerializer.SerializeToElement(new { requestId = "response-id" });
        var events = Text("blocked output", new(3, 2)).Select(item => item is ModelMessageStopEvent stop ? stop with { Metadata = metadata } : item)
            .Append(new ModelRedactionEvent("safe input", "safe output")).ToArray();
        var store = new RecordingStore();
        await using var agent = new Agent(new() { Model = new ScriptedModel(events), Messages = [original], SessionStore = store, SessionId = "redacted" });

        var result = await agent.InvokeAsync(null);

        Assert.Equal("safe output", result.ToString());
        Assert.Equal(new Usage(3, 2), result.Usage);
        Assert.Equal("safe input", agent.Messages[0].Text);
        Assert.Equal("input-id", agent.Messages[0].TrackingId);
        Assert.Equal("blocked input", original.Text);
        Assert.Equal("response-id", agent.Messages[1].Metadata!.Value.GetProperty("requestId").GetString());
        Assert.NotNull(store.Last);
        Assert.Equal(new[] { "safe input", "safe output" }, store.Last.Messages.Select(message => message.Text));
        var reloadedModel = new ScriptedModel(Text("next"));
        await using var reloaded = new Agent(new() { Model = reloadedModel, SessionStore = store, SessionId = "redacted" });
        await reloaded.InvokeAsync(null);
        Assert.Equal(new[] { "safe input", "safe output" }, reloadedModel.Requests[0].Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ToolResultRedactionRetainsOnlyCorrelatedStatusAndReplacementText()
    {
        var json = JsonSerializer.SerializeToElement(new { secret = "structured" });
        var call = new Message(Role.Assistant, [new ToolUseBlock("first", "read", json), new ToolUseBlock("second", "read", json)]);
        var source = new Message(Role.User, [new TextBlock("outside"), new ToolResultBlock("first", [new JsonBlock(json)], ToolResultStatus.Error, json) { Error = new IOException("private error") }, new ToolResultBlock("second", "secret")], "tool-results", json);
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("done").Append(new ModelRedactionEvent("removed")).ToArray()), Messages = [call, source] });

        await agent.InvokeAsync(null);

        var message = agent.Messages[1];
        Assert.Equal("tool-results", message.TrackingId);
        Assert.Null(message.Metadata);
        var results = message.Content.Cast<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "first", "second" }, results.Select(result => result.ToolUseId));
        Assert.Equal(new[] { ToolResultStatus.Error, ToolResultStatus.Success }, results.Select(result => result.Status));
        Assert.All(results, result =>
        {
            Assert.Equal("removed", result.Content);
            Assert.Equal("removed", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
            Assert.Null(result.StructuredContent);
            Assert.Null(result.Error);
        });
        Assert.Equal("secret", Assert.IsType<ToolResultBlock>(source.Content[2]).Content);
    }

    [Fact]
    public void PostStopRedactionIsAcceptedAndNormalContentStillCannotFollowStop()
    {
        var accumulator = new ModelStreamAccumulator();
        foreach (var item in Text("before")) accumulator.Process(item);
        accumulator.Process(new ModelRedactionEvent(OutputReplacement: "after"));

        Assert.Equal("after", accumulator.Complete().Message.Text);
        Assert.Throws<ModelException>(() => accumulator.Process(new ModelContentBlockStartEvent(1)));
    }

    [Fact]
    public async Task IncompleteOrFailedProviderTurnCannotCommitInputRedaction()
    {
        var failure = new IOException("provider failed");
        await using var agent = new Agent(new() { Model = new FailingModel(failure) });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("original")));
        Assert.Equal("original", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task FailedSessionRedactionDoesNotPublishARewrittenHistoryOrAssistant()
    {
        var failure = new IOException("redaction save failed");
        var store = new RecordingStore { Failure = failure };
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("done").Append(new ModelRedactionEvent("replacement")).ToArray()), Messages = [Message.User("original")], SessionStore = store, SessionId = "existing" });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null)));
        Assert.Equal("original", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task MiddlewareProviderRetryDoesNotCarryRedactionFromTheDiscardedAttempt()
    {
        var model = new ScriptedModel(Text("discard").Append(new ModelRedactionEvent("discarded input")).ToArray(), Text("accepted"));
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Retry);
        await using var agent = new Agent(new() { Model = model, Middleware = middleware });

        Assert.Equal("accepted", (await agent.InvokeAsync("original")).ToString());
        Assert.Equal("original", agent.Messages[0].Text);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Retry(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var unused in next(context).WithCancellation(cancellationToken)) { }
            await foreach (var item in next(context).WithCancellation(cancellationToken)) yield return item;
        }
    }

    [Fact]
    public async Task MiddlewareCachedReplacementDoesNotCarryRedactionFromADiscardedProviderResult()
    {
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Replace);
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("discard").Append(new ModelRedactionEvent("discarded input")).ToArray()), Middleware = middleware });

        Assert.Equal("cached", (await agent.InvokeAsync("original")).ToString());
        Assert.Equal("original", agent.Messages[0].Text);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Replace(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var unused in next(context).WithCancellation(cancellationToken)) { }
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new()));
        }
    }

    [Fact]
    public async Task AfterModelRetryDoesNotCommitTheRejectedTurnsRedaction()
    {
        var hooks = new HookRegistry();
        var attempts = 0;
        hooks.Register<AfterModelCallEvent>((value, _) => { value.Retry = ++attempts == 1; return default; });
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("discard").Append(new ModelRedactionEvent("discarded input")).ToArray(), Text("accepted")), Hooks = hooks });

        Assert.Equal("accepted", (await agent.InvokeAsync("original")).ToString());
        Assert.Equal("original", agent.Messages[0].Text);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DisposingTheConsumerBeforeTurnAcceptanceLeavesHistoryUnchanged()
    {
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("partial").Append(new ModelRedactionEvent("uncommitted")).ToArray()) });
        var observed = false;

        await foreach (var item in agent.StreamAsync("original"))
            if (item is ModelStreamUpdateEvent { Event: ModelRedactionEvent }) { observed = true; break; }

        Assert.True(observed);
        Assert.Equal("original", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task EmptyReplacementDeliberatelyErasesContent()
    {
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("blocked").Append(new ModelRedactionEvent("", "")).ToArray()) });
        Assert.Equal("", (await agent.InvokeAsync("original")).ToString());
        Assert.Equal("", agent.Messages[0].Text);
    }

    [Theory]
    [InlineData("before-start")]
    [InlineData("empty-event")]
    [InlineData("repeat-input")]
    [InlineData("repeat-output")]
    public void MalformedRedactionIsRejected(string scenario)
    {
        var accumulator = new ModelStreamAccumulator();
        if (scenario != "before-start") accumulator.Process(new ModelMessageStartEvent());
        if (scenario == "repeat-input") accumulator.Process(new ModelRedactionEvent("first"));
        if (scenario == "repeat-output") accumulator.Process(new ModelRedactionEvent(OutputReplacement: "first"));

        Assert.Throws<ModelException>(() => accumulator.Process(scenario switch
        {
            "empty-event" => new ModelRedactionEvent(),
            "repeat-output" => new ModelRedactionEvent(OutputReplacement: "second"),
            _ => new ModelRedactionEvent("second")
        }));
    }

    [Fact]
    public void SeparateInputAndOutputEventsAreCombinedWithoutLosingUsage()
    {
        var accumulator = new ModelStreamAccumulator();
        foreach (var item in Text("before", new(3, 2))) accumulator.Process(item);
        accumulator.Process(new ModelRedactionEvent("input"));
        accumulator.Process(new ModelRedactionEvent(OutputReplacement: "output"));
        Assert.Equal("output", accumulator.Complete().Message.Text);
        Assert.Equal("input", accumulator.InputRedaction);
        Assert.Equal(new Usage(3, 2), accumulator.Usage);
    }

    [Fact]
    public void OutputRedactionCannotEraseToolCorrelation()
    {
        var accumulator = new ModelStreamAccumulator();
        foreach (var item in Call(("call", "tool", "{}"))) accumulator.Process(item);
        accumulator.Process(new ModelRedactionEvent(OutputReplacement: "removed"));
        Assert.Contains("correlation", Assert.Throws<ModelException>(() => accumulator.Complete()).Message);
    }

    [Fact]
    public async Task ProviderEventModeRedactsHistoryAndSessionBeforeALaterProviderFailure()
    {
        var failure = new IOException("provider failed");
        var store = new RecordingStore();
        await using var agent = new Agent(new() { Model = new FailingModel(failure), SessionStore = store, SessionId = "inline", InputRedactionTiming = InputRedactionTiming.ProviderEvent });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("original")));
        Assert.Equal("uncommitted", Assert.Single(agent.Messages).Text);
        Assert.Equal("uncommitted", Assert.Single(store.Last!.Messages).Text);
    }

    [Fact]
    public async Task ProviderEventModeRetainsRedactionWhenTheConsumerStopsBeforeTurnAcceptance()
    {
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("partial").Append(new ModelRedactionEvent("immediate")).ToArray()), InputRedactionTiming = InputRedactionTiming.ProviderEvent });
        await foreach (var item in agent.StreamAsync("original"))
            if (item is ModelStreamUpdateEvent { Event: ModelRedactionEvent }) break;
        Assert.Equal("immediate", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task ProviderEventModeDoesNotUndoRedactionWhenMiddlewareDiscardsThatAttempt()
    {
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Replace);
        await using var agent = new Agent(new() { Model = new ScriptedModel(Text("discard").Append(new ModelRedactionEvent("immediate")).ToArray()), Middleware = middleware, InputRedactionTiming = InputRedactionTiming.ProviderEvent });
        Assert.Equal("cached", (await agent.InvokeAsync("original")).ToString());
        Assert.Equal("immediate", agent.Messages[0].Text);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Replace(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var unused in next(context).WithCancellation(cancellationToken)) { }
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new()));
        }
    }

    [Fact]
    public void UnknownInputRedactionTimingIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new ScriptedModel(), InputRedactionTiming = (InputRedactionTiming)123 }));
    }

    private static ModelStreamEvent[] Text(string text, Usage? usage = null) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(usage ?? new())];

    private static ModelStreamEvent[] Call((string Id, string Name, string Json) call) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new(call.Id, call.Name, JsonSerializer.SerializeToElement(new { }))),
        new ModelContentBlockDeltaEvent(0, ToolInput: call.Json), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];

    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class RecordingStore : ISessionStore
    {
        internal SessionSnapshot? Last { get; private set; }
        internal IOException? Failure { get; init; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Last);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure) throw failure;
            Last = snapshot;
            return default;
        }
    }

    private sealed class FailingModel(IOException failure) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelRedactionEvent("uncommitted");
            throw failure;
        }
    }
}
