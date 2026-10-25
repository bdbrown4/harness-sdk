using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Middleware;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputPreemptionSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentNullIsDetachedAndCustomConversionRunsOnlyOnce(bool cancel)
    {
        using var producer = JsonDocument.Parse("null");
        var converted = 0;
        var model = new OneModel();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) =>
        {
            producer.Dispose();
            if (cancel) value.Agent.Cancel("cancelled", afterCurrentTools: true);
            else value.EndTurn = [new TextBlock("done")];
            return default;
        });
        var options = new StructuredOutputOptions<JsonElement>
        {
            Schema = Schema(),
            Behavior = StructuredOutputBehavior.Python,
            Deserializer = _ => { converted++; return producer.RootElement; }
        };
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = options });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(cancel ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonElement>(result.StructuredOutput).ValueKind);
        Assert.Equal(1, converted);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedConveniencePreservesItsNonToolUsePreemptionContract(bool cancel)
    {
        var converter = new PayloadConverter();
        var model = new OneModel();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) =>
        {
            if (cancel) value.Agent.Cancel("cancelled", afterCurrentTools: true);
            else value.EndTurn = [new TextBlock("done")];
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("question", Typed(converter)));

        Assert.Equal(1, converter.Reads);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.Disposals);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task HardCallerRetainsItsExactTokenAfterValidatedBody()
    {
        using var caller = new CancellationTokenSource();
        var converter = new PayloadConverter();
        var model = new OneModel();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((_, _) => { caller.Cancel(); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync("question", Typed(converter), cancellationToken: caller.Token));

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, converter.Reads);
        Assert.Equal(1, model.Disposals);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task ProviderCleanupFailureWinsBeforeToolValidationOrFinalBatchHooks()
    {
        var primary = new IOException("provider physical cleanup");
        var model = new OneModel { CleanupError = primary };
        var converted = 0;
        var after = 0;
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) => { after++; value.EndTurn = [new TextBlock("done")]; return default; });
        var options = new StructuredOutputOptions<JsonElement> { Schema = Schema(), Behavior = StructuredOutputBehavior.Python, Deserializer = element => { converted++; return element; } };
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = options });

        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(primary, error);
        Assert.Equal(0, converted);
        Assert.Equal(0, after);
        Assert.Equal(1, model.Disposals);
        Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content), block => block is ToolResultBlock);
    }

    [Fact]
    public async Task ToolMiddlewareCleanupRemainsPrimaryAfterAValidatedValueExists()
    {
        var primary = new IOException("tool middleware physical cleanup");
        var model = new OneModel();
        var converted = 0;
        var cleaned = 0;
        var middleware = new MiddlewareRegistry();
        using var layer = middleware.RegisterWrap(MiddlewareStages.ExecuteTool, Close);
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("done")]; return default; });
        var options = new StructuredOutputOptions<JsonElement> { Schema = Schema(), Behavior = StructuredOutputBehavior.Python, Deserializer = element => { converted++; return element; } };
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Middleware = middleware, StructuredOutput = options });

        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(primary, error);
        Assert.Equal(1, converted);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, model.Disposals);

        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Close(ExecuteToolContext context,
            MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try { await foreach (var item in next(context)) yield return item; }
            finally { cleaned++; throw primary; }
        }
    }

    [Fact]
    public async Task StreamCarriesTheCachedPythonPayloadWithItsEndTurnEvent()
    {
        var model = new OneModel();
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("done")]; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = new() { Schema = Schema(), Behavior = StructuredOutputBehavior.Python } });
        var results = new List<AgentResult>();

        await foreach (var item in agent.StreamAsync("question")) if (item is AgentResultEvent result) results.Add(result.Result);

        var accepted = Assert.Single(results);
        Assert.Equal(StopReasons.EndTurn, accepted.StopReason);
        Assert.Equal(7, accepted.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal("done", accepted.Message!.Text);
        Assert.Equal(1, model.Disposals);
    }

    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false });
    private static StructuredOutputOptions<Payload> Typed(PayloadConverter converter)
    {
        var settings = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        settings.Converters.Add(converter);
        return new() { Schema = Schema(), Behavior = StructuredOutputBehavior.Python, SerializerOptions = settings };
    }
    private sealed class Payload(int value) { internal int Value { get; } = value; }
    private sealed class PayloadConverter : JsonConverter<Payload>
    {
        internal int Reads { get; private set; }
        public override Payload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Reads++;
            if (Reads != 1) throw new InvalidOperationException("The accepted typed value cannot be converted twice.");
            using var document = JsonDocument.ParseValue(ref reader);
            return new(document.RootElement.GetProperty("value").GetInt32());
        }
        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options)
        { writer.WriteStartObject(); writer.WriteNumber("value", value.Value); writer.WriteEndObject(); }
    }
    private sealed class OneModel : IModel
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        internal Exception? CleanupError { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls != 1) throw new InvalidOperationException("The preempting boundary must not run a second provider turn.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("answer", StructuredOutputTool<JsonElement>.ToolName, JsonSerializer.SerializeToElement(new { value = 7 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
                await Task.CompletedTask;
            }
            finally { Disposals++; if (CleanupError is { } error) throw error; }
        }
    }
}
