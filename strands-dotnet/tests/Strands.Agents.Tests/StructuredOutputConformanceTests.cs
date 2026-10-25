using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Middleware;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputConformanceTests
{
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);


    [Theory]
    [InlineData(StructuredOutputBehavior.TypeScript)]
    [InlineData(StructuredOutputBehavior.Python)]
    public async Task PerCallOverridesDefaultAndTheNextInvocationGetsAFreshDefault(StructuredOutputBehavior behavior)
    {
        var model = new ScriptModel(false, Text("first plain"), Call("first", 7), Text("next plain"), Call("next", 8));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("borrowed")], StructuredOutput = Format() });
        Assert.Equal(7, Value(await agent.InvokeAsync("first input", new() { StructuredOutput = Format(behavior) })));
        Assert.Equal(8, Value(await agent.InvokeAsync("next input")));
        var forced = model.Requests[1];
        if (behavior == StructuredOutputBehavior.TypeScript)
        {
            Assert.Equal("first input", Assert.Single(forced.Messages).Text);
            Assert.Equal(new[] { "borrowed", OutputName }, forced.Tools!.Select(tool => tool.Name));
        }
        else
        {
            Assert.Equal(new[] { "first input", "first plain", Format().ForcePrompt }, forced.Messages.Select(message => message.Text));
            Assert.Equal(OutputName, Assert.Single(forced.Tools!).Name);
        }
        Assert.Equal(new[] { "borrowed", OutputName }, model.Requests[3].Tools!.Select(tool => tool.Name));
        Assert.Equal(Format().ForcePrompt, model.Requests[3].Messages.Last().Text);
        Assert.Equal("borrowed", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(4, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(StructuredOutputBehavior.TypeScript)]
    [InlineData(StructuredOutputBehavior.Python)]
    public async Task StreamReturnsTheProfileValueAndCorrelatedAcceptedHistory(StructuredOutputBehavior behavior)
    {
        var model = new ScriptModel(false, Text("plain"), Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(behavior) });
        var events = new List<IAgentStreamEvent>();
        await foreach (var item in agent.StreamAsync("question")) events.Add(item);
        Assert.Equal(7, Value(Assert.Single(events.OfType<AgentResultEvent>()).Result));
        var accepted = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages.Last().Content));
        Assert.Equal("answer", accepted.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, accepted.Status);
        if (behavior == StructuredOutputBehavior.TypeScript)
        {
            Assert.DoesNotContain(agent.Messages, message => message.Text == "plain");
            Assert.Equal(7, Assert.IsType<JsonBlock>(Assert.Single(accepted.ContentBlocks)).Json.GetProperty("value").GetInt32());
        }
        else
        {
            Assert.Contains(agent.Messages, message => message.Text == "plain");
            Assert.Equal($"Successfully validated {OutputName} structured output", Assert.IsType<TextBlock>(Assert.Single(accepted.ContentBlocks)).Text);
        }
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedAndAgentNodeEntryPointsCarryTheirExplicitProfile(bool graphEntry)
    {
        var behavior = graphEntry ? StructuredOutputBehavior.Python : StructuredOutputBehavior.TypeScript;
        var model = new ScriptModel(false, Text("plain"), Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("extra")], StructuredOutput = Format() });
        if (graphEntry)
        {
            var graph = new Graph(new() { Nodes = [new AgentNode(agent, options: new() { StructuredOutput = Format(behavior) })] });
            var result = await graph.InvokeAsync("question");
            Assert.Equal(Status.Completed, result.Status);
            Assert.Equal(7, Assert.Single(result.Results).StructuredOutput!.Value.GetProperty("value").GetInt32());
        }
        else
        {
            var result = await agent.InvokeStructuredAsync("question", Format(behavior));
            Assert.Equal(7, result.Output.GetProperty("value").GetInt32());
        }
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), model.Requests[1].ToolChoice);
        Assert.Equal(graphEntry ? new[] { OutputName } : new[] { "extra", OutputName }, model.Requests[1].Tools!.Select(tool => tool.Name));
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(StopReasons.StopSequence)]
    [InlineData("refusal")]
    public async Task PythonOtherNonToolStopsKeepTheResponseWithoutForcing(string reason)
    {
        var model = new ScriptModel(false, Text("retained", reason));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.Python) });
        var result = await agent.InvokeAsync("question");
        Assert.Equal(reason, result.StopReason);
        Assert.Null(result.StructuredOutput);
        Assert.Equal(new[] { "question", "retained" }, agent.Messages.Select(message => message.Text));
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Fact]
    public async Task PythonExtractsTheFirstSuccessfulMatchingCall()
    {
        var batch = new Turn(StopReasons.ToolUse,
        [new ToolUseBlock("invalid", OutputName, JsonSerializer.SerializeToElement(new { value = "bad" })),
         new ToolUseBlock("valid", OutputName, JsonSerializer.SerializeToElement(new { value = 8 }))]);
        var model = new ScriptModel(false, batch);
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format(StructuredOutputBehavior.Python) });
        Assert.Equal(8, Value(await agent.InvokeAsync("question")));
        var results = agent.Messages.Last().Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "invalid", "valid" }, results.Select(result => result.ToolUseId));
        Assert.Equal(new[] { ToolResultStatus.Error, ToolResultStatus.Success }, results.Select(result => result.Status));
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeScriptExtractionUsesTheFinalResultFirstContent(bool leadingText)
    {
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            if (value.ToolUse.ToolUseId == "first")
            {
                ContentBlock[] blocks = leadingText
                    ? [new TextBlock("not JSON authority"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 99 }))]
                    : [new JsonBlock(JsonSerializer.SerializeToElement(new { value = 99 }))];
                value.Result = new(value.ToolUse.ToolUseId, blocks);
            }
            return default;
        });
        var model = new ScriptModel(false, Call("first", 7), Call("next", 9));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        var result = await agent.InvokeAsync("question");
        Assert.Equal(leadingText ? 9 : 99, Value(result));
        Assert.Equal(leadingText ? 2 : 1, model.Requests.Count);
        var first = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == "first");
        Assert.Equal(leadingText ? typeof(TextBlock) : typeof(JsonBlock), first.ContentBlocks[0].GetType());
        Assert.Equal(ToolResultStatus.Success, first.Status);
    }

    [Fact]
    public async Task PythonCachedValidationSurvivesFinalHookStatusReplacement()
    {
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            value.Result = new(value.ToolUse.ToolUseId, "hook replaced display", ToolResultStatus.Error);
            return default;
        });
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(StructuredOutputBehavior.Python) });
        Assert.Equal(7, Value(await agent.InvokeAsync("question", new() { MaxTurns = 1 })));
        var accepted = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages.Last().Content));
        Assert.Equal(ToolResultStatus.Error, accepted.Status);
        Assert.Equal("hook replaced display", accepted.Content);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(StructuredOutputBehavior.TypeScript)]
    [InlineData(StructuredOutputBehavior.Python)]
    public async Task ValidationFeedbackBudgetAndAfterToolRetryKeepOnlyCurrentCache(StructuredOutputBehavior behavior)
    {
        var invalid = new Turn(StopReasons.ToolUse, [new ToolUseBlock("invalid", OutputName, JsonSerializer.SerializeToElement(new { value = "bad" }))]);
        var model = new ScriptModel(false, invalid, Call("valid", 8));
        await using (var agent = new Agent(new() { Model = model, StructuredOutput = Format(behavior) with { MaxValidationAttempts = 2 } }))
        {
            Assert.Equal(8, Value(await agent.InvokeAsync("question")));
            Assert.Equal(ToolResultStatus.Error, Assert.Single(model.Requests[1].Messages.Last().Content.OfType<ToolResultBlock>()).Status);
            Assert.Equal(2, model.IteratorDisposals);
        }
        var bounded = new ScriptModel(false, invalid);
        await using (var agent = new Agent(new() { Model = bounded, StructuredOutput = Format(behavior) with { MaxValidationAttempts = 1 } }))
        {
            await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeAsync("question"));
            Assert.Single(bounded.Requests);
        }
        var hooks = new HookRegistry();
        var attempts = 0;
        using var retry = hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = ++attempts == 1; return default; });
        var conversions = 0;
        var repeated = new ScriptModel(false, Call("same-id", 7));
        await using (var agent = new Agent(new()
        {
            Model = repeated,
            Hooks = hooks,
            StructuredOutput = Format(behavior) with
            { Deserializer = _ => JsonSerializer.SerializeToElement(new { value = ++conversions }) }
        }))
        {
            Assert.Equal(2, Value(await agent.InvokeAsync("question")));
            Assert.Equal(2, conversions);
            Assert.Equal(2, attempts);
            Assert.Single(repeated.Requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializationFailureOrRequestedCancellationNeverPublishesCache(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var sentinel = new FormatException("serializer failure");
        var options = new JsonSerializerOptions(Web);
        options.Converters.Add(new FailingJsonConverter(() =>
        {
            if (cancelCaller) { caller.Cancel(); throw new OperationCanceledException(caller.Token); }
            throw sentinel;
        }));
        var tool = new StructuredOutputTool<JsonElement>(Format(StructuredOutputBehavior.TypeScript) with
        { SerializerOptions = options, Deserializer = value => value.Clone() });
        await using var host = new Agent(new() { Model = new ScriptModel(false) });
        if (cancelCaller)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeResultAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "call"), caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var result = await tool.InvokeResultAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "call"), caller.Token);
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Same(sentinel, Assert.IsType<StructuredOutputException>(result.Error).InnerException);
        }
        Assert.False(tool.HasResult);
        Assert.False(tool.TryGetResult("call", out _));
        Assert.Equal(1, tool.ValidationAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndTurnOrPhysicalCleanupTakesPrecedenceOverStructuredSuccess(bool cleanupFailure)
    {
        var sentinel = new IOException("physical provider cleanup");
        var model = new ScriptModel(false, Call("answer", 7)) { FinallyError = cleanupFailure ? sentinel : null };
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("hook completed")]; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        if (cleanupFailure)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));
            Assert.Same(sentinel, error);
            Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content), block => block is ToolResultBlock);
        }
        else
        {
            var result = await agent.InvokeAsync("question");
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Null(result.StructuredOutput);
            Assert.Equal("hook completed", result.Message!.Text);
            Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        }
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(StructuredOutputBehavior.TypeScript)]
    [InlineData(StructuredOutputBehavior.Python)]
    public async Task DeniedNormalCompletionStillUsesTheProfileForcingWorkflow(StructuredOutputBehavior behavior)
    {
        var hooks = new HookRegistry();
        var before = 0;
        using var registration = hooks.Register<BeforeModelCallEvent>((value, _) => { if (++before == 1) value.CancelReason = "denied"; return default; });
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(behavior), Tools = [Tool("extra")] });
        Assert.Equal(7, Value(await agent.InvokeAsync("question")));
        var real = Assert.Single(model.Requests);
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), real.ToolChoice);
        if (behavior == StructuredOutputBehavior.TypeScript)
        {
            Assert.Equal("question", Assert.Single(real.Messages).Text);
            Assert.Equal(new[] { "extra", OutputName }, real.Tools!.Select(tool => tool.Name));
        }
        else
        {
            Assert.Equal(new[] { "question", "denied", Format().ForcePrompt }, real.Messages.Select(message => message.Text));
            Assert.Equal(OutputName, Assert.Single(real.Tools!).Name);
        }
        Assert.Equal(2, before);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Fact]
    public async Task LegacyStringAndRichCallsReuseOnlyTheirCurrentCorrelatedValue()
    {
        await using var host = new Agent(new() { Model = new ScriptModel(false) });
        var tool = new StructuredOutputTool<JsonElement>(Format(StructuredOutputBehavior.TypeScript));
        Assert.Equal("Successfully validated structured output.", await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { value = 7 }), Context(host, "same")));
        Assert.True(tool.TryGetResult("same", out var first));
        Assert.Equal(7, first.GetProperty("value").GetInt32());
        ToolResultBlock accepted;
        using (var document = JsonDocument.Parse("{\"value\":9}")) accepted = await tool.InvokeResultAsync(document.RootElement, Context(host, "same"));
        Assert.Equal(9, Assert.IsType<JsonBlock>(Assert.Single(accepted.ContentBlocks)).Json.GetProperty("value").GetInt32());
        Assert.True(tool.TryGetResult("same", out var latest));
        Assert.Equal(9, latest.GetProperty("value").GetInt32());
        Assert.Equal(7, first.GetProperty("value").GetInt32());
        Assert.Equal(2, tool.ValidationAttempts);
    }

    private sealed class FailingJsonConverter(Action fail) : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) => fail();
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
        internal Exception? FinallyError { get; init; }
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
            finally { IteratorDisposals++; if (FinallyError is { } error) throw error; }
        }
    }
}
