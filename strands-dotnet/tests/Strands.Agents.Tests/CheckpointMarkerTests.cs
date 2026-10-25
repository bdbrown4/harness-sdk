using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointMarkerTests
{
    [Theory]
    [InlineData(CheckpointPosition.AfterModel, "afterModel", 0)]
    [InlineData(CheckpointPosition.AfterTools, "afterTools", 7)]
    public void SerializedMarkersPreserveTheirVersionedBoundary(CheckpointPosition position, string wirePosition, int cycleIndex)
    {
        var marker = new AgentCheckpoint(position, cycleIndex);

        var json = JsonSerializer.SerializeToElement(marker);

        Assert.Equal(wirePosition, json.GetProperty("position").GetString());
        Assert.Equal(cycleIndex, json.GetProperty("cycleIndex").GetInt32());
        Assert.Equal("1.0", json.GetProperty("schemaVersion").GetString());
        Assert.Equal(3, json.EnumerateObject().Count());
    }

    [Fact]
    public void ReadingAMarkerDefaultsOnlyTheMissingCycleIndex()
    {
        var marker = JsonSerializer.Deserialize<AgentCheckpoint>("{\"position\":\"afterTools\",\"schemaVersion\":\"1.0\"}");

        Assert.NotNull(marker);
        Assert.Equal(CheckpointPosition.AfterTools, marker.Position);
        Assert.Equal(0, marker.CycleIndex);
        Assert.Equal("1.0", marker.SchemaVersion);
    }

    [Theory]
    [InlineData("\"0.9\"")]
    [InlineData("null")]
    [InlineData("missing")]
    public void AnIncompatibleOrMissingSchemaVersionCannotBeIgnored(string version)
    {
        var json = version == "missing" ? "{\"position\":\"afterModel\"}" :
            "{\"position\":\"afterModel\",\"schemaVersion\":" + version + "}";

        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentCheckpoint>(json));

        Assert.Contains("schema version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CheckpointPosition.AfterModel, -1)]
    [InlineData((CheckpointPosition)99, 0)]
    public void NativeMarkersRejectInvalidBoundaryValues(CheckpointPosition position, int cycleIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentCheckpoint(position, cycleIndex));
    }

    [Fact]
    public void ValidMarkersHaveDetachedValueIdentity()
    {
        var marker = new AgentCheckpoint(CheckpointPosition.AfterTools, 3);

        Assert.Equal(CheckpointPosition.AfterTools, marker.Position);
        Assert.Equal(3, marker.CycleIndex);
        Assert.Equal("1.0", marker.SchemaVersion);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools, 3), marker);
        Assert.NotEqual(new AgentCheckpoint(CheckpointPosition.AfterTools, 4), marker);
    }

    [Theory]
    [InlineData(CheckpointPosition.AfterModel, "afterModel")]
    [InlineData(CheckpointPosition.AfterTools, "afterTools")]
    public void VersionedMarkersRoundTripTheLargestNativeCycle(CheckpointPosition position, string wirePosition)
    {
        var original = new AgentCheckpoint(position, int.MaxValue);
        var encoded = JsonSerializer.Serialize(original);

        Assert.Equal(original, JsonSerializer.Deserialize<AgentCheckpoint>(encoded));
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(wirePosition, document.RootElement.GetProperty("position").GetString());
        Assert.Equal(int.MaxValue, document.RootElement.GetProperty("cycleIndex").GetInt32());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"position\":null}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"position\":\"AfterModel\"}")]
    [InlineData("{\"schemaVersion\":1.0,\"position\":\"afterModel\"}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"position\":\"afterModel\",\"cycleIndex\":-1}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"position\":\"afterModel\",\"cycleIndex\":0.5}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"position\":\"afterModel\",\"cycleIndex\":2147483648}")]
    public void InvalidMarkerValuesCannotAcquireAValidBoundary(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentCheckpoint>(json));
    }

    [Fact]
    public void CallerNamingAndDefaultOmissionDoNotChangeTheMarkerSchema()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
        };

        var encoded = JsonSerializer.SerializeToElement(new AgentCheckpoint(CheckpointPosition.AfterModel), options);

        Assert.Equal("afterModel", encoded.GetProperty("position").GetString());
        Assert.Equal(0, encoded.GetProperty("cycleIndex").GetInt32());
        Assert.Equal("1.0", encoded.GetProperty("schemaVersion").GetString());
        Assert.False(encoded.TryGetProperty("cycle_index", out _));
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), encoded.Deserialize<AgentCheckpoint>(options));
    }

    [Fact]
    public void UnknownFutureFieldsDoNotBecomeConversationState()
    {
        const string input = "{\"position\":\"afterTools\",\"cycleIndex\":3,\"schemaVersion\":\"1.0\",\"messages\":[\"untrusted\"],\"future\":true}";

        var marker = JsonSerializer.Deserialize<AgentCheckpoint>(input);
        var output = JsonSerializer.SerializeToElement(marker);

        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools, 3), marker);
        Assert.False(output.TryGetProperty("messages", out _));
        Assert.False(output.TryGetProperty("future", out _));
    }

    [Fact]
    public void NullableAbsenceDoesNotInventAPauseMarker()
    {
        Assert.Equal("null", JsonSerializer.Serialize<AgentCheckpoint?>(null));
        Assert.Null(JsonSerializer.Deserialize<AgentCheckpoint>("null"));
    }
}
