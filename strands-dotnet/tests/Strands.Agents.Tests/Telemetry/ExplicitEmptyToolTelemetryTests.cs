using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class ExplicitEmptyToolTelemetryTests
{
    private const string CallId = "telemetry-result-one";
    private const string ToolName = "trace_result";
    private const string LegacySentinel = "legacy-content-must-not-be-traced";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeEmptyToolResultStaysEmptyInCompletedToolTrace(bool latest)
    {
        var observation = await InvokeAsync(ExplicitEmpty(), latest);

        Assert.Empty(ToolResponse(observation.Tool, latest).EnumerateArray());
        if (latest)
            Assert.Empty(ReadJson(Value(observation.Tool, "gen_ai.tool.call.result")).EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeEmptyToolResultStaysEmptyInFollowingModelTrace(bool latest)
    {
        var observation = await InvokeAsync(ExplicitEmpty(), latest);

        Assert.Empty(FollowingModelResponse(observation.FollowingModel, latest).EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTextFallbackRemainsVisibleInActualToolAndFollowingModelTraces(bool latest)
    {
        var observation = await InvokeAsync(new(CallId, "legacy-text"), latest);

        Assert.False(observation.AcceptedResult.HasExplicitContent);
        Assert.Empty(observation.AcceptedResult.ContentBlocks);
        AssertText(ToolResponse(observation.Tool, latest), "legacy-text");
        AssertText(FollowingModelResponse(observation.FollowingModel, latest), "legacy-text");
        if (latest)
            AssertText(ReadJson(Value(observation.Tool, "gen_ai.tool.call.result")), "legacy-text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonemptyRichResultRetainsOrderedTextAndJsonInActualTraces(bool latest)
    {
        var result = new ToolResultBlock(CallId, new ContentBlock[]
        {
            new TextBlock("rich-text"),
            new JsonBlock(JsonSerializer.SerializeToElement(new { answer = 7 })),
        })
        { HasExplicitContent = true };
        var observation = await InvokeAsync(result, latest);

        Assert.True(observation.AcceptedResult.HasExplicitContent);
        Assert.Equal(2, observation.AcceptedResult.ContentBlocks.Count);
        AssertRich(ToolResponse(observation.Tool, latest));
        AssertRich(FollowingModelResponse(observation.FollowingModel, latest));
        if (latest)
            AssertRich(ReadJson(Value(observation.Tool, "gen_ai.tool.call.result")));
    }

    private static ToolResultBlock ExplicitEmpty()
        => new(CallId, LegacySentinel) { ContentBlocks = Array.Empty<ContentBlock>(), HasExplicitContent = true };

    private static async Task<(Activity Tool, Activity FollowingModel, ToolResultBlock AcceptedResult)> InvokeAsync(
        ToolResultBlock result, bool latest)
    {
        using var environment = new TelemetryEnvironment(latest);
        var caseId = Guid.NewGuid().ToString("N");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.GetTagItem("tg06.test") as string, caseId, StringComparison.Ordinal))
                    stopped.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var model = new ToolThenTextModel();
        var tool = new ResultTool(result);
        await using var agent = new Agent(new()
        {
            Model = model,
            Tools = [tool],
            TraceAttributes = new Dictionary<string, object?> { ["tg06.test"] = caseId },
        });

        var final = await agent.InvokeAsync("exercise the result projection");

        Assert.Equal("done", final.ToString());
        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, model.Requests.Count);
        var accepted = Assert.Single(model.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(CallId, accepted.ToolUseId);
        Assert.Equal(result.HasExplicitContent, accepted.HasExplicitContent);
        Assert.Equal(result.Content, accepted.Content);
        Assert.Equal(result.ContentBlocks.Count, accepted.ContentBlocks.Count);
        if (result.HasExplicitContent && result.ContentBlocks.Count == 0)
        {
            Assert.Empty(accepted.ContentBlocks);
            Assert.Equal(LegacySentinel, accepted.Content);
        }

        var activities = stopped.ToArray();
        var toolActivity = Assert.Single(activities, activity => activity.DisplayName == "execute_tool " + ToolName);
        var models = activities.Where(activity => activity.DisplayName == "chat").ToArray();
        Assert.Equal(2, models.Length);
        return (toolActivity, models[1], accepted);
    }

    private static JsonElement ToolResponse(Activity activity, bool latest)
    {
        Assert.Equal("success", activity.GetTagItem("gen_ai.tool.status"));
        if (latest)
        {
            var output = ReadJson(Value(activity, "gen_ai.output.messages"));
            var part = output[0].GetProperty("parts")[0];
            Assert.Equal("tool_call_response", part.GetProperty("type").GetString());
            Assert.Equal(CallId, part.GetProperty("id").GetString());
            return part.GetProperty("response").Clone();
        }

        var choice = Assert.Single(activity.Events, value => value.Name == "gen_ai.choice");
        Assert.Equal(CallId, Assert.Single(choice.Tags, pair => pair.Key == "id").Value);
        return ReadJson(Assert.IsType<string>(Assert.Single(choice.Tags, pair => pair.Key == "message").Value));
    }

    private static JsonElement FollowingModelResponse(Activity activity, bool latest)
    {
        if (latest)
        {
            var input = ReadJson(Value(activity, "gen_ai.input.messages"));
            var parts = input.EnumerateArray().SelectMany(message => message.GetProperty("parts").EnumerateArray());
            var result = Assert.Single(parts, part => part.GetProperty("type").GetString() == "tool_call_response");
            Assert.Equal(CallId, result.GetProperty("id").GetString());
            return result.GetProperty("response").Clone();
        }

        var message = Assert.Single(activity.Events, value => value.Name == "gen_ai.tool.message");
        var blocks = ReadJson(Assert.IsType<string>(Assert.Single(message.Tags, pair => pair.Key == "content").Value));
        var resultBlock = Assert.Single(blocks.EnumerateArray(), block => block.GetProperty("type").GetString() == "toolResultBlock");
        Assert.Equal(CallId, resultBlock.GetProperty("toolUseId").GetString());
        return resultBlock.GetProperty("content").Clone();
    }

    private static string Value(Activity activity, string name)
        => Assert.IsType<string>(activity.GetTagItem(name)
            ?? Assert.Single(activity.Events.SelectMany(value => value.Tags), pair => pair.Key == name).Value);

    private static JsonElement ReadJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static void AssertText(JsonElement content, string expected)
    {
        var part = Assert.Single(content.EnumerateArray());
        Assert.Equal(expected, part.GetProperty("text").GetString());
    }

    private static void AssertRich(JsonElement content)
        => Assert.Collection(content.EnumerateArray(),
            part => Assert.Equal("rich-text", part.GetProperty("text").GetString()),
            part => Assert.Equal(7, part.GetProperty("json").GetProperty("answer").GetInt32()));

    private sealed class ResultTool(ToolResultBlock result) : IRichTool
    {
        public ToolSpec Spec { get; } = new(ToolName, "Return one fixed rich result.",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        internal int Calls { get; private set; }

        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The agent must use this tool's rich result path.");

        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(CallId, context.ToolUse.ToolUseId);
            Calls++;
            return new(result);
        }
    }

    private sealed class ToolThenTextModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count > 2)
                throw new InvalidOperationException("The fixture permits exactly one tool turn and one final text turn.");
            var toolTurn = Requests.Count == 1;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, toolTurn
                ? new ToolUseBlock(CallId, ToolName, JsonSerializer.SerializeToElement(new { }))
                : null);
            if (!toolTurn)
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(toolTurn ? StopReasons.ToolUse : StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(1, 1));
        }
    }

    private sealed class TelemetryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        internal TelemetryEnvironment(bool latest)
        {
            foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "LANGFUSE_BASE_URL" })
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, name == "OTEL_SEMCONV_STABILITY_OPT_IN"
                    ? latest ? "gen_ai_latest_experimental,gen_ai_span_attributes_only" : ""
                    : null);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
