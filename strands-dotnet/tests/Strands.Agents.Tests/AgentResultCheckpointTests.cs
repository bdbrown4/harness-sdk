using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentResultCheckpointTests
{
    [Theory]
    [InlineData(CheckpointPosition.AfterModel, 0)]
    [InlineData(CheckpointPosition.AfterTools, 3)]
    public void TypeScriptProjectsBothTypedBoundaries(CheckpointPosition position, int cycle)
    {
        var marker = new AgentCheckpoint(position, cycle);
        var result = Result(StopReasons.Checkpoint) with { Checkpoint = marker };
        var json = AgentResultJson.ToJson(result);
        var projected = json.GetProperty("checkpoint");
        Assert.Equal("1.0", projected.GetProperty("schemaVersion").GetString());
        Assert.Equal(position == CheckpointPosition.AfterModel ? "afterModel" : "afterTools", projected.GetProperty("position").GetString());
        Assert.Equal(cycle, projected.GetProperty("cycleIndex").GetInt32());
        Assert.Equal(marker, AgentResultJson.FromJson(json).Checkpoint);
        Assert.Same(marker, result.Checkpoint);
    }

    [Fact]
    public void PythonProjectsTheMarkerWithoutLocalOrStructuredState()
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterTools, 5);
        var result = Result("custom") with { Checkpoint = marker, StructuredOutput = Parse("{\"answer\":1}") };
        var json = AgentResultJson.ToJson(result, AgentResultOutputProfile.Python);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("checkpoint").ValueKind);
        Assert.Equal(marker, AgentResultJson.FromJson(json).Checkpoint);
        Assert.False(json.TryGetProperty("structuredOutput", out _));
        Assert.False(json.TryGetProperty("metrics", out _));
        Assert.False(json.TryGetProperty("invocationState", out _));
    }

    [Fact]
    public void ReadingACheckpointUsesTypedDataAndFreshLocalState()
    {
        var json = Envelope(StopReasons.Checkpoint, "{\"position\":\"afterModel\",\"cycleIndex\":7,\"schemaVersion\":\"1.0\"}");
        var result = AgentResultJson.FromJson(json);
        Assert.Equal(new(CheckpointPosition.AfterModel, 7), result.Checkpoint);
        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.NotNull(result.Metrics);
        Assert.Empty(result.Traces);
        Assert.Empty(result.InvocationState);
        Assert.Empty(result.Interrupts);
        Assert.Equal(0, result.Turns);
    }

    [Fact]
    public void RepresentationDoesNotRequireMarkerAndReasonAgreement()
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterTools, 2);
        var json = Envelope("ordinary-custom", JsonSerializer.Serialize(marker));
        var result = AgentResultJson.FromJson(json);
        Assert.Equal("ordinary-custom", result.StopReason);
        Assert.Equal(marker, result.Checkpoint);
        Assert.Equal(marker, AgentResultJson.FromJson(AgentResultJson.ToJson(result)).Checkpoint);
    }

    [Fact]
    public void ACheckpointReasonWithoutAMarkerRemainsRepresentableData()
    {
        var result = Result(StopReasons.Checkpoint);
        var ts = AgentResultJson.ToJson(result);
        Assert.False(ts.TryGetProperty("checkpoint", out _));
        Assert.Null(AgentResultJson.FromJson(ts).Checkpoint);
        var py = AgentResultJson.ToJson(result, AgentResultOutputProfile.Python);
        Assert.Equal(JsonValueKind.Null, py.GetProperty("checkpoint").ValueKind);
        Assert.Equal(StopReasons.Checkpoint, AgentResultJson.FromJson(py).StopReason);
    }

    [Fact]
    public void AbsentMarkerPreservesTheOldConstructorDeconstructionAndDefaultJson()
    {
        var message = Message.Assistant("legacy");
        var state = new Dictionary<string, object?>();
        var result = new AgentResult(StopReasons.EndTurn, message, new(), 1, state);
        var (reason, last, usage, turns, caller) = result;
        Assert.Equal(5, Assert.Single(typeof(AgentResult).GetConstructors()).GetParameters().Length);
        Assert.Equal(result, new AgentResult(reason, last, usage, turns, caller));
        Assert.Same(message, last);
        Assert.Same(state, caller);
        Assert.Null(result.Checkpoint);
        Assert.Equal("legacy", result.ToString());
        Assert.Equal(new[] { "StopReason", "Message", "Usage", "Turns", "InvocationState", "Metrics", "Traces", "Interrupts" }, JsonSerializer.SerializeToElement(result).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TheActualMarkerConverterRetainsItsStrictNativeSchema()
    {
        foreach (var text in new[] { "{}", "false", "{\"position\":\"afterModel\",\"schemaVersion\":\"0.9\"}", "{\"position\":\"afterModel\",\"schemaVersion\":\"1.0\",\"cycleIndex\":1.5}", "{\"position\":\"invalid\",\"schemaVersion\":\"1.0\"}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentCheckpoint>(text));
    }

    [Fact]
    public void OrdinaryOpaqueDataAndPresentNullRemainUnchanged()
    {
        var opaque = Parse("{\"x\":null,\"dup\":1,\"dup\":2}");
        var result = Result(StopReasons.EndTurn) with { Message = new(Role.Assistant, [new JsonBlock(opaque), new ToolUseBlock("call", "tool", opaque) { ProviderMetadata = Parse("null") }]) };
        var restored = AgentResultJson.FromJson(AgentResultJson.ToJson(result));
        Assert.Equal(2, Assert.IsType<JsonBlock>(restored.Message!.Content[0]).Json.EnumerateObject().Count(p => p.Name == "dup"));
        Assert.Equal(JsonValueKind.Null, Assert.IsType<ToolUseBlock>(restored.Message.Content[1]).ProviderMetadata!.Value.ValueKind);
        Assert.Null(restored.Checkpoint);
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("end_turn")]
    [InlineData("custom")]
    [InlineData("")]
    [InlineData("Checkpoint")]
    public void MixedStopReasonsRemainIndependentRepresentationData(string reason)
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterTools, 11);
        foreach (var profile in new[] { AgentResultOutputProfile.TypeScript, AgentResultOutputProfile.Python })
        {
            var original = Result(reason) with { Checkpoint = marker };
            var restored = AgentResultJson.FromJson(AgentResultJson.ToJson(original, profile));
            Assert.Equal(reason, restored.StopReason);
            Assert.Equal(marker, restored.Checkpoint);
        }
    }

    [Theory]
    [InlineData(AgentResultOutputProfile.TypeScript, false)]
    [InlineData(AgentResultOutputProfile.Python, true)]
    public void NullAndMissingMarkersFollowOnlyTheSelectedProjectionProfile(AgentResultOutputProfile profile, bool present)
    {
        var projected = AgentResultJson.ToJson(Result("checkpoint") with { Checkpoint = null }, profile);
        Assert.Equal(present, projected.TryGetProperty("checkpoint", out var field));
        if (present) Assert.Equal(JsonValueKind.Null, field.ValueKind);
        Assert.Null(AgentResultJson.FromJson(projected).Checkpoint);
        Assert.Null(AgentResultJson.FromJson(Envelope("custom", "null")).Checkpoint);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"\"")]
    [InlineData("{\"position\":\"afterModel\"}")]
    [InlineData("{\"position\":\"afterModel\",\"schemaVersion\":\"0.9\"}")]
    [InlineData("{\"position\":\"after_model\",\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"position\":null,\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"position\":\"afterModel\",\"schemaVersion\":\"1.0\",\"cycleIndex\":-1}")]
    [InlineData("{\"position\":\"afterModel\",\"schemaVersion\":\"1.0\",\"cycleIndex\":1.5}")]
    [InlineData("{\"position\":\"afterModel\",\"schemaVersion\":\"1.0\",\"cycleIndex\":2147483648}")]
    public void EveryNonnullMarkerDelegatesToTheStrictNativeConverter(string marker)
        => Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Envelope("custom", marker)));

    [Fact]
    public void ReservedCheckpointDuplicatesRejectBeforeResultPublication()
    {
        var marker = JsonSerializer.Serialize(new AgentCheckpoint(CheckpointPosition.AfterModel));
        foreach (var pair in new[] { (First: "null", Second: "null"), (First: "null", Second: marker), (First: marker, Second: marker) })
        {
            var json = "{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":\"custom\",\"message\":null,\"checkpoint\":" + pair.First + ",\"checkpoint\":" + pair.Second + "}";
            Assert.Throws<JsonException>(() => AgentResultJson.FromJson(Parse(json)));
        }
    }

    [Fact]
    public void MarkerUnknownFieldsDoNotBecomeResultExecutionState()
    {
        var restored = AgentResultJson.FromJson(Envelope("custom", "{\"position\":\"afterTools\",\"cycleIndex\":4,\"schemaVersion\":\"1.0\",\"future\":{\"secret\":1}}"));
        var projected = AgentResultJson.ToJson(restored).GetProperty("checkpoint");
        Assert.Equal(new[] { "position", "cycleIndex", "schemaVersion" }, projected.EnumerateObject().Select(p => p.Name));
        Assert.Empty(restored.InvocationState);
        Assert.Empty(restored.Traces);
    }

    [Fact]
    public void DefaultJsonUsesTheActualMarkerConverterOnlyWhenPresent()
    {
        var result = Result("custom") with { Checkpoint = new(CheckpointPosition.AfterTools, int.MaxValue) };
        var json = JsonSerializer.SerializeToElement(result);
        var marker = json.GetProperty("Checkpoint");
        Assert.Equal("afterTools", marker.GetProperty("position").GetString());
        Assert.Equal(int.MaxValue, marker.GetProperty("cycleIndex").GetInt32());
        Assert.Equal("1.0", marker.GetProperty("schemaVersion").GetString());
        var web = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonElement.DeepEquals(marker, web.GetProperty("checkpoint")));
    }

    [Fact]
    public void RecordCopiesCanClearTheMarkerWithoutMutatingTheOriginal()
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterModel, 2);
        var original = Result("custom") with { Checkpoint = marker };
        var copy = original with { Checkpoint = null };
        Assert.Same(marker, original.Checkpoint);
        Assert.Null(copy.Checkpoint);
        Assert.False(JsonSerializer.SerializeToElement(copy).TryGetProperty("Checkpoint", out _));
        Assert.Equal(original with { Checkpoint = new(CheckpointPosition.AfterModel, 2) }, original);
    }

    [Fact]
    public void HydratedMarkerOutlivesItsSourceJsonDocument()
    {
        AgentResult restored;
        using (var document = JsonDocument.Parse(Envelope("custom", "{\"position\":\"afterModel\",\"schemaVersion\":\"1.0\"}").GetRawText()))
            restored = AgentResultJson.FromJson(document.RootElement);
        Assert.Equal(new(CheckpointPosition.AfterModel), restored.Checkpoint);
        Assert.Equal("1.0", AgentResultJson.ToJson(restored).GetProperty("checkpoint").GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public void MarkersDoNotChangeExistingRenderingPriorities()
    {
        var result = Result("custom") with { Checkpoint = new(CheckpointPosition.AfterModel) };
        Assert.Equal("answer", AgentResultRendering.Render(result));
        Assert.Equal("answer\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        Assert.Equal("null", AgentResultRendering.Render(result with { StructuredOutput = Parse("null") }));
        var interrupted = result with { Interrupts = [new("id", "approve", "call")] };
        Assert.Contains("approve", AgentResultRendering.Render(interrupted), StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", AgentResultRendering.Render(interrupted), StringComparison.Ordinal);
    }

    private static AgentResult Result(string reason) => new(reason, Message.Assistant("answer"), new(), 0, new Dictionary<string, object?>());
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static JsonElement Envelope(string reason, string marker) => Parse("{\"type\":\"strands.agent_result\",\"schemaVersion\":1,\"stopReason\":" + JsonSerializer.Serialize(reason) + ",\"message\":null,\"checkpoint\":" + marker + "}");
}
