using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class AgentResultJsonTests
{
    [Fact]
    public async Task ActualInvocationProjectsWithoutTraversingTelemetryOrDelegateState()
    {
        var callback = (Action)(() => throw new InvalidOperationException("Do not invoke caller state."));
        var state = new Dictionary<string, object?> { ["callback"] = callback };
        await using var agent = new Agent(new() { Model = new TextModel() });
        var original = await agent.InvokeAsync("question", new() { InvocationState = state });
        Assert.NotNull(original.Metrics);
        Assert.NotEmpty(original.Traces);
        Assert.Same(state, original.InvocationState);

        var json = AgentResultJson.ToJson(original);
        Assert.Equal(new[] { "type", "schemaVersion", "stopReason", "message" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("strands.agent_result", json.GetProperty("type").GetString());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        var restored = AgentResultJson.FromJson(json);
        Assert.Equal(original.StopReason, restored.StopReason);
        Assert.Equal("answer", restored.Message!.Text);
        Assert.Equal(original.Message!.TrackingId, restored.Message.TrackingId);
        Assert.Same(callback, state["callback"]);
        Assert.Same(state, original.InvocationState);
        Assert.NotSame(original.Metrics, restored.Metrics);
        Assert.Empty(restored.InvocationState);
        Assert.Empty(restored.Traces);
        Assert.Empty(restored.Interrupts);
    }

    [Fact]
    public void TypeScriptProjectionDistinguishesAbsentAndEveryPresentJsonValue()
    {
        Assert.False(AgentResultJson.ToJson(Result()).TryGetProperty("structuredOutput", out _));
        foreach (var text in new[] { "null", "false", "0", "\"\"", "{\"value\":7}" })
        {
            using var document = JsonDocument.Parse(text);
            var original = Result() with { StructuredOutput = document.RootElement };
            var json = AgentResultJson.ToJson(original);
            Assert.True(JsonElement.DeepEquals(document.RootElement, json.GetProperty("structuredOutput")));
            Assert.True(JsonElement.DeepEquals(document.RootElement, AgentResultJson.FromJson(json).StructuredOutput!.Value));
        }
    }

    [Fact]
    public void PythonProjectionOmitsStructuredAndInterruptsAndWritesAbsentCheckpoint()
    {
        var result = Result() with { StructuredOutput = Parse("null"), Interrupts = [new("id", "approve", "call")] };
        var json = AgentResultJson.ToJson(result, AgentResultOutputProfile.Python);
        Assert.Equal(new[] { "type", "schemaVersion", "stopReason", "message", "checkpoint" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("checkpoint").ValueKind);
        Assert.False(json.TryGetProperty("structuredOutput", out _));
        Assert.False(json.TryGetProperty("interrupts", out _));
        var restored = AgentResultJson.FromJson(json);
        Assert.Null(restored.StructuredOutput);
        Assert.Empty(restored.Interrupts);
        Assert.Single(result.Interrupts);
    }

    [Fact]
    public void RichNativeMessagePreservesOpaqueDuplicatesAndDetachedValues()
    {
        AgentResult original;
        using (var document = JsonDocument.Parse("{\"value\":1,\"value\":2,\"nested\":{\"x\":null}}"))
        {
            var opaque = document.RootElement;
            original = Result(new(Role.User, [
                new ToolResultBlock("call", "display")
                {
                    HasExplicitContent = true,
                    ContentBlocks = [new JsonBlock(opaque), new RedactedReasoningBlock("AAEC"), new ImageBlock("image/png", "AAEC")],
                    StructuredContent = Parse("null"), Metadata = opaque
                },
                new ReasoningBlock("private\nreason", "signature"),
                new ToolUseBlock("later", "lookup", opaque) { ProviderMetadata = opaque },
                new CitationsBlock([new(new WebCitationLocation("https://example.invalid/evidence"))], [new("cited")], opaque),
                new GoogleContentBlock(Parse("{\"text\":\"provider\",\"extra\":1,\"extra\":2}"))
            ], "stable-id", opaque));
        }

        var projected = AgentResultJson.ToJson(original);
        var restored = AgentResultJson.FromJson(projected);
        var expected = JsonSerializer.SerializeToElement(original.Message);
        var actual = JsonSerializer.SerializeToElement(restored.Message);
        Assert.True(JsonElement.DeepEquals(expected, actual));
        Assert.Equal("stable-id", restored.Message!.TrackingId);
        Assert.NotSame(original.Message, restored.Message);
        var result = Assert.IsType<ToolResultBlock>(restored.Message.Content[0]);
        Assert.True(result.HasExplicitContent);
        Assert.Equal(2, result.Metadata!.Value.EnumerateObject().Count(p => p.Name == "value"));
        Assert.Equal(2, Assert.IsType<JsonBlock>(result.ContentBlocks[0]).Json.EnumerateObject().Count(p => p.Name == "value"));
        Assert.Equal("AAEC", Assert.IsType<RedactedReasoningBlock>(result.ContentBlocks[1]).Data);
        Assert.Equal("AAEC", Assert.IsType<ImageBlock>(result.ContentBlocks[2]).Data);
    }

    [Fact]
    public void RehydrationCreatesFreshEmptyMetricsAndStateEachTime()
    {
        var json = AgentResultJson.ToJson(Result() with { StructuredOutput = Parse("{\"ok\":true}") });
        var first = AgentResultJson.FromJson(json);
        var second = AgentResultJson.FromJson(json);
        Assert.NotSame(first.InvocationState, second.InvocationState);
        Assert.NotSame(first.Metrics, second.Metrics);
        Assert.NotNull(first.Metrics);
        Assert.Equal(0, first.Metrics.CycleCount);
        Assert.Empty(first.Metrics.AgentInvocations);
        Assert.Empty(first.Metrics.ToolMetrics);
        Assert.Null(first.ContextSize);
        Assert.Null(first.ProjectedContextSize);
        Assert.Equal(new Usage(), first.Usage);
        Assert.Equal(0, first.Turns);
        Assert.Empty(first.InvocationState);
        Assert.NotSame(first.Message, second.Message);
    }

    [Fact]
    public void NativeEnvelopeRejectsReservedDuplicatesVersionAndMalformedFields()
    {
        foreach (var text in new[]
        {
            "{\"type\":\"wrong\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null}",
            "{\"type\":\"strands.agent_result\",\"schemaVersion\":2,\"stopReason\":\"custom\",\"message\":null}",
            "{\"type\":\"strands.agent_result\",\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null}",
            "{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\"}",
            "{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"unknown\"}]}}"
        }) Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse(text)));

        var allowed = Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null,\"future\":1,\"future\":2}");
        Assert.Equal("custom", AgentResultJson.FromJson(allowed).StopReason);
    }

    [Fact]
    public void TypedCheckpointCanBeSafelyProjectedAndRestored()
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterModel, 3);
        var original = Result() with { StopReason = StopReasons.Checkpoint, Checkpoint = marker };
        var restored = AgentResultJson.FromJson(AgentResultJson.ToJson(original));
        Assert.Equal(marker, restored.Checkpoint);
        Assert.Equal(StopReasons.Checkpoint, restored.StopReason);
        Assert.Empty(restored.InvocationState);
    }

    [Fact]
    public void LegacyConstructorDefaultJsonAndToStringRemainUnchanged()
    {
        var message = Message.Assistant("legacy");
        var state = new Dictionary<string, object?>();
        var result = new AgentResult(StopReasons.EndTurn, message, new(), 1, state);
        var (reason, last, usage, turns, caller) = result;
        Assert.Equal(StopReasons.EndTurn, reason);
        Assert.Same(message, last);
        Assert.Equal(new Usage(), usage);
        Assert.Equal(1, turns);
        Assert.Same(state, caller);
        Assert.Equal(result, new AgentResult(reason, last, usage, turns, caller));
        Assert.Equal("legacy", result.ToString());
        var json = JsonSerializer.SerializeToElement(result);
        Assert.Equal(new[] { "StopReason", "Message", "Usage", "Turns", "InvocationState", "Metrics", "Traces", "Interrupts" }, json.EnumerateObject().Select(p => p.Name));
        Assert.False(json.TryGetProperty("StructuredOutput", out _));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("toolUse")]
    [InlineData("toolResult")]
    [InlineData("reasoning")]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("video")]
    [InlineData("guardContent")]
    [InlineData("json")]
    [InlineData("resourceLink")]
    [InlineData("redactedReasoning")]
    [InlineData("cachePoint")]
    [InlineData("document")]
    [InlineData("openaiResponsesContent")]
    [InlineData("openaiResponsesReasoning")]
    [InlineData("openaiResponsesOutputItem")]
    [InlineData("googleContent")]
    [InlineData("anthropicContent")]
    [InlineData("citations")]
    public void EveryNativeContentArmRoundTripsWithoutFlattening(string kind)
    {
        var block = NativeArm(kind);
        var copy = RoundTripMessage(new(Role.Assistant, [block], "arm-" + kind));
        var actual = Assert.Single(copy.Content);
        Assert.Equal(block.GetType(), actual.GetType());
        Assert.Equal(kind, JsonSerializer.SerializeToElement<ContentBlock>(actual).GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement<ContentBlock>(block), JsonSerializer.SerializeToElement<ContentBlock>(actual)));
    }

    [Fact]
    public void JsonConformanceJ01NullMessageAndCustomReasonsRemainNative()
    {
        foreach (var reason in new[] { "custom-reason", "", "Checkpoint" })
            foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
            {
                var result = new AgentResult(reason, null, new(), 0, new Dictionary<string, object?>());
                var json = AgentResultJson.ToJson(result, profile);
                Assert.Equal(JsonValueKind.Null, json.GetProperty("message").ValueKind);
                var restored = AgentResultJson.FromJson(json);
                Assert.Null(restored.Message);
                Assert.Equal(reason, restored.StopReason);
            }
    }

    [Fact]
    public void JsonConformanceJ02NullArgumentsAndUnknownProfilesFailBeforeProjection()
    {
        Assert.Throws<ArgumentNullException>(() => AgentResultJson.ToJson(null!));
        Assert.Throws<ArgumentNullException>(() => AgentResultRendering.Render(null!));
        var result = Result();
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentResultJson.ToJson(result, (AgentResultOutputProfile)123));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentResultRendering.Render(result, (AgentResultOutputProfile)123));
    }

    [Fact]
    public void JsonConformanceJ03UndefinedAndDisposedEnvelopesNeverProduceResults()
    {
        Assert.Throws<JsonException>(() => AgentResultJson.FromJson(default));
        var document = JsonDocument.Parse("{}");
        var disposed = document.RootElement;
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => AgentResultJson.FromJson(disposed));
    }

    [Fact]
    public void JsonConformanceJ04StructuredEntryDetachesBeforeProducerDisposal()
    {
        AgentResult result;
        using (var document = JsonDocument.Parse("{\"nested\":[1,null]}")) result = Result() with { StructuredOutput = document.RootElement };
        Assert.Equal(2, AgentResultJson.ToJson(result).GetProperty("structuredOutput").GetProperty("nested").GetArrayLength());
        var error = Record.Exception(() => AgentResultJson.ToJson(Result() with { StructuredOutput = default(JsonElement) }));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void JsonConformanceJ05EveryReservedEnvelopeDuplicateIsRejected()
    {
        foreach (var pair in new[]
        {
            "\"type\":\"strands.agent_result\"", "\"schemaVersion\":1", "\"stopReason\":\"custom\"",
            "\"message\":null", "\"structuredOutput\":null", "\"checkpoint\":null"
        })
        {
            var text = "{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null,";
            text += pair + (pair.StartsWith("\"structuredOutput\"", StringComparison.Ordinal) || pair.StartsWith("\"checkpoint\"", StringComparison.Ordinal) ? "," + pair : "") + "}";
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse(text)));
        }
    }

    [Fact]
    public void JsonConformanceJ06OpaqueAndUnknownDuplicatesAreNotRecursivelyRejected()
    {
        var duplicate = Parse("{\"key\":1,\"key\":2}");
        var original = Result(new(Role.Assistant, [new JsonBlock(duplicate), new ToolUseBlock("call", "tool", duplicate) { ProviderMetadata = duplicate }], "dup", duplicate))
            with
        { StructuredOutput = duplicate };
        var json = AgentResultJson.ToJson(original);
        var text = json.GetRawText();
        text = text.Substring(0, text.Length - 1) + ",\"future\":{\"x\":1,\"x\":2},\"future\":null}";
        var restored = AgentResultJson.FromJson(Parse(text));
        Assert.Equal(2, restored.StructuredOutput!.Value.EnumerateObject().Count());
        Assert.Equal(2, restored.Message!.Metadata!.Value.EnumerateObject().Count());
        Assert.Equal(2, Assert.IsType<JsonBlock>(restored.Message.Content[0]).Json.EnumerateObject().Count());
        Assert.Equal(2, Assert.IsType<ToolUseBlock>(restored.Message.Content[1]).ProviderMetadata!.Value.EnumerateObject().Count());
    }

    [Fact]
    public void JsonConformanceJ07EnvelopeKnownTypesAndVersionsFailClosed()
    {
        foreach (var text in new[] { "null", "[]", "\"text\"", "{}" }) Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse(text)));
        foreach (var version in new[] { "0", "1.5", "\"1\"", "true", "null", "2147483648" })
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":" + version + ",\"stopReason\":\"custom\",\"message\":null}")));
        foreach (var reason in new[] { "null", "1", "{}" })
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":" + reason + ",\"message\":null}")));
        Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":[]}")));
    }

    [Fact]
    public void JsonConformanceJ08OnlyExistingMessageChecksApply()
    {
        foreach (var message in new[]
        {
            "{\"role\":\"system\",\"content\":[]}", "{\"role\":\"assistant\",\"content\":null}",
            "{\"role\":\"assistant\",\"content\":[null]}"
        }) Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":" + message + "}")));
        var loneResult = new Message(Role.User, [new ToolResultBlock("outside-call", "result")]);
        Assert.IsType<ToolResultBlock>(Assert.Single(RoundTripMessage(loneResult).Content));
    }

    [Fact]
    public void JsonConformanceJ09UnknownTelemetryFieldsCannotRestoreLocalExecution()
    {
        var json = Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null,\"metrics\":{\"cycleCount\":99},\"traces\":[1],\"invocationState\":{\"secret\":true},\"interrupts\":[1],\"usage\":{\"inputTokens\":99},\"turns\":99}");
        var result = AgentResultJson.FromJson(json);
        Assert.Equal(0, result.Metrics!.CycleCount);
        Assert.Equal(new Usage(), result.Usage);
        Assert.Equal(0, result.Turns);
        Assert.Empty(result.InvocationState);
        Assert.Empty(result.Traces);
        Assert.Empty(result.Interrupts);
    }

    [Fact]
    public void JsonConformanceJ10FreshMetricsExposeEveryEmptyNativeMeasure()
    {
        var result = AgentResultJson.FromJson(AgentResultJson.ToJson(Result()));
        var metrics = result.Metrics!;
        Assert.Equal(new Usage(), metrics.AccumulatedUsage);
        Assert.Equal(TimeSpan.Zero, metrics.AccumulatedMetrics.Latency);
        Assert.Null(metrics.AccumulatedMetrics.TimeToFirstToken);
        Assert.Equal(TimeSpan.Zero, metrics.TotalDuration);
        Assert.Equal(TimeSpan.Zero, metrics.AverageCycleTime);
        Assert.Null(metrics.LatestAgentInvocation);
        Assert.Empty(metrics.ToolMetrics);
        Assert.Empty(metrics.AgentInvocations);
        var state = Assert.IsAssignableFrom<IDictionary<string, object?>>(result.InvocationState);
        Assert.True(state.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => state.Add("key", true));
    }

    [Fact]
    public void JsonConformanceJ11NativeNamespaceDoesNotClaimForeignInterchange()
    {
        foreach (var type in new[] { "agentResult", "agent_result" })
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"" + type + "\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null}")));
        foreach (var block in new[] { "{\"text\":\"foreign-union\"}", "{\"reasoningContent\":{\"redactedContent\":{\"__bytes_encoded__\":true,\"data\":\"AAEC\"}}}" })
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":{\"role\":\"assistant\",\"content\":[" + block + "]}}")));
        var common = AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":{\"role\":\"assistant\",\"content\":[]}}"));
        Assert.Empty(common.Message!.Content);
    }

    [Fact]
    public void JsonConformanceJ12CheckpointUsesTheStrictNativeMarker()
    {
        foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
        {
            var original = Result() with { StopReason = StopReasons.Checkpoint, Checkpoint = new(CheckpointPosition.AfterTools, 4) };
            Assert.Equal(original.Checkpoint, AgentResultJson.FromJson(AgentResultJson.ToJson(original, profile)).Checkpoint);
        }
        foreach (var marker in new[] { "{}", "[]", "false", "1", "\"opaque\"" })
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null,\"checkpoint\":" + marker + "}")));
        Assert.Null(AgentResultJson.FromJson(Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"checkpoint\",\"message\":null,\"checkpoint\":null}")).Checkpoint);
    }

    [Fact]
    public void JsonConformanceJ13AllNativeMediaSourceVariantsRetainTheirAuthority()
    {
        var location = new S3Location("s3://example-bucket/opaque/../key", "123456789012");
        var blocks = new ContentBlock[]
        {
            new ImageBlock("image/png", "AAEC"), new ImageBlock("image/png", location), new ImageBlock("image/png", new Uri("https://example.invalid/a%2Fb?q=1")),
            new AudioBlock("audio/wav", "AAEC"), new AudioBlock("audio/wav", location), new VideoBlock("video/mp4", "AAEC"), new VideoBlock("video/mp4", location),
            new DocumentBlock("application/pdf", "AAEC", "bytes"), new DocumentBlock("application/pdf", location, "s3"),
            DocumentBlock.FromText("α\ntext", "text"), DocumentBlock.FromContent([new("one"), new("two")], "segments") with { CitationsEnabled = false, Context = "context" }
        };
        var message = new Message(Role.Assistant, blocks);
        var copy = RoundTripMessage(message);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(message), JsonSerializer.SerializeToElement(copy)));
        Assert.Equal(location.Uri, Assert.IsType<ImageBlock>(copy.Content[1]).Location!.Uri);
        Assert.Equal("one", Assert.IsType<DocumentBlock>(copy.Content[10]).Content![0].Text);
    }

    [Fact]
    public void JsonConformanceJ14GuardAndEveryCitationLocationRetainFullFields()
    {
        var locations = new CitationLocation[] { new DocumentCharCitationLocation(1, 2, 3), new DocumentPageCitationLocation(2, 3, 4), new DocumentChunkCitationLocation(3, 4, 5), new SearchResultCitationLocation(4, 5, 6), new WebCitationLocation("https://example.invalid", "example.invalid") };
        var citations = locations.Select(location => new Citation(location, "source", [new("evidence")], "title")).ToArray();
        var message = new Message(Role.Assistant, [new GuardContentBlock("guard", qualifiers: [GuardQualifier.Query, GuardQualifier.GroundingSource]), new GuardContentBlock(new ImageBlock("image/png", "AAEC")), new CitationsBlock(citations, [new("answer")], Parse("{\"opaque\":true}"))]);
        var copy = RoundTripMessage(message);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(message), JsonSerializer.SerializeToElement(copy)));
        Assert.Equal(5, Assert.IsType<CitationsBlock>(copy.Content[2]).Citations.Count);
        Assert.Equal(new[] { GuardQualifier.Query, GuardQualifier.GroundingSource }, Assert.IsType<GuardContentBlock>(copy.Content[0]).Qualifiers);
    }

    [Fact]
    public void JsonConformanceJ15RichResultsKeepWireDataButExcludeLocalErrors()
    {
        var sentinel = new IOException("local-only-error");
        var blocks = new ContentBlock[]
        {
            new ToolResultBlock("legacy", "fallback"),
            new ToolResultBlock("empty", "display") { HasExplicitContent = true },
            new ToolResultBlock("rich", "display", ToolResultStatus.Error) { HasExplicitContent = true, ContentBlocks = [new TextBlock("first"), new JsonBlock(Parse("null"))], StructuredContent = Parse("null"), Metadata = Parse("{\"nested\":{\"k\":1,\"k\":2}}"), Error = sentinel, IteratorCleanupFailed = true }
        };
        var result = Result(new(Role.User, blocks));
        var json = AgentResultJson.ToJson(result);
        Assert.DoesNotContain(sentinel.Message, json.GetRawText());
        Assert.DoesNotContain("iteratorCleanupFailed", json.GetRawText());
        var restored = AgentResultJson.FromJson(json);
        var rich = Assert.IsType<ToolResultBlock>(restored.Message!.Content[2]);
        Assert.Null(rich.Error);
        Assert.False(rich.IteratorCleanupFailed);
        Assert.Equal(ToolResultStatus.Error, rich.Status);
        Assert.True(rich.HasExplicitContent);
        Assert.Equal(JsonValueKind.Null, rich.StructuredContent!.Value.ValueKind);
        Assert.Same(sentinel, Assert.IsType<ToolResultBlock>(result.Message!.Content[2]).Error);
    }

    [Fact]
    public void JsonConformanceJ16ProviderNativeReplayRemainsWhole()
    {
        var blocks = new[] { NativeArm("openaiResponsesContent"), NativeArm("openaiResponsesReasoning"), NativeArm("openaiResponsesOutputItem"), NativeArm("googleContent"), NativeArm("anthropicContent") };
        var message = new Message(Role.Assistant, blocks);
        var copy = RoundTripMessage(message);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(message), JsonSerializer.SerializeToElement(copy)));
        Assert.Equal(2, Assert.IsType<GoogleContentBlock>(copy.Content[3]).Part.EnumerateObject().Count(p => p.Name == "opaque"));
        Assert.Equal("encrypted", Assert.IsType<ResponsesReasoningBlock>(copy.Content[1]).Item.GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public void JsonConformanceJ17UnsupportedExternalBlockIsNeverSilentlyFlattened()
    {
        var result = Result(new(Role.Assistant, [new UnsupportedBlock()]));
        Assert.Throws<NotSupportedException>(() => AgentResultJson.ToJson(result));
    }

    [Fact]
    public void JsonConformanceJ18ProjectionAndHydrationDoNotRetainProducerCollections()
    {
        var blocks = new List<ContentBlock> { new TextBlock("original") };
        var message = new Message(Role.Assistant, blocks, "fixed");
        var result = Result(message);
        var projected = AgentResultJson.ToJson(result);
        blocks.Clear();
        var hydrated = AgentResultJson.FromJson(projected);
        Assert.Equal("original", hydrated.Message!.Text);
        Assert.Same(message, result.Message);
        Assert.NotSame(message.Content, hydrated.Message.Content);
        Assert.Equal("fixed", hydrated.Message.TrackingId);
    }

    [Fact]
    public void NullablePresenceControlsSeparateMissingNullAndObjectAcrossNestedResults()
    {
        var absent = new ToolResultBlock("absent", "display");
        var presentNull = new ToolResultBlock("null", "display") { StructuredContent = Parse("null") };
        var presentObject = new ToolResultBlock("object", "display") { StructuredContent = Parse("{\"value\":null}") };
        var nested = new ToolResultBlock("nested", "display") { HasExplicitContent = true, ContentBlocks = [absent, presentNull, presentObject] };
        var result = Result(new(Role.User, [nested]));
        var json = AgentResultJson.ToJson(result);
        var values = json.GetProperty("message").GetProperty("content")[0].GetProperty("contentBlocks");
        Assert.False(values[0].TryGetProperty("structuredContent", out _));
        Assert.Equal(JsonValueKind.Null, values[1].GetProperty("structuredContent").ValueKind);
        Assert.Equal(JsonValueKind.Null, values[2].GetProperty("structuredContent").GetProperty("value").ValueKind);
        var copy = Assert.IsType<ToolResultBlock>(AgentResultJson.FromJson(json).Message!.Content[0]);
        Assert.Null(Assert.IsType<ToolResultBlock>(copy.ContentBlocks[0]).StructuredContent);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<ToolResultBlock>(copy.ContentBlocks[1]).StructuredContent!.Value.ValueKind);
        Assert.Equal(JsonValueKind.Object, Assert.IsType<ToolResultBlock>(copy.ContentBlocks[2]).StructuredContent!.Value.ValueKind);
    }

    [Fact]
    public void NullablePresenceControlsPreserveOptionalToolReplayJsonNull()
    {
        var message = new Message(Role.Assistant, [
            new ToolUseBlock("absent", "tool", Parse("{}")),
            new ToolUseBlock("null", "tool", Parse("{}")) { ProviderMetadata = Parse("null") },
            new ToolUseBlock("object", "tool", Parse("{}")) { ProviderMetadata = Parse("{\"k\":null,\"k\":2}") }
        ]);
        var json = AgentResultJson.ToJson(Result(message));
        var values = json.GetProperty("message").GetProperty("content");
        Assert.False(values[0].TryGetProperty("providerMetadata", out _));
        Assert.Equal(JsonValueKind.Null, values[1].GetProperty("providerMetadata").ValueKind);
        var copy = AgentResultJson.FromJson(json).Message!;
        Assert.Null(Assert.IsType<ToolUseBlock>(copy.Content[0]).ProviderMetadata);
        Assert.Equal(JsonValueKind.Null, Assert.IsType<ToolUseBlock>(copy.Content[1]).ProviderMetadata!.Value.ValueKind);
        Assert.Equal(2, Assert.IsType<ToolUseBlock>(copy.Content[2]).ProviderMetadata!.Value.EnumerateObject().Count());
    }

    [Fact]
    public void NullablePresenceControlsOmitAbsentConstructorJsonButKeepLegacyDefaultShape()
    {
        var message = new Message(Role.Assistant, [new CitationsBlock([], [new("answer")]), new ResponsesContentBlock("item", 0, Parse("{\"type\":\"output_text\",\"text\":\"text\"}"))]);
        var json = AgentResultJson.ToJson(Result(message)).GetProperty("message");
        Assert.False(json.TryGetProperty("metadata", out _));
        Assert.False(json.GetProperty("content")[0].TryGetProperty("providerMetadata", out _));
        Assert.False(json.GetProperty("content")[1].TryGetProperty("item", out _));
        Assert.Null(RoundTripMessage(message).Metadata);
        var legacy = JsonSerializer.SerializeToElement(message);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("Metadata").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("Content")[0].GetProperty("ProviderMetadata").ValueKind);
    }

    [Fact]
    public void NullablePresenceControlsExternalLegacyNullMetadataHitsNativeObjectValidation()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var message = Message.Assistant("legacy");
        var external = JsonSerializer.SerializeToElement(new { type = "strands.agent_result", schemaVersion = 1, stopReason = "custom", message }, web);
        Assert.Equal(JsonValueKind.Null, external.GetProperty("message").GetProperty("metadata").ValueKind);
        var error = Assert.Throws<JsonException>(() => AgentResultJson.FromJson(external));
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
        Assert.Equal("legacy", AgentResultJson.FromJson(AgentResultJson.ToJson(Result(message))).Message!.Text);
    }

    [Fact]
    public void NullablePresenceControlsOpaqueNullsAndDuplicatesStayWholeUnderBothProfiles()
    {
        var opaque = Parse("{\"metadata\":null,\"key\":null,\"key\":2,\"array\":[null,{\"x\":null}]}");
        var block = new ToolResultBlock("call", "display") { StructuredContent = Parse("null"), Metadata = opaque, ContentBlocks = [new JsonBlock(opaque)] };
        var original = Result(new(Role.User, [block])) with { StructuredOutput = Parse("null") };
        foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
        {
            var copy = Assert.IsType<ToolResultBlock>(AgentResultJson.FromJson(AgentResultJson.ToJson(original, profile)).Message!.Content[0]);
            Assert.Equal(JsonValueKind.Null, copy.StructuredContent!.Value.ValueKind);
            Assert.Equal(JsonValueKind.Null, copy.Metadata!.Value.GetProperty("metadata").ValueKind);
            Assert.Equal(2, copy.Metadata.Value.EnumerateObject().Count(p => p.Name == "key"));
            Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonBlock>(copy.ContentBlocks[0]).Json.GetProperty("array")[0].ValueKind);
        }
    }

    [Fact]
    public void NullablePresenceControlsKeepExistingWebMessagePropertyMatching()
    {
        var json = Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":{\"ROLE\":\"user\",\"CONTENT\":[{\"type\":\"toolResult\",\"TOOLUSEID\":\"call\",\"CONTENT\":\"display\",\"CONTENTBLOCKS\":[],\"STRUCTUREDCONTENT\":null}]}}");
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(AgentResultJson.FromJson(json).Message!.Content));
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal(JsonValueKind.Null, result.StructuredContent!.Value.ValueKind);
    }

    internal static ContentBlock NativeArm(string kind) => kind switch
    {
        "text" => new TextBlock("text\nα"),
        "toolUse" => new ToolUseBlock("call", "lookup", Parse("{\"value\":1}")) { ProviderMetadata = Parse("{\"provider\":{\"token\":\"opaque\"}}") },
        "toolResult" => new ToolResultBlock("call", "display", ToolResultStatus.Error) { HasExplicitContent = true, ContentBlocks = [new TextBlock("rich")], Metadata = Parse("{\"opaque\":true}"), StructuredContent = Parse("null") },
        "reasoning" => new ReasoningBlock("reason", "signature"),
        "image" => new ImageBlock("image/png", "AAEC"),
        "audio" => new AudioBlock("audio/wav", "AAEC"),
        "video" => new VideoBlock("video/mp4", "AAEC"),
        "guardContent" => new GuardContentBlock("guard", qualifiers: [GuardQualifier.GuardContent]),
        "json" => new JsonBlock(Parse("{\"value\":null,\"value\":2}")),
        "resourceLink" => new ResourceLinkBlock("urn:opaque:resource", "resource", "text/plain", "description"),
        "redactedReasoning" => new RedactedReasoningBlock("AAEC"),
        "cachePoint" => new CachePointBlock("5m"),
        "document" => DocumentBlock.FromContent([new("segment1"), new("segment2")], "document") with { Context = "context", CitationsEnabled = true },
        "openaiResponsesContent" => new ResponsesContentBlock("item", 0, Parse("{\"type\":\"output_text\",\"text\":\"opaque-response\",\"annotations\":[],\"extra\":true}"), "incomplete", Parse("{\"type\":\"message\",\"id\":\"item\",\"role\":\"assistant\",\"status\":\"incomplete\",\"content\":[{\"type\":\"output_text\",\"text\":\"opaque-response\",\"annotations\":[],\"extra\":true}],\"opaque\":1}")),
        "openaiResponsesReasoning" => new ResponsesReasoningBlock(Parse("{\"type\":\"reasoning\",\"id\":\"reason\",\"encrypted_content\":\"encrypted\",\"summary\":[{\"text\":\"summary\"}]}")),
        "openaiResponsesOutputItem" => new ResponsesOutputItemBlock(Parse("{\"type\":\"web_search_call\",\"id\":\"server\",\"status\":\"completed\",\"opaque\":null}")),
        "googleContent" => new GoogleContentBlock(Parse("{\"text\":\"opaque-google\",\"thoughtSignature\":\"signature\",\"opaque\":1,\"opaque\":2}")),
        "anthropicContent" => new AnthropicContentBlock(Parse("{\"type\":\"text\",\"text\":\"opaque-anthropic\",\"citations\":[{\"opaque\":true}]}")),
        "citations" => new CitationsBlock([new(new WebCitationLocation("https://example.invalid", "example.invalid"), "source", [new("excerpt")], "title")], [new("answer1"), new("answer2")], Parse("{\"opaque\":true}")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Message RoundTripMessage(Message message)
        => AgentResultJson.FromJson(AgentResultJson.ToJson(Result(message))).Message!;

    private sealed record UnsupportedBlock : ContentBlock;

    internal static AgentResult Result(Message? message = null) => new(StopReasons.EndTurn, message ?? Message.Assistant("plain"), new(), 1, new Dictionary<string, object?>());
    internal static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMetadataEvent(new Usage(4, 2));
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
