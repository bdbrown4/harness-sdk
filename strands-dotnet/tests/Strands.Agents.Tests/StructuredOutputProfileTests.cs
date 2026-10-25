using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputProfileTests
{
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TypeScriptDropsTheIgnoredResponseWithoutAddingForceInput()
    {
        var model = new ScriptModel(false, Text("ignored"), Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("first"), Tool("second")], StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        var result = await agent.InvokeAsync("question", new() { EphemeralTools = [Tool("ephemeral")] });

        Assert.Equal(7, Value(result));
        Assert.Equal(2, model.Requests.Count);
        var forced = model.Requests[1];
        Assert.Equal("question", Assert.Single(forced.Messages).Text);
        Assert.Equal(new[] { "first", "second", "ephemeral", OutputName }, forced.Tools!.Select(tool => tool.Name));
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), forced.ToolChoice);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "ignored" || message.Text == Format().ForcePrompt);
        Assert.Equal(new[] { "first", "second" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task PythonForcedRequestUsesOnlyTheStructuredSpec()
    {
        var model = new ScriptModel(false, Text("retained"), Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("first"), Tool("second")], StructuredOutput = Format(StructuredOutputBehavior.Python) with { ForcePrompt = "format explicitly" } });
        var result = await agent.InvokeAsync("question");

        Assert.Equal(7, Value(result));
        Assert.Equal(OutputName, Assert.Single(model.Requests[1].Tools!).Name);
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), model.Requests[1].ToolChoice);
        Assert.Equal(new[] { "question", "retained", "format explicitly" }, model.Requests[1].Messages.Select(message => message.Text));
        Assert.Equal(new[] { "first", "second" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task TypeScriptDirectRichResultUsesConvertedJson()
    {
        await using var host = new Agent(new() { Model = new ScriptModel(false, Text("unused")) });
        ToolResultBlock result;
        using (var converted = JsonDocument.Parse("{\"value\":99}"))
        {
            var tool = new StructuredOutputTool<JsonElement>(Format(StructuredOutputBehavior.TypeScript) with { Deserializer = _ => converted.RootElement });
            var rich = Assert.IsAssignableFrom<IRichTool>(tool);
            result = await rich.InvokeResultAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "rich"));
            Assert.True(tool.TryGetResult("rich", out var output));
            Assert.Equal(99, output.GetProperty("value").GetInt32());
            Assert.Equal(1, tool.ValidationAttempts);
        }
        Assert.Equal("rich", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(99, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PythonDirectRichResultUsesValidationText()
    {
        await using var host = new Agent(new() { Model = new ScriptModel(false, Text("unused")) });
        var tool = new StructuredOutputTool<JsonElement>(Format(StructuredOutputBehavior.Python));
        var rich = Assert.IsAssignableFrom<IRichTool>(tool);
        var result = await rich.InvokeResultAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "python"));

        Assert.Equal("python", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal($"Successfully validated {OutputName} structured output", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
        Assert.True(tool.TryGetResult("python", out var output));
        Assert.Equal(7, output.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task TypeScriptNullableTypedResultCanBeARealNull()
    {
        var model = new ScriptModel(false, Call("nullable", 7));
        await using var agent = new Agent(new() { Model = model });
        var format = new StructuredOutputOptions<string?>
        { Schema = Format().Schema, Behavior = StructuredOutputBehavior.TypeScript, Deserializer = _ => null };
        var result = await agent.InvokeStructuredAsync("question", format, new() { MaxTurns = 1 });

        Assert.Null(result.Output);
        var accepted = Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>());
        Assert.Equal("nullable", accepted.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, accepted.Status);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonBlock>(Assert.Single(accepted.ContentBlocks)).Json.ValueKind);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task TypeScriptSelectsFirstMatchingCallRatherThanLaterSuccess()
    {
        var firstBatch = new Turn(StopReasons.ToolUse,
        [new ToolUseBlock("first-invalid", OutputName, JsonSerializer.SerializeToElement(new { value = "bad" })),
         new ToolUseBlock("second-valid", OutputName, JsonSerializer.SerializeToElement(new { value = 7 }))]);
        var model = new ScriptModel(false, firstBatch, Call("third-valid", 9));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        var result = await agent.InvokeAsync("question");

        Assert.Equal(9, Value(result));
        Assert.Equal(2, model.Requests.Count);
        var prior = model.Requests[1].Messages.Last().Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "first-invalid", "second-valid" }, prior.Select(value => value.ToolUseId));
        Assert.Equal(new[] { ToolResultStatus.Error, ToolResultStatus.Success }, prior.Select(value => value.Status));
        Assert.Equal("third-valid", Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).ToolUseId);
    }

    [Fact]
    public async Task TypeScriptNonEndTurnResponseIsForcedAndThenRefused()
    {
        var model = new ScriptModel(false, Text("sequence response", StopReasons.StopSequence), Text("refused", "refusal"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeAsync("question"));

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), model.Requests[1].ToolChoice);
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Fact]
    public async Task TypeScriptStatefulDiscardCommitsRealConsumptionWithoutMessage()
    {
        var store = new RecordingStore();
        JsonElement beforeForced = default;
        var added = new List<Message>();
        var model = new ScriptModel(true, Text("discard this"), Call("answer", 7));
        model.BeforeCall = count => { if (count == 2) beforeForced = store.Saved.Last().Clone(); };
        var hooks = new HookRegistry();
        using var registration = hooks.Register<MessageAddedEvent>((value, _) => { added.Add(value.Message); return default; });
        await using var agent = new Agent(new() { Model = model, SessionId = "profile-state", SessionStore = store, StructuredOutput = Format(StructuredOutputBehavior.TypeScript), Hooks = hooks });
        var result = await agent.InvokeAsync("question");

        Assert.Equal(7, Value(result));
        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal(2, beforeForced.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, beforeForced.GetProperty("modelHistoryCount").GetInt32());
        var user = Assert.Single(beforeForced.GetProperty("messages").EnumerateArray());
        Assert.Equal(agent.Messages[0].TrackingId, user.GetProperty("trackingId").GetString());
        Assert.True(beforeForced.TryGetProperty("modelInputReceipt", out var receipt), "A discarded completion requires its separate persisted input receipt.");
        Assert.Equal(1, receipt.GetProperty("consumedMessageCount").GetInt32());
        Assert.Equal(agent.Messages[0].TrackingId, receipt.GetProperty("lastConsumedTrackingId").GetString());
        Assert.False(string.IsNullOrEmpty(receipt.GetProperty("discardedResponseTrackingId").GetString()));
        Assert.Equal("state-1", beforeForced.GetProperty("modelState").GetProperty("responseId").GetString());
        Assert.DoesNotContain(added, message => message.Text == "discard this");
    }

    [Fact]
    public async Task TypeScriptSyntheticCompletionDoesNotAcknowledgeInput()
    {
        var middleware = new MiddlewareRegistry();
        var attempts = 0;
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, CachedOnce);
        var model = new ScriptModel(true, Call("real", 7));
        await using var agent = new Agent(new() { Model = model, Middleware = middleware, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        var result = await agent.InvokeAsync("question");

        Assert.Equal(7, Value(result));
        var real = Assert.Single(model.Requests);
        Assert.Equal("question", Assert.Single(real.Messages).Text);
        Assert.Empty(real.ModelState!);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "synthetic");

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> CachedOnce(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken token)
        {
            if (++attempts == 1)
            {
                yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("synthetic"), StopReasons.EndTurn, new()));
                yield break;
            }
            await foreach (var item in next(context).WithCancellation(token)) yield return item;
        }
    }

    [Fact]
    public async Task TypeScriptLimitResultUsesLastAcceptedMessage()
    {
        var model = new ScriptModel(false, Text("not accepted"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        var result = await agent.InvokeAsync("question", new() { MaxTurns = 1 });

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(1, result.Turns);
        Assert.Equal("question", result.Message!.Text);
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task PythonLimitPreservesItsAlreadyAppendedForcePrompt()
    {
        var model = new ScriptModel(false, Text("retained"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.Python) with { ForcePrompt = "format next" } });
        var result = await agent.InvokeAsync("question", new() { MaxTurns = 1 });

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(new[] { "question", "retained", "format next" }, agent.Messages.Select(message => message.Text));
        Assert.Equal("format next", result.Message!.Text);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(StructuredOutputBehavior.TypeScript)]
    [InlineData(StructuredOutputBehavior.Python)]
    public async Task MaxTokensUsesSourceSpecificAcceptanceBoundary(StructuredOutputBehavior behavior)
    {
        var model = new ScriptModel(false, Text("partial", StopReasons.MaxTokens));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(behavior) });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeAsync("question"));

        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
        if (behavior == StructuredOutputBehavior.TypeScript) Assert.Equal("question", Assert.Single(agent.Messages).Text);
        else Assert.Equal(new[] { "question", "partial" }, agent.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task NativeDefaultKeepsTheExistingHybrid()
    {
        var model = new ScriptModel(false, Text("retained"), Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("extra")], StructuredOutput = Format() });
        var result = await agent.InvokeAsync("question");

        Assert.Equal(StructuredOutputBehavior.Native, Format().Behavior);
        Assert.Equal(7, Value(result));
        Assert.Equal(new[] { "question", "retained", Format().ForcePrompt }, model.Requests[1].Messages.Select(message => message.Text));
        Assert.Equal(new[] { "extra", OutputName }, model.Requests[1].Tools!.Select(tool => tool.Name));
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), model.Requests[1].ToolChoice);
        Assert.Equal("Successfully validated structured output.", Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task LegacyDirectStringContractIsPreserved()
    {
        await using var host = new Agent(new() { Model = new ScriptModel(false, Text("unused")) });
        foreach (var behavior in new[] { StructuredOutputBehavior.Native, StructuredOutputBehavior.TypeScript, StructuredOutputBehavior.Python })
        {
            var tool = new StructuredOutputTool<JsonElement>(Format(behavior));
            Assert.Equal("Successfully validated structured output.", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "direct")));
            Assert.True(tool.TryGetResult("direct", out var value));
            Assert.Equal(7, value.GetProperty("value").GetInt32());
            var primary = new FormatException("conversion sentinel");
            var invalid = new StructuredOutputTool<JsonElement>(Format(behavior) with { Deserializer = _ => throw primary });
            var failure = await Assert.ThrowsAsync<StructuredOutputException>(async () => await invalid.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "invalid")));
            Assert.Same(primary, failure.InnerException);
            Assert.False(invalid.HasResult);
        }
    }

    [Fact]
    public async Task RejectedBodyOrCallerCancellationNeverCompletesStructuredOutput()
    {
        var hooks = new HookRegistry();
        using var rejection = hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = value.Result with { Status = ToolResultStatus.Error }; return default; });
        await using (var agent = new Agent(new() { Model = new ScriptModel(false, Call("rejected", 7)), StructuredOutput = Format(), Hooks = hooks }))
        {
            var result = await agent.InvokeAsync("question", new() { MaxTurns = 1 });
            Assert.Equal(StopReasons.MaxTurns, result.StopReason);
            Assert.Null(result.StructuredOutput);
            Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).Status);
        }
        using var caller = new CancellationTokenSource();
        var cancelledHooks = new HookRegistry();
        using var cancellation = cancelledHooks.Register<AfterToolCallEvent>((_, _) => { caller.Cancel(); return default; });
        await using (var agent = new Agent(new() { Model = new ScriptModel(false, Call("cancelled", 7)), StructuredOutput = Format(), Hooks = cancelledHooks }))
        {
            var result = await agent.InvokeAsync("question", cancellationToken: caller.Token);
            Assert.Equal(StopReasons.Cancelled, result.StopReason);
            Assert.Null(result.StructuredOutput);
        }
    }

    private static StructuredOutputOptions<JsonElement> Format(StructuredOutputBehavior behavior = StructuredOutputBehavior.Native) => new()
    {
        Behavior = behavior,
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };
    private static int Value(AgentResult result) => result.StructuredOutput!.Value.GetProperty("value").GetInt32();
    private static ToolContext Context(Agent agent, string id) => new(agent, new(id, OutputName, JsonSerializer.SerializeToElement(new { value = 7 })), new Dictionary<string, object?>());
    private static ITool Tool(string name) => new FunctionTool(new(name, "Test capability", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("ok"));
    private static Turn Text(string text, string reason = StopReasons.EndTurn) => new(reason, [new TextBlock(text)]);
    private static Turn Call(string id, int value) => new(StopReasons.ToolUse, [new ToolUseBlock(id, OutputName, JsonSerializer.SerializeToElement(new { value }))]);
    private sealed record Turn(string Reason, ContentBlock[] Blocks);

    private sealed class RecordingStore : ISessionStore
    {
        internal List<JsonElement> Saved { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saved.Add(JsonSerializer.SerializeToElement(snapshot, Web)); return default; }
    }

    private sealed class ScriptModel(bool stateful, params Turn[] turns) : IStatefulModel
    {
        public bool Stateful => stateful;
        internal List<ModelRequest> Requests { get; } = [];
        internal Action<int>? BeforeCall { get; set; }
        internal int IteratorDisposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                Tools = request.Tools?.ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            };
            Requests.Add(captured);
            BeforeCall?.Invoke(Requests.Count);
            if (Requests.Count > turns.Length) throw new InvalidOperationException("The fake model has no scripted response for this call.");
            var turn = turns[Requests.Count - 1];
            try
            {
                yield return new ModelMessageStartEvent();
                for (var index = 0; index < turn.Blocks.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (turn.Blocks[index] is ToolUseBlock tool)
                    {
                        yield return new ModelContentBlockStartEvent(index, tool);
                        yield return new ModelContentBlockStopEvent(index);
                    }
                    else
                    {
                        yield return new ModelContentBlockStartEvent(index) { ContentBlock = turn.Blocks[index] };
                        yield return new ModelContentBlockStopEvent(index) { ContentBlock = turn.Blocks[index] };
                    }
                }
                yield return new ModelMessageStopEvent(turn.Reason)
                { ModelState = stateful ? new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("state-" + Requests.Count) } : null };
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
    }
}
