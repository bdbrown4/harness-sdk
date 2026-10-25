using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Middleware;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputAcceptedResultTests
{
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);



    [Fact]
    public async Task ChangedFinalJsonIsTheTypedAcceptedValue()
    {
        var hooks = Replacing(JsonSerializer.SerializeToElement(new { value = 99 }));
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var result = await agent.InvokeStructuredAsync("question", Typed());
        Assert.Equal(99, result.Output.Value);
        Assert.Equal(99, FinalJson(agent).GetProperty("value").GetInt32());
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Fact]
    public async Task AgentNodeUsesChangedAcceptedJsonWithItsOriginalToolCallContent()
    {
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = Replacing(JsonSerializer.SerializeToElement(new { value = 99 })) });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent, options: new() { StructuredOutput = Format(StructuredOutputBehavior.TypeScript) })] });
        var result = await graph.InvokeAsync("question");
        Assert.Equal(Status.Completed, result.Status);
        var node = Assert.Single(result.Results);
        Assert.Equal(99, node.StructuredOutput!.Value.GetProperty("value").GetInt32());
        var call = Assert.IsType<ToolUseBlock>(Assert.Single(node.Content));
        Assert.Equal("answer", call.ToolUseId);
        Assert.Equal(7, call.Input.GetProperty("value").GetInt32());
        Assert.Empty(agent.Messages);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task JoinedTypedCallReceivesTheSameFinalValueWithoutAnotherProviderOrHook()
    {
        var entered = Signal();
        var release = Signal();
        var hooks = new HookRegistry();
        var hookCalls = 0;
        using var registration = hooks.Register<AfterToolCallEvent>(async (value, _) =>
        {
            hookCalls++;
            value.Result = new(value.ToolUse.ToolUseId, [new JsonBlock(JsonSerializer.SerializeToElement(new { value = 99 }))]);
            entered.TrySetResult(true);
            await release.Task;
        });
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Task<StructuredOutputResult<Payload>>? first = null;
        Task<StructuredOutputResult<Payload>>? joined = null;
        try
        {
            first = agent.InvokeStructuredAsync("question", Typed(), new() { IdempotencyToken = "same-call" });
            await Wait(entered.Task);
            joined = agent.InvokeStructuredAsync("question", Typed(), new() { IdempotencyToken = "same-call" });
            Assert.False(first.IsCompleted);
            Assert.False(joined.IsCompleted);
            release.TrySetResult(true);
            var original = await first;
            var reused = await joined;
            Assert.Equal(99, original.Output.Value);
            Assert.Same(original.Output, reused.Output);
            Assert.Equal(99, reused.Output.Value);
            Assert.Single(model.Requests);
            Assert.Equal(1, hookCalls);
            Assert.Equal(1, model.IteratorDisposals);
        }
        finally { release.TrySetResult(true); await Drain(first); await Drain(joined); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalExplicitNullIsAcceptedByOrdinaryAndNullableTypedCalls(bool typed)
    {
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new()
        {
            Model = model,
            Hooks = Replacing(JsonSerializer.SerializeToElement<object?>(null)),
            StructuredOutput = Format(StructuredOutputBehavior.TypeScript)
        });
        if (typed)
        {
            var result = await agent.InvokeStructuredAsync("question", new StructuredOutputOptions<Payload?>
            { Schema = Format().Schema, Behavior = StructuredOutputBehavior.TypeScript });
            Assert.Null(result.Output);
            Assert.Equal(StopReasons.ToolUse, result.Result.StopReason);
        }
        else
        {
            var result = await agent.InvokeAsync("question");
            Assert.NotNull(result.StructuredOutput);
            Assert.Equal(JsonValueKind.Null, result.StructuredOutput.Value.ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, FinalJson(agent).ValueKind);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task ChangedJsonOutsideTheNativeTypeFailsAfterAcceptedHistoryWithoutRevalidation()
    {
        var json = JsonSerializer.SerializeToElement("changed by hook");
        var model = new ScriptModel(false, Call("answer", 7));
        await using (var agent = new Agent(new() { Model = model, Hooks = Replacing(json) }))
        {
            var failure = await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("question", Typed()));
            Assert.IsType<JsonException>(failure.InnerException);
            Assert.Equal("changed by hook", FinalJson(agent).GetString());
            Assert.Single(model.Requests);
        }
        var ordinary = new ScriptModel(false, Call("answer", 7));
        await using (var agent = new Agent(new() { Model = ordinary, Hooks = Replacing(json), StructuredOutput = Format(StructuredOutputBehavior.TypeScript) }))
        {
            var result = await agent.InvokeAsync("question");
            Assert.Equal("changed by hook", result.StructuredOutput!.Value.GetString());
            Assert.Single(ordinary.Requests);
        }
    }

    [Fact]
    public async Task UnmodifiedEmittedJsonDoesNotRepeatCustomValidationOrConverters()
    {
        var payload = new Payload(99);
        var validations = 0;
        var reads = 0;
        var writes = 0;
        var settings = new JsonSerializerOptions(Web);
        settings.Converters.Add(new PayloadConverter(() => { reads++; throw new InvalidOperationException("No second conversion is allowed."); }, () => writes++));
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model });
        var options = Typed() with { SerializerOptions = settings, Deserializer = _ => { validations++; return payload; } };
        var result = await agent.InvokeStructuredAsync("question", options);
        Assert.Same(payload, result.Output);
        Assert.Equal(1, validations);
        Assert.Equal(0, reads);
        Assert.Equal(1, writes);
        Assert.Equal(99, FinalJson(agent).GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedTypedConversionPreservesRequestedCallerVersusIndependentCancellation(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        var sentinel = new OperationCanceledException("independent typed conversion", independent.Token);
        var settings = new JsonSerializerOptions(Web);
        settings.Converters.Add(new PayloadConverter(() =>
        {
            if (cancelCaller) { caller.Cancel(); throw new OperationCanceledException(caller.Token); }
            throw sentinel;
        }));
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new() { Model = model, Hooks = Replacing(JsonSerializer.SerializeToElement(new { value = 99 })) });
        var options = Typed() with { SerializerOptions = settings, Deserializer = _ => new Payload(7) };
        if (cancelCaller)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync("question", options, cancellationToken: caller.Token));
            Assert.Equal(caller.Token, failure.CancellationToken);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("question", options, cancellationToken: caller.Token));
            Assert.Same(sentinel, failure.InnerException);
            Assert.False(caller.IsCancellationRequested);
        }
        Assert.Equal(99, FinalJson(agent).GetProperty("value").GetInt32());
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Fact]
    public async Task FinalHookJsonCanSupersedeValidationFeedbackWithoutASecondValidation()
    {
        var invalid = new Turn(StopReasons.ToolUse, [new ToolUseBlock("answer", OutputName, JsonSerializer.SerializeToElement(new { value = "bad" }))]);
        var model = new ScriptModel(false, invalid);
        await using var agent = new Agent(new()
        { Model = model, Hooks = Replacing(JsonSerializer.SerializeToElement(new { value = 99 })), StructuredOutput = Format(StructuredOutputBehavior.TypeScript) });
        Assert.Equal(99, Value(await agent.InvokeAsync("question")));
        Assert.Equal(ToolResultStatus.Success, Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages.Last().Content)).Status);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task PublicStructuredMarkerDoesNotSelectAnExternalToolIntoTheProfileSlot()
    {
        var external = new ExternalStructuredTool("ordinary");
        var ordinary = new ScriptModel(false, new Turn(StopReasons.ToolUse, [new ToolUseBlock("external", "ordinary", JsonSerializer.SerializeToElement(new { }))]), Text("done"));
        await using (var agent = new Agent(new() { Model = ordinary, Tools = [external] }))
        {
            var result = await agent.InvokeAsync("question");
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Null(result.StructuredOutput);
            Assert.Equal(1, external.Calls);
        }
        var selected = new ScriptModel(false, Call("answer", 7));
        await using (var agent = new Agent(new() { Model = selected, Tools = [external], StructuredOutput = Format(StructuredOutputBehavior.TypeScript) }))
        {
            Assert.Equal(7, Value(await agent.InvokeAsync("question")));
            Assert.Equal(1, external.Calls);
            Assert.Same(external, Assert.Single(agent.Tools));
        }
        var never = new ScriptModel(false);
        await using (var agent = new Agent(new() { Model = never, Tools = [new ExternalStructuredTool(OutputName)], StructuredOutput = Format(StructuredOutputBehavior.TypeScript) }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("question"));
            Assert.Empty(never.Requests);
        }
    }

    [Fact]
    public async Task PythonKeepsItsValidatedValueWhenFinalDisplayJsonChanges()
    {
        var model = new ScriptModel(false, Call("answer", 7));
        await using var agent = new Agent(new()
        { Model = model, Hooks = Replacing(JsonSerializer.SerializeToElement(new { value = 99 })), StructuredOutput = Format(StructuredOutputBehavior.Python) });
        Assert.Equal(7, Value(await agent.InvokeAsync("question")));
        Assert.Equal(99, FinalJson(agent).GetProperty("value").GetInt32());
        Assert.Single(model.Requests);
    }

    public sealed record Payload(int Value);
    private static StructuredOutputOptions<Payload> Typed() => new()
    { Schema = Format().Schema, Behavior = StructuredOutputBehavior.TypeScript };
    private static JsonElement FinalJson(Agent agent)
    {
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages.Last().Content));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        return Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json;
    }
    private static HookRegistry Replacing(JsonElement json)
    {
        var hooks = new HookRegistry();
        var captured = json.Clone();
        hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new(value.ToolUse.ToolUseId, [new JsonBlock(captured)]); return default; });
        return hooks;
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private static async Task Drain(Task? task)
    { if (task is null) return; try { await task; } catch (Exception) { /* The test body owns the outcome; cleanup joins. */ } }
    private sealed class PayloadConverter(Action? reading = null, Action? writing = null) : JsonConverter<Payload>
    {
        public override Payload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reading?.Invoke();
            using var document = JsonDocument.ParseValue(ref reader);
            return new(document.RootElement.GetProperty("value").GetInt32());
        }
        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options)
        { writing?.Invoke(); writer.WriteStartObject(); writer.WriteNumber("value", value.Value); writer.WriteEndObject(); }
    }
    private sealed class ExternalStructuredTool(string name) : IStructuredOutputTool
    {
        public ToolSpec Spec { get; } = new(name, "External ordinary tool", JsonSerializer.SerializeToElement(new { type = "object" }));
        internal int Calls { get; private set; }
        public bool HasResult => throw new InvalidOperationException("External cache must not be read.");
        public object? Result => throw new InvalidOperationException("External cache must not be read.");
        public bool TryGetResult(string toolUseId, out object? result) => throw new InvalidOperationException("External cache must not be read.");
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return new("ordinary result"); }
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
