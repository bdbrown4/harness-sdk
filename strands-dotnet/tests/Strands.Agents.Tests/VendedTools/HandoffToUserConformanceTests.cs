using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserConformanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("bad.name")]
    public void InvalidNameIsRejectedByTheOrdinaryToolSpec(string? name)
        => Assert.ThrowsAny<ArgumentException>(() => HandoffToUser.Create(new() { Name = name! }));

    [Fact]
    public void NameBeyondTheOrdinaryLimitIsRejected()
        => Assert.ThrowsAny<ArgumentException>(() => HandoffToUser.Create(new() { Name = new string('a', 65) }));

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void BlankCustomDescriptionIsRejected(string description)
        => Assert.ThrowsAny<ArgumentException>(() => HandoffToUser.Create(new() { Description = description }));

    [Fact]
    public void UnknownBehaviorIsRejectedBeforeAnyContextExists()
        => Assert.Throws<ArgumentOutOfRangeException>(() => HandoffToUser.Create(new() { Behavior = (HandoffToUserBehavior)99 }));

    [Fact]
    public async Task MissingContextIsAnArgumentError()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () => await HandoffToUser.Create().InvokeResultAsync(Input("Q"), null!));

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript, "\ufeff", true)]
    [InlineData(HandoffToUserBehavior.Python, "\ufeff", false)]
    [InlineData(HandoffToUserBehavior.TypeScript, "\u0085", false)]
    [InlineData(HandoffToUserBehavior.Python, "\u0085", true)]
    [InlineData(HandoffToUserBehavior.TypeScript, "\u001c", false)]
    [InlineData(HandoffToUserBehavior.Python, "\u001c", true)]
    [InlineData(HandoffToUserBehavior.TypeScript, "\u3000", true)]
    [InlineData(HandoffToUserBehavior.Python, "\u3000", true)]
    public async Task UnicodeBlankSetsMatchTheSelectedSourceProfile(HandoffToUserBehavior behavior, string question, bool blank)
    {
        var calls = 0;
        var context = Context((_, reason, _) => { calls++; Assert.Equal(question, reason!.Value.GetString()); return Json("\"answer\""); });
        var tool = HandoffToUser.Create(new() { Behavior = behavior });
        if (blank) await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeResultAsync(Input(question), context));
        else Assert.Equal("answer", (await tool.InvokeResultAsync(Input(question), context)).Content);
        Assert.Equal(blank ? 0 : 1, calls);
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript, "null", "null")]
    [InlineData(HandoffToUserBehavior.Python, "null", "null")]
    [InlineData(HandoffToUserBehavior.TypeScript, "true", "true")]
    [InlineData(HandoffToUserBehavior.Python, "true", "true")]
    [InlineData(HandoffToUserBehavior.TypeScript, "false", "false")]
    [InlineData(HandoffToUserBehavior.Python, "false", "false")]
    [InlineData(HandoffToUserBehavior.TypeScript, "1.25", "1.25")]
    [InlineData(HandoffToUserBehavior.Python, "1.25", "1.25")]
    [InlineData(HandoffToUserBehavior.TypeScript, "\"\"", "")]
    [InlineData(HandoffToUserBehavior.Python, "\"\"", "")]
    [InlineData(HandoffToUserBehavior.TypeScript, "\"h\u00e9llo\"", "h\u00e9llo")]
    [InlineData(HandoffToUserBehavior.Python, "\"h\u00e9llo\"", "h\u00e9llo")]
    public async Task PrimitiveReplyKeepsTheTextualJsonValue(HandoffToUserBehavior behavior, string raw, string expected)
    {
        var result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Input("Q"), Context((_, _, _) => Json(raw)));
        Assert.Equal("current", result.ToolUseId);
        Assert.Equal(expected, Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreCancellationUsesTheOriginalAttributableTokenAndDoesNotInterrupt(bool operationCanceled, bool contextCanceled)
    {
        using var operation = new CancellationTokenSource();
        using var execution = new CancellationTokenSource();
        if (operationCanceled) operation.Cancel();
        if (contextCanceled) execution.Cancel();
        var calls = 0;
        var context = Context((_, _, _) => { calls++; return Json("null"); }) with { ExecutionCancelSignal = execution.Token };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await HandoffToUser.Create().InvokeResultAsync(Input("Q"), context, operation.Token));
        Assert.Equal(operationCanceled ? operation.Token : execution.Token, error.CancellationToken);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptedSynchronousAnswerIsNotRelabeledByCancellationInsideTheInterrupt(bool cancelOperation)
    {
        using var operation = new CancellationTokenSource();
        using var execution = new CancellationTokenSource();
        var context = Context((_, _, _) => { if (cancelOperation) operation.Cancel(); else execution.Cancel(); return Json("\"accepted\""); }) with { ExecutionCancelSignal = execution.Token };
        var result = await HandoffToUser.Create().InvokeResultAsync(Input("Q"), context, operation.Token);
        Assert.Equal("accepted", result.Content);
        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task IndependentInterruptFailureKeepsTheExactException()
    {
        var sentinel = new InvalidOperationException("independent interrupt callback");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await HandoffToUser.Create().InvokeResultAsync(Input("Q"), Context((_, _, _) => throw sentinel)));
        Assert.Same(sentinel, error);
        Assert.Empty(error.Data);
    }

    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript)]
    [InlineData(HandoffToUserBehavior.Python)]
    public async Task JsonReplySurvivesTheCallersDocumentDisposal(HandoffToUserBehavior behavior)
    {
        ToolResultBlock result;
        using (var document = JsonDocument.Parse("{\"json\":{\"unicode\":\"h\u00e9llo\",\"array\":[1,null,true]}}"))
            result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Input("Q"), Context((_, _, _) => behavior == HandoffToUserBehavior.TypeScript
                ? document.RootElement : JsonSerializer.SerializeToElement(new { status = "success", content = new[] { document.RootElement } })));
        var value = Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json;
        Assert.Equal("h\u00e9llo", value.GetProperty("unicode").GetString());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("array")[1].ValueKind);
    }

    [Fact]
    public async Task RecognizedTextAndJsonArrayPreservesOrderAndDoesNotMutateTheReply()
    {
        var reply = Json("[{\"text\":\"first\"},{\"json\":false},{\"text\":\"last\"}]");
        var before = reply.GetRawText();
        var result = await HandoffToUser.Create().InvokeResultAsync(Input("Q"), Context((_, _, _) => reply));
        Assert.Collection(result.ContentBlocks, b => Assert.Equal("first", Assert.IsType<TextBlock>(b).Text), b => Assert.False(Assert.IsType<JsonBlock>(b).Json.GetBoolean()), b => Assert.Equal("last", Assert.IsType<TextBlock>(b).Text));
        Assert.Equal(before, reply.GetRawText());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"text\":\"first\"},5,{\"text\":\"last\"}]")]
    public async Task NonContentArrayRemainsAWholeWrappedJsonValue(string raw)
    {
        var reply = Json(raw);
        var result = await HandoffToUser.Create().InvokeResultAsync(Input("Q"), Context((_, _, _) => reply));
        Assert.True(JsonElement.DeepEquals(reply, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetProperty("$value")));
    }

    [Theory]
    [InlineData("{\"status\":\"unknown\",\"content\":[]}")]
    [InlineData("{\"status\":true,\"content\":[]}")]
    [InlineData("{\"status\":\"success\",\"content\":false}")]
    [InlineData("{\"status\":\"success\",\"content\":[{\"text\":\"first\"},{\"unknown\":2}]}")]
    public async Task MalformedClaimedPythonEnvelopeNeverReturnsAPartialResult(string raw)
        => await Assert.ThrowsAnyAsync<ArgumentException>(async () => await HandoffToUser.Create(new() { Behavior = HandoffToUserBehavior.Python }).InvokeResultAsync(Input("Q"), Context((_, _, _) => Json(raw))));

    [Fact]
    public async Task DurablePausedBatchResumesOnAFreshAgentWithoutRepeatingTheSibling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "handoff-session-" + Guid.NewGuid().ToString("N"));
        var effects = 0;
        var sibling = new FunctionTool(new("effect", "One effect.", Json("{\"type\":\"object\"}")), (_, _, _) => { effects++; return new("committed"); });
        var handoff = HandoffToUser.Create();
        string question;
        try
        {
            await using (var first = new Agent(new() { Model = new ScriptedModel(Call(("effect-use", "effect", "{}"), ("handoff-use", "handoff_to_user", "{\"message\":\"Answer?\"}"))), Tools = [sibling, handoff], ToolExecution = ToolExecutionStrategy.Sequential, SessionStore = new FileSessionStore(directory), SessionId = "session" }))
            {
                var paused = await first.InvokeAsync("go");
                Assert.Equal(StopReasons.Interrupt, paused.StopReason);
                question = Assert.Single(paused.Interrupts).Id;
                Assert.Equal(1, effects);
            }
            await using (var fresh = new Agent(new() { Model = new ScriptedModel(Text("complete")), Tools = [sibling, handoff], ToolExecution = ToolExecutionStrategy.Sequential, SessionStore = new FileSessionStore(directory), SessionId = "session" }))
            {
                var result = await fresh.InvokeAsync(null, new() { InterruptResponses = [new(question, Json("\"yes\""))] });
                Assert.Equal(StopReasons.EndTurn, result.StopReason);
                Assert.Null(fresh.InterruptState);
                Assert.Equal(1, effects);
                var outputs = fresh.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().ToArray();
                Assert.Equal(new[] { "effect-use", "handoff-use" }, outputs.Select(r => r.ToolUseId));
                Assert.Equal("yes", outputs[1].Content);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OneInertToolCanServeTwoAgentsWithoutSharingAnswers()
    {
        var tool = HandoffToUser.Create();
        await using var left = new Agent(new() { Model = new ScriptedModel(Call(("left-use", "handoff_to_user", "{\"message\":\"Left?\"}")), Text("left done")), Tools = [tool] });
        await using var right = new Agent(new() { Model = new ScriptedModel(Call(("right-use", "handoff_to_user", "{\"message\":\"Right?\"}")), Text("right done")), Tools = [tool] });
        var lp = await left.InvokeAsync("left");
        var rp = await right.InvokeAsync("right");
        Assert.Equal("Left?", Assert.Single(lp.Interrupts).Reason!.Value.GetString());
        Assert.Equal("Right?", Assert.Single(rp.Interrupts).Reason!.Value.GetString());
        await right.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(rp.Interrupts).Id, Json("\"R\""))] });
        Assert.NotNull(left.InterruptState);
        await left.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(lp.Interrupts).Id, Json("\"L\""))] });
        Assert.Equal("L", Assert.Single(left.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()).Content);
        Assert.Equal("R", Assert.Single(right.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task RegisteredInvalidInputProducesAnErrorWithoutPausing()
    {
        await using var agent = new Agent(new() { Model = new ScriptedModel(Call(("bad-use", "handoff_to_user", "{\"message\":false}")), Text("complete")), Tools = [HandoffToUser.Create()] });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(agent.InterruptState);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()).Status);
    }

    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static JsonElement Input(string text) => JsonSerializer.SerializeToElement(new { message = text });
    private static ToolContext Context(Func<string, JsonElement?, JsonElement?, JsonElement> interrupt)
        => new(null!, new("current", "handoff_to_user", Input("Q")), new Dictionary<string, object?>()) { InterruptHandler = interrupt };
    private static ModelStreamEvent[] Call(params (string Id, string Name, string Input)[] calls)
    {
        var values = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var i = 0; i < calls.Length; i++)
        {
            values.Add(new ModelContentBlockStartEvent(i, new(calls[i].Id, calls[i].Name, Json("{}"))));
            values.Add(new ModelContentBlockDeltaEvent(i, ToolInput: calls[i].Input));
            values.Add(new ModelContentBlockStopEvent(i));
        }
        values.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return values.ToArray();
    }
    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        private int _index;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in turns[_index++]) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
        }
    }
}
