using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Models;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class TelemetryContentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ModelInputInstructionsAndOutputUseSelectedConvention(bool latest, bool attributesOnly)
    {
        using var environment = new TelemetryEnvironment(Tokens(latest, attributesOnly));
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartModelInvokeSpan("model");
        span.RecordInput(new([Message.User("input-secret")], "system-secret"));
        span.End(Message.Assistant("output-secret"), stopReason: StopReasons.EndTurn);

        var activity = Assert.Single(stopped);
        if (latest)
        {
            using var input = JsonDocument.Parse(Value(activity, "gen_ai.input.messages"));
            Assert.Equal("user", input.RootElement[0].GetProperty("role").GetString());
            Assert.Equal("input-secret", input.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString());
            using var instructions = JsonDocument.Parse(Value(activity, "gen_ai.system_instructions"));
            Assert.Equal("system-secret", instructions.RootElement[0].GetProperty("content").GetString());
            using var output = JsonDocument.Parse(Value(activity, "gen_ai.output.messages"));
            Assert.Equal("output-secret", output.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString());
            Assert.Equal(StopReasons.EndTurn, output.RootElement[0].GetProperty("finish_reason").GetString());
            Assert.Equal(attributesOnly, activity.GetTagItem("gen_ai.input.messages") is not null);
            Assert.Equal(attributesOnly ? 0 : 3, activity.Events.Count(item => item.Name == "gen_ai.client.inference.operation.details"));
        }
        else
        {
            var input = Assert.Single(activity.Events, item => item.Name == "gen_ai.user.message");
            Assert.Contains("input-secret", (string)input.Tags.Single(pair => pair.Key == "content").Value!);
            Assert.Contains(activity.Events, item => item.Name == "gen_ai.system.message");
            var output = Assert.Single(activity.Events, item => item.Name == "gen_ai.choice");
            Assert.Equal(StopReasons.EndTurn, output.Tags.Single(pair => pair.Key == "finish_reason").Value);
            Assert.Contains("output-secret", (string)output.Tags.Single(pair => pair.Key == "message").Value!);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("gen_ai.input.messages", true)]
    [InlineData("gen_ai.input.*", true)]
    [InlineData("gen_ai.*.messages", false)]
    [InlineData("GEN_AI.INPUT.MESSAGES", false)]
    [InlineData(" ignored ; gen_ai.input.messages ; ", true)]
    public void PythonSensitiveAttributeAllowlistSupportsExactAndTrailingPrefixOnly(string? allowlist, bool inputAllowed)
    {
        var tokens = "gen_ai_latest_experimental,gen_ai_span_attributes_only";
        if (allowlist is not null) tokens += ",gen_ai_unredacted_attributes=" + allowlist;
        using var environment = new TelemetryEnvironment(tokens);
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartModelInvokeSpan();
        span.RecordInput(new([Message.User("private-input")], "private-system"));
        span.End(Message.Assistant("private-output"));

        var activity = Assert.Single(stopped);
        Assert.Equal(inputAllowed, Value(activity, "gen_ai.input.messages").Contains("private-input"));
        Assert.Equal(allowlist is null, Value(activity, "gen_ai.system_instructions").Contains("private-system"));
        Assert.Equal(allowlist is null, Value(activity, "gen_ai.output.messages").Contains("private-output"));
        if (!inputAllowed) Assert.Equal("[REDACTED]", Value(activity, "gen_ai.input.messages"));
        Assert.Equal("private-output", span.Trace!.Message!.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEventsUseCanonicalRedactionPolicyEvenWhenAttributeOnlyFlagIsEnabled(bool allowInput)
    {
        var tokens = "gen_ai_span_attributes_only,gen_ai_unredacted_attributes=" + (allowInput ? "gen_ai.input.messages" : "");
        using var environment = new TelemetryEnvironment(tokens);
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartModelInvokeSpan();
        span.RecordInput(new([Message.User("legacy-secret")], "system-secret"));
        span.End(Message.Assistant("output-secret"));

        var activity = Assert.Single(stopped);
        var input = Assert.Single(activity.Events, item => item.Name == "gen_ai.user.message");
        var value = (string)input.Tags.Single(pair => pair.Key == "content").Value!;
        Assert.Equal(allowInput, value.Contains("legacy-secret"));
        Assert.Null(activity.GetTagItem("content"));
        Assert.Equal("[REDACTED]", Assert.Single(activity.Events, item => item.Name == "gen_ai.system.message").Tags.Single(pair => pair.Key == "content").Value);
        Assert.Equal("[REDACTED]", Assert.Single(activity.Events, item => item.Name == "gen_ai.choice").Tags.Single(pair => pair.Key == "message").Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TextOnlyToolResultIsRetainedInBothConventionsIncludingErrors(bool latest, bool failed)
    {
        using var environment = new TelemetryEnvironment(Tokens(latest, true));
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartToolCallSpan(new("call-one", "calculate", JsonSerializer.SerializeToElement(new { value = 7 })));
        span.EndToolCall(new("call-one", "tool-text", failed ? ToolResultStatus.Error : ToolResultStatus.Success));

        var activity = Assert.Single(stopped);
        Assert.Equal(failed ? "error" : "success", activity.GetTagItem("gen_ai.tool.status"));
        if (latest)
        {
            using var output = JsonDocument.Parse(Value(activity, "gen_ai.output.messages"));
            var part = output.RootElement[0].GetProperty("parts")[0];
            Assert.Equal("call-one", part.GetProperty("id").GetString());
            Assert.Equal("tool-text", part.GetProperty("response")[0].GetProperty("text").GetString());
            Assert.Equal(!failed, activity.GetTagItem("gen_ai.tool.call.result") is not null);
        }
        else
        {
            var output = Assert.Single(activity.Events, item => item.Name == "gen_ai.choice");
            using var content = JsonDocument.Parse((string)output.Tags.Single(pair => pair.Key == "message").Value!);
            Assert.Equal("tool-text", content.RootElement[0].GetProperty("text").GetString());
            Assert.Equal("call-one", output.Tags.Single(pair => pair.Key == "id").Value);
        }
    }

    [Fact]
    public void RedactedToolOutputDoesNotSerializePrivateUnsupportedContent()
    {
        using var environment = new TelemetryEnvironment("gen_ai_latest_experimental,gen_ai_span_attributes_only,gen_ai_unredacted_attributes=");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var span = new Tracer().StartToolCallSpan(new("call", "custom", JsonSerializer.SerializeToElement(new { secret = "input" })));
        var result = new ToolResultBlock("call", new ContentBlock[] { new CyclicBlock() });

        span.EndToolCall(result);

        var activity = Assert.Single(stopped);
        Assert.Equal("[REDACTED]", Value(activity, "gen_ai.output.messages"));
        Assert.Equal("[REDACTED]", activity.GetTagItem("gen_ai.tool.call.result"));
        Assert.Equal("[REDACTED]", activity.GetTagItem("gen_ai.tool.call.arguments"));
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT")]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")]
    [InlineData("LANGFUSE_BASE_URL")]
    public void LangfuseUsesAggregatedAttributesAndMarksAgentToAvoidDuplicateGenerations(string variable)
    {
        using var environment = new TelemetryEnvironment("gen_ai_latest_experimental");
        Environment.SetEnvironmentVariable(variable, "https://cloud.langfuse.com");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var span = new Tracer().StartAgentSpan("assistant");
        span.RecordInput(new([Message.User("input")], "instructions"));
        span.End(Message.Assistant("answer"));

        var activity = Assert.Single(stopped);
        Assert.Contains("input", Assert.IsType<string>(activity.GetTagItem("gen_ai.input.messages")));
        Assert.Equal("span", activity.GetTagItem("langfuse.observation.type"));
        Assert.DoesNotContain(activity.Events, item => item.Name == "gen_ai.client.inference.operation.details");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolDefinitionsRequireAnExplicitOptIn(bool includeDefinitions)
    {
        using var environment = new TelemetryEnvironment(includeDefinitions ? "gen_ai_tool_definitions" : "");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tool = new ToolSpec("calculate", "Calculate", JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } } }));
        var span = new Tracer().StartAgentSpan("assistant");
        span.RecordInput(new([Message.User("input")], Tools: [tool]));
        span.End();

        var activity = Assert.Single(stopped);
        Assert.Equal("[\"calculate\"]", activity.GetTagItem("gen_ai.agent.tools"));
        Assert.Equal(includeDefinitions, activity.GetTagItem("gen_ai.tool.definitions") is not null);
        if (includeDefinitions)
        {
            using var definitions = JsonDocument.Parse((string)activity.GetTagItem("gen_ai.tool.definitions")!);
            Assert.Equal("integer", definitions.RootElement.GetProperty("calculate").GetProperty("inputSchema").GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("refusal")]
    [InlineData("google")]
    [InlineData("anthropic")]
    [InlineData("citations")]
    public void NativeProviderTextRemainsVisibleInModelAndAgentTraceContent(string provider)
    {
        using var environment = new TelemetryEnvironment("gen_ai_latest_experimental,gen_ai_span_attributes_only");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        ContentBlock block = provider switch
        {
            "responses" => new ResponsesContentBlock("item", 0, JsonSerializer.SerializeToElement(new { type = "output_text", text = "visible" })),
            "refusal" => new ResponsesContentBlock("item", 0, JsonSerializer.SerializeToElement(new { type = "refusal", refusal = "visible" })),
            "google" => new GoogleContentBlock(JsonSerializer.SerializeToElement(new { text = "visible", thoughtSignature = "opaque" })),
            "anthropic" => new AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "text", text = "visible", citations = Array.Empty<object>() })),
            _ => new CitationsBlock(Array.Empty<Citation>(), new[] { new CitationText("visible") }),
        };
        var message = new Message(Role.Assistant, [block]);
        var tracer = new Tracer();
        var agent = tracer.StartAgentSpan("assistant");
        var model = tracer.StartModelInvokeSpan();
        model.RecordInput(new([message]));
        model.End(message);
        agent.End(message);

        var modelActivity = Assert.Single(stopped, item => item.OperationName == "chat");
        using var input = JsonDocument.Parse(Value(modelActivity, "gen_ai.input.messages"));
        Assert.Equal("visible", input.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString());
        foreach (var activity in stopped)
        {
            using var output = JsonDocument.Parse(Value(activity, "gen_ai.output.messages"));
            Assert.Equal("visible", output.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString());
        }
    }

    [Fact]
    public void GoogleThoughtTextDoesNotBecomeVisibleOutputContent()
    {
        using var environment = new TelemetryEnvironment("gen_ai_latest_experimental,gen_ai_span_attributes_only");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var message = new Message(Role.Assistant, [new GoogleContentBlock(JsonSerializer.SerializeToElement(new { text = "private-thought", thought = true, thoughtSignature = "opaque" }))]);
        var span = new Tracer().StartModelInvokeSpan();
        span.End(message);

        Assert.DoesNotContain("private-thought", Value(Assert.Single(stopped), "gen_ai.output.messages"));
    }

    [Theory]
    [InlineData(false, "text", false)]
    [InlineData(true, "text", true)]
    [InlineData(true, "json", true)]
    [InlineData(true, "empty", false)]
    public void PythonToolStatusPolicyUsesFirstTextWithoutFabricatingAnException(bool python, string content, bool expectedError)
    {
        using var environment = new TelemetryEnvironment("gen_ai_latest_experimental");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var span = new Tracer(python).StartToolCallSpan(new("call", "tool", JsonSerializer.SerializeToElement(new { })));
        var result = content == "json"
            ? new ToolResultBlock("call", new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(new { failed = true })) }, ToolResultStatus.Error)
            : new ToolResultBlock("call", content == "empty" ? "" : "tool failed", ToolResultStatus.Error);
        span.EndToolCall(result);

        var activity = Assert.Single(stopped);
        Assert.Equal(expectedError ? ActivityStatusCode.Error : ActivityStatusCode.Ok, activity.Status);
        if (expectedError) Assert.Equal(content == "json" ? "tool returned error status" : "tool failed", activity.StatusDescription);
        Assert.DoesNotContain(activity.Events, item => item.Name == "exception");
        Assert.Null(activity.GetTagItem("gen_ai.tool.call.result"));
    }

    private sealed record CyclicBlock : ContentBlock
    {
        public CyclicBlock Self => this;
    }

    private static string Tokens(bool latest, bool attributesOnly) => string.Join(",", new[]
    {
        latest ? "gen_ai_latest_experimental" : "",
        attributesOnly ? "gen_ai_span_attributes_only" : "",
    }.Where(value => value.Length > 0));

    private static string Value(Activity activity, string name) => (string)(activity.GetTagItem(name)
        ?? activity.Events.SelectMany(item => item.Tags).Single(pair => pair.Key == name).Value)!;

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TelemetryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
        internal TelemetryEnvironment(string tokens)
        {
            foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "LANGFUSE_BASE_URL" })
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, name == "OTEL_SEMCONV_STABILITY_OPT_IN" ? tokens : null);
            }
        }
        public void Dispose()
        {
            foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
