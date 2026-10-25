using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed partial class OrdinaryStructuredOutputMinimumTests
{
    [Fact]
    public async Task DefaultFormatIsUsedByOrdinaryInvoke()
    {
        var model = new RecordingModel();
        var state = new Dictionary<string, object?> { ["caller"] = 1 };
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });

        var result = await agent.InvokeAsync("answer", new() { InvocationState = state });

        Assert.Equal(7, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(StopReasons.ToolUse, result.StopReason);
        Assert.Single(model.Requests);
        Assert.Same(state, result.InvocationState);
        Assert.Single(state);
    }

    [Fact]
    public async Task PerCallFormatOverridesDefaultForOneInvocation()
    {
        var model = new RecordingModel((request, _) => StructuredSpec(request).Description.IndexOf("override", StringComparison.Ordinal) >= 0
            ? "{\"value\":\"changed\"}" : "{\"value\":7}");
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        var alternate = Format("string") with { Description = "override" };

        var changed = await agent.InvokeAsync("first", new() { StructuredOutput = alternate });
        Assert.Equal("changed", changed.StructuredOutput!.Value.GetProperty("value").GetString());
        var original = await agent.InvokeAsync("second");

        Assert.Equal(7, original.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("string", StructuredSpec(model.Requests[0]).InputSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("integer", StructuredSpec(model.Requests[1]).InputSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public async Task OrdinaryStreamHooksObserveAcceptedValue()
    {
        var hooks = new HookRegistry();
        AgentResult? observed = null;
        using var registration = hooks.Register<AgentResultEvent>((value, _) => { observed = value.Result; return default; });
        await using var agent = new Agent(new() { Model = new RecordingModel(), Hooks = hooks });
        var events = new List<IAgentStreamEvent>();

        await foreach (var item in agent.StreamAsync("answer", new() { StructuredOutput = Format() })) events.Add(item);

        var terminal = Assert.Single(events.OfType<AgentResultEvent>());
        Assert.Same(observed, terminal.Result);
        Assert.Equal(7, observed!.StructuredOutput!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task EachInvocationUsesFreshValidationState()
    {
        var conversions = 0;
        var format = Format() with { MaxValidationAttempts = 1, Deserializer = value => { conversions++; return value.Clone(); } };
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, StructuredOutput = format });

        var first = await agent.InvokeAsync("first");
        Assert.NotNull(first.StructuredOutput);
        var second = await agent.InvokeAsync("second");

        Assert.NotNull(second.StructuredOutput);
        Assert.Equal(2, conversions);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public void ResultOwnsAndProjectsStructuredJsonAsText()
    {
        AgentResult result;
        using (var document = JsonDocument.Parse("{\"value\":7}"))
            result = new(StopReasons.ToolUse, Message.Assistant("plain"), new(), 1, new Dictionary<string, object?>())
            { StructuredOutput = document.RootElement };

        Assert.Equal("{\"value\":7}", result.ToString());
        Assert.Equal(7, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PlainInvocationRetainsLegacyResultAndJsonShape()
    {
        await using var agent = new Agent(new() { Model = new RecordingModel() });
        var result = await agent.InvokeAsync("answer");

        Assert.Null(result.StructuredOutput);
        Assert.Equal("plain", result.ToString());
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.DoesNotContain(serialized.RootElement.EnumerateObject(), property => property.Name.Equals("StructuredOutput", StringComparison.OrdinalIgnoreCase));
        var (reason, message, usage, turns, state) = result;
        Assert.Equal(StopReasons.EndTurn, reason);
        Assert.Same(result.Message, message);
        Assert.Equal(result.Usage, usage);
        Assert.Equal(1, turns);
        Assert.Same(result.InvocationState, state);
    }

    [Fact]
    public async Task ExplicitTypedInvocationTakesPrecedenceOverTheDefault()
    {
        await using var agent = new Agent(new() { Model = new RecordingModel(), StructuredOutput = Format("string") });
        var typed = new StructuredOutputOptions<Answer> { Schema = Format().Schema };

        var result = await agent.InvokeStructuredAsync("answer", typed);

        Assert.Equal(7, result.Output.Value);
        Assert.Equal(StopReasons.ToolUse, result.Result.StopReason);
    }

    [Fact]
    public async Task ConfiguredSchemaIsDetachedBeforeItsDocumentIsDisposed()
    {
        Agent agent;
        var model = new RecordingModel();
        using (var document = JsonDocument.Parse(Format().Schema.GetRawText()))
            agent = new(new() { Model = model, StructuredOutput = Format() with { Schema = document.RootElement } });
        await using (agent)
        {
            var result = await agent.InvokeAsync("answer");
            Assert.Equal(7, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
            Assert.Single(model.Requests);
        }
    }

    private static StructuredOutputOptions<JsonElement> Format(string type = "integer") => new()
    {
        Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = new { type } },
            required = new[] { "value" },
            additionalProperties = false
        })
    };

    private static ModelStreamEvent[] TextTurn(string text) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0),
        new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new())];

    private static ModelStreamEvent[] ToolCalls(params (string Id, string Name, string Json)[] calls)
    {
        List<ModelStreamEvent> events = [new ModelMessageStartEvent()];
        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            events.Add(new ModelContentBlockStartEvent(i, new(call.Id, call.Name, JsonSerializer.SerializeToElement(new { }))));
            events.Add(new ModelContentBlockDeltaEvent(i, ToolInput: call.Json));
            events.Add(new ModelContentBlockStopEvent(i));
        }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }

    private static ToolSpec StructuredSpec(ModelRequest request) => Assert.Single(request.Tools!, tool => tool.Name == StructuredOutputTool<JsonElement>.ToolName);

    private sealed class RecordingModel(Func<ModelRequest, int, string>? output = null) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var configured = request.Tools?.Any(tool => tool.Name == StructuredOutputTool<JsonElement>.ToolName) == true;
            var events = configured ? ToolCalls(("structured-" + Requests.Count, StructuredOutputTool<JsonElement>.ToolName,
                output?.Invoke(request, Requests.Count) ?? "{\"value\":7}")) : TextTurn("plain");
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    public sealed record Answer(int Value);
}
