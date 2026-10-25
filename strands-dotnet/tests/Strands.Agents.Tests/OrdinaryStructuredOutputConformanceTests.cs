using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Tests;

public sealed partial class OrdinaryStructuredOutputMinimumTests
{
    [Fact]
    public void InvalidDefaultRejectsBeforeConstructionCallbacks()
    {
        var callbacks = 0;
        var tool = new FunctionTool(new("ordinary", "ordinary", Format().Schema), (_, _, _) => new("unused"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new()
        {
            Model = new RecordingModel(),
            StructuredOutput = Format() with { MaxValidationAttempts = 0 },
            Tools = [tool],
            ToolFilter = _ => { callbacks++; return true; }
        }));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task InvalidPerCallDoesNotRetainInvocationAdmission()
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.InvokeAsync("bad", new()
        { StructuredOutput = Format() with { MaxValidationAttempts = 0 } }));
        Assert.Empty(model.Requests);
        Assert.Equal(7, (await agent.InvokeAsync("good")).StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task EnumeratingTheSameStreamTwiceUsesFreshValidationState()
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() with { MaxValidationAttempts = 1 } });
        var stream = agent.StreamAsync("answer");
        for (var i = 0; i < 2; i++)
        {
            AgentResult? result = null;
            await foreach (var item in stream)
                if (item is AgentResultEvent completed) result = completed.Result;
            Assert.Equal(7, result!.StructuredOutput!.Value.GetProperty("value").GetInt32());
        }
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task HookRejectionDoesNotPublishAValidatedValue()
    {
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolCallEvent>((value, _) =>
        { value.Result = value.Result with { Status = ToolResultStatus.Error }; return default; });
        await using var agent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format(), Hooks = hooks });
        var result = await agent.InvokeAsync("answer", new() { MaxTurns = 1 });
        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Null(result.StructuredOutput);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task CallerCancellationAfterBodyDoesNotPublishStructuredSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolCallEvent>((_, _) => { cancellation.Cancel(); return default; });
        await using var agent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format(), Hooks = hooks });
        var result = await agent.InvokeAsync("answer", cancellationToken: cancellation.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.StructuredOutput);
    }

    [Fact]
    public async Task ConvertedJsonNullIsDifferentFromAnAbsentResult()
    {
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            StructuredOutput = Format() with { Deserializer = _ => JsonSerializer.SerializeToElement<object?>(null) }
        });
        var result = await agent.InvokeAsync("answer");
        Assert.NotNull(result.StructuredOutput);
        Assert.Equal(JsonValueKind.Null, result.StructuredOutput!.Value.ValueKind);
        Assert.Equal("null", result.ToString());
    }

    [Fact]
    public async Task FailedConversionReturnsFeedbackAndCanBeCorrected()
    {
        var attempts = 0;
        var model = new RecordingModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            StructuredOutput = Format() with
            { Deserializer = value => ++attempts == 1 ? throw new FormatException("business rule") : value.Clone() }
        });
        var result = await agent.InvokeAsync("answer");
        Assert.Equal(7, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, attempts);
        Assert.Equal(2, model.Requests.Count);
        var feedback = Assert.Single(model.Requests[1].Messages.Last().Content.OfType<ToolResultBlock>());
        Assert.Equal(ToolResultStatus.Error, feedback.Status);
        Assert.Contains("business rule", feedback.Content);
    }

    [Fact]
    public async Task HookContinuationPublishesTheLastAcceptedPass()
    {
        var observed = new List<int>();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            observed.Add(value.Result!.StructuredOutput!.Value.GetProperty("value").GetInt32());
            if (observed.Count == 1) value.Resume = InvocationContinuation.Continue;
            return default;
        });
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel((_, count) => JsonSerializer.SerializeToElement(new { value = count }).GetRawText()),
            StructuredOutput = Format(),
            Hooks = hooks
        });
        var result = await agent.InvokeAsync("answer");
        Assert.Equal(new[] { 1, 2 }, observed);
        Assert.Equal(2, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task IdempotentWaiterReceivesThePrimaryAcceptedJson()
    {
        var conversions = 0;
        var model = new BarrierModel();
        await using var agent = new Agent(new()
        { Model = model, StructuredOutput = Format() with { Deserializer = value => { conversions++; return value.Clone(); } } });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "same" });
        Task<AgentResult>? second = null;
        try
        {
            await Bounded(model.Entered.Task);
            second = agent.InvokeAsync("ignored", new() { IdempotencyToken = "same", StructuredOutput = Format("string") });
            Assert.False(second.IsCompleted);
            model.Release.TrySetResult(true);
            var results = await Task.WhenAll(first, second);
            Assert.Same(results[0], results[1]);
            Assert.Equal(7, results[1].StructuredOutput!.Value.GetProperty("value").GetInt32());
            Assert.Equal(1, model.Calls);
            Assert.Equal(1, conversions);
        }
        finally
        {
            model.Release.TrySetResult(true);
            await Bounded(first);
            if (second is not null) await Bounded(second);
        }
    }

    [Fact]
    public async Task CancelingAnIdempotentWaiterDoesNotCancelThePrimary()
    {
        var model = new BarrierModel();
        using var cancellation = new CancellationTokenSource();
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "same" });
        try
        {
            await Bounded(model.Entered.Task);
            var second = agent.InvokeAsync("ignored", new() { IdempotencyToken = "same" }, cancellation.Token);
            Assert.False(second.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(first.IsCompleted);
            model.Release.TrySetResult(true);
            Assert.Equal(7, (await first).StructuredOutput!.Value.GetProperty("value").GetInt32());
            Assert.Equal(1, model.Calls);
        }
        finally { model.Release.TrySetResult(true); await Bounded(first); }
    }

    [Fact]
    public async Task ChildInvocationsHaveSeparateValidationState()
    {
        var model = new RecordingModel();
        await using var parent = new Agent(new() { Model = model, StructuredOutput = Format() with { MaxValidationAttempts = 1 } });
        await using var child = parent.CreateChild();
        Assert.Equal(7, (await parent.InvokeAsync("parent")).StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(7, (await child.InvokeAsync("child")).StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(parent.Messages, message => message.Role == Role.Assistant);
        Assert.Single(child.Messages, message => message.Role == Role.Assistant);
    }

    [Fact]
    public async Task LegacyAgentToolProjectsItsStructuredDefaultAsJsonText()
    {
        await using var parent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format() });
        var tool = parent.AsTool();
        var context = new ToolContext(parent, new("delegated", tool.Spec.Name, JsonSerializer.SerializeToElement(new { task = "answer" })), new Dictionary<string, object?>());
        var text = await tool.InvokeAsync(context.ToolUse.Input, context);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(7, document.RootElement.GetProperty("value").GetInt32());
        Assert.Empty(parent.Messages);
    }

    [Fact]
    public async Task ConstructorCopiesSerializerOptionsBeforeCallerChangesThem()
    {
        var settings = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        settings.Converters.Add(new ConvertedJson());
        await using var agent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format() with { SerializerOptions = settings } });
        settings.Converters.Clear();
        Assert.Equal(99, (await agent.InvokeAsync("answer")).StructuredOutput!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task CustomConvertedJsonIsDetachedFromItsBorrowedDocument()
    {
        using var document = JsonDocument.Parse("{\"value\":99}");
        await using var agent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format() with { Deserializer = _ => document.RootElement } });
        var result = await agent.InvokeAsync("answer");
        document.Dispose();
        Assert.Equal(99, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public void ManualStructuredResultSurvivesJsonRoundTrip()
    {
        var result = new AgentResult(StopReasons.ToolUse, null, new(), 1, new Dictionary<string, object?>())
        { StructuredOutput = JsonSerializer.SerializeToElement(new { value = 7 }) };
        var restored = JsonSerializer.Deserialize<AgentResult>(JsonSerializer.Serialize(result));
        Assert.Equal(7, restored!.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(result.ToString(), restored.ToString());
    }

    private static async Task Bounded(Task task)
    {
        using var stop = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(15), stop.Token);
        try
        {
            if (await Task.WhenAny(task, deadline) != task) throw new TimeoutException("The owned structured-output test operation did not settle.");
            await task;
        }
        finally { stop.Cancel(); }
    }

    private sealed class BarrierModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult(true);
            await Release.Task;
            foreach (var item in ToolCalls(("accepted", StructuredOutputTool<JsonElement>.ToolName, "{\"value\":7}")))
            { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class ConvertedJson : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.SerializeToElement(new { value = 99 });
        }

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) => value.WriteTo(writer);
    }
}
