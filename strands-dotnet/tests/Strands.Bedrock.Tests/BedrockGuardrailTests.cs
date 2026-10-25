using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Native = Amazon.BedrockRuntime.Model;

namespace Strands.Bedrock.Tests;

public sealed class BedrockGuardrailTests
{
    [Fact]
    public async Task StreamedBlockedAssessmentRedactsHistoryWithDefaultInputPolicy()
    {
        using var body = Wire("end_turn", """{"inputAssessment":{"guardrail":{"contentPolicy":{"filters":[{"type":"HATE","confidence":"HIGH","action":"BLOCKED","detected":true}]}}}}""");
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = Guardrail() });
        await using var agent = new Agent(new() { Model = model });

        var result = await agent.InvokeAsync("private input");

        Assert.Equal("[User input redacted.]", agent.Messages[0].Text);
        Assert.Equal("provider output", result.ToString());
        Assert.Equal(new Usage(3, 2, ReportedTotalTokens: 5), result.Usage);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingTraceFallsBackToGuardrailStopWithOrWithoutMetadata(bool metadata)
    {
        using var body = Wire("guardrail_intervened", null, metadata);
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = Guardrail() with { RedactOutput = true } });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("input")]));

        var redaction = Assert.Single(events.OfType<ModelRedactionEvent>());
        Assert.Equal("[User input redacted.]", redaction.InputReplacement);
        Assert.Equal("[Assistant output redacted.]", redaction.OutputReplacement);
        Assert.Null(redaction.RedactedOutput);
    }

    [Fact]
    public async Task NativeConverseUsesTheSameBlockedTracePolicy()
    {
        var response = BedrockNonStreamingTests.Response([new Native.ContentBlock { Text = "provider output" }]);
        response.Trace = new ConverseTrace { Guardrail = Trace("BLOCKED", true) };
        using var client = new BedrockNonStreamingTests.Client { Response = response };
        using var model = new BedrockModel(client, new() { Streaming = false, Guardrail = Guardrail() with { RedactOutput = true, InputReplacement = "input removed", OutputReplacement = "output removed" } });
        await using var agent = new Agent(new() { Model = model });

        var result = await agent.InvokeAsync("private input");

        Assert.Equal("input removed", agent.Messages[0].Text);
        Assert.Equal("output removed", result.ToString());
        Assert.Single(client.ConverseRequests);
        Assert.Equal(0, client.StreamRequests);
    }

    [Fact]
    public async Task OutputOnlyPolicyCarriesProviderOriginalOutputInItsRedactionEvent()
    {
        using var body = Wire("guardrail_intervened", """{"modelOutput":["original ","output"],"outputAssessments":{"guardrail":[{"wordPolicy":{"customWords":[{"match":"x","action":"BLOCKED","detected":true}]}}]}}""");
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = Guardrail() with { RedactInput = false, RedactOutput = true } });

        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("input")]));

        var redaction = Assert.Single(events.OfType<ModelRedactionEvent>());
        Assert.Null(redaction.InputReplacement);
        Assert.Equal("original output", redaction.RedactedOutput);
    }

    [Fact]
    public void NullReplacementIsRejectedBeforePublishingConfiguration()
    {
        using var model = new BedrockModel(new() { Guardrail = Guardrail() });
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Guardrail = options.Guardrail! with { InputReplacement = null! } }));
        Assert.Equal("[User input redacted.]", model.GetConfig().Guardrail!.InputReplacement);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"inputAssessment\":{\"g\":{\"sensitiveInformationPolicy\":{\"piiEntities\":[{\"type\":\"EMAIL\",\"action\":\"ANONYMIZED\",\"detected\":true}]}}}}")]
    [InlineData("{\"inputAssessment\":{\"g\":{\"contentPolicy\":{\"filters\":[{\"type\":\"HATE\",\"action\":\"BLOCKED\",\"detected\":false}]}}}}")]
    [InlineData("{\"inputAssessment\":{\"g\":{\"contentPolicy\":{\"filters\":[{\"type\":\"HATE\",\"action\":\"BLOCKED\"}]}}}}")]
    public async Task PresentTraceIsAuthoritativeAndMaskingOrUndetectedPoliciesDoNotRedact(string trace)
    {
        using var body = Wire("guardrail_intervened", trace);
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = Guardrail() with { RedactOutput = true } });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("input")]));
        Assert.Empty(events.OfType<ModelRedactionEvent>());
    }

    [Theory]
    [InlineData("{\"topicPolicy\":{\"topics\":[{\"name\":\"topic\",\"type\":\"DENY\",\"action\":\"BLOCKED\",\"detected\":true}]}}")]
    [InlineData("{\"wordPolicy\":{\"managedWordLists\":[{\"match\":\"word\",\"type\":\"PROFANITY\",\"action\":\"BLOCKED\",\"detected\":true}]}}")]
    [InlineData("{\"sensitiveInformationPolicy\":{\"piiEntities\":[{\"type\":\"EMAIL\",\"action\":\"BLOCKED\",\"detected\":true}]}}")]
    [InlineData("{\"sensitiveInformationPolicy\":{\"regexes\":[{\"name\":\"regex\",\"action\":\"BLOCKED\",\"detected\":true}]}}")]
    [InlineData("{\"contextualGroundingPolicy\":{\"filters\":[{\"type\":\"GROUNDING\",\"action\":\"BLOCKED\",\"detected\":true}]}}")]
    public async Task NativePolicyUnionsContributeBlockedAssessments(string assessment)
    {
        using var body = Wire("end_turn", "{\"outputAssessments\":{\"g\":[" + assessment + "]}}");
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = Guardrail() });
        Assert.Single((await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("input")]))).OfType<ModelRedactionEvent>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuardrailConfigurationAndExplicitOptOutControlSdkRedaction(bool configured)
    {
        using var body = Wire("guardrail_intervened", null);
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new() { Guardrail = configured ? Guardrail() with { RedactInput = false } : null });
        Assert.Empty((await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("input")]))).OfType<ModelRedactionEvent>());
    }

    [Fact]
    public async Task ConverseTraceWithoutUsageStillRedactsAndPublishesAnEmptyReplacement()
    {
        var response = BedrockNonStreamingTests.Response([new Native.ContentBlock { Text = "blocked" }]);
        response.Usage = null;
        response.Trace = new ConverseTrace { Guardrail = Trace("BLOCKED", true) };
        using var client = new BedrockNonStreamingTests.Client { Response = response };
        using var model = new BedrockModel(client, new() { Streaming = false, Guardrail = Guardrail() with { RedactInput = false, RedactOutput = true, OutputReplacement = "" } });
        await using var agent = new Agent(new() { Model = model });
        Assert.Equal("", (await agent.InvokeAsync("input")).ToString());
        Assert.Equal("input", agent.Messages[0].Text);
    }

    [Fact]
    public async Task NativeConverseWithoutTraceOrUsageFallsBackToItsGuardrailStop()
    {
        var response = BedrockNonStreamingTests.Response([new Native.ContentBlock { Text = "blocked" }], "guardrail_intervened");
        response.Usage = null;
        using var client = new BedrockNonStreamingTests.Client { Response = response };
        using var model = new BedrockModel(client, new() { Streaming = false, Guardrail = Guardrail() });
        await using var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("input");
        Assert.Equal("[User input redacted.]", agent.Messages[0].Text);
    }

    [Fact]
    public void ReplacementDefaultsAndConfigurationCopiesRemainIndependent()
    {
        using var model = new BedrockModel(new() { Guardrail = Guardrail() });
        var original = model.GetConfig();
        Assert.True(original.Guardrail!.RedactInput);
        Assert.False(original.Guardrail.RedactOutput);
        model.UpdateConfig(options => options with { Guardrail = options.Guardrail! with { RedactInput = false, RedactOutput = true, OutputReplacement = "new" } });
        Assert.True(original.Guardrail.RedactInput);
        Assert.Equal("[Assistant output redacted.]", original.Guardrail.OutputReplacement);
        Assert.Equal("new", model.GetConfig().Guardrail!.OutputReplacement);
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Guardrail = options.Guardrail! with { OutputReplacement = null! } }));
        Assert.Equal("new", model.GetConfig().Guardrail!.OutputReplacement);
    }

    internal static BedrockGuardrailOptions Guardrail() => new() { Identifier = "guardrail-id", Version = "DRAFT" };

    internal static GuardrailTraceAssessment Trace(string action, bool? detected) => new()
    {
        InputAssessment = new Dictionary<string, GuardrailAssessment>
        {
            ["guardrail"] = new() { ContentPolicy = new() { Filters = [new() { Action = action, Detected = detected }] } }
        }
    };

    internal static BedrockModelTests.TrackingStream Wire(string stopReason, string? trace, bool metadata = true)
    {
        var frames = new List<(string, string)>
        {
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"provider output"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", "{\"stopReason\":\"" + stopReason + "\"}")
        };
        if (metadata) frames.Add(("metadata", "{\"usage\":{\"inputTokens\":3,\"outputTokens\":2,\"totalTokens\":5}" + (trace is null ? "" : ",\"trace\":{\"guardrail\":" + trace + "}") + "}"));
        return BedrockModelTests.Wire(frames.ToArray());
    }
}
