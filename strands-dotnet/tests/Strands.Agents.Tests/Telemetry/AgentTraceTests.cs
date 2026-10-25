using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

public sealed class AgentTraceTests
{
    [Fact]
    public void HierarchyRetainsCorrelatedChildrenAndDetachedCollectionSnapshots()
    {
        var cycle = new AgentTrace("Cycle 1", startTime: 1000);
        var firstSnapshot = cycle.Children;
        var model = new AgentTrace("stream_messages", cycle, 1001);
        var tool = new AgentTrace("Tool: calculate", cycle, 1050);
        tool.SetMetadata("toolUseId", "call-1");
        tool.SetMetadata("toolName", "calculate");

        Assert.Empty(firstSnapshot);
        Assert.Equal(new[] { model, tool }, cycle.Children);
        Assert.Equal(cycle.Id, model.ParentId);
        Assert.Equal(cycle.Id, tool.ParentId);
        Assert.Null(cycle.ParentId);
        Assert.True(Guid.TryParseExact(cycle.Id, "D", out _));
        Assert.NotEqual(cycle.Id, model.Id);
        Assert.Equal("call-1", tool.Metadata["toolUseId"]);
    }

    [Fact]
    public void CompletionTimingAndOutputSurviveJsonSerializationAndSecondCompletion()
    {
        var trace = new AgentTrace("stream_messages", startTime: 1000);
        Assert.Null(trace.EndTime);
        Assert.Equal(0, trace.Duration);
        var output = Message.Assistant("answer");

        trace.End(1500, output);
        trace.End(2000, Message.Assistant("other"));

        Assert.Equal(1500, trace.EndTime);
        Assert.Equal(500, trace.Duration);
        Assert.Same(output, trace.Message);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(trace));
        Assert.Equal(1500, json.RootElement.GetProperty("EndTime").GetInt64());
        Assert.Equal(500, json.RootElement.GetProperty("Duration").GetInt64());
        Assert.Equal("answer", json.RootElement.GetProperty("Message").GetProperty("Content")[0].GetProperty("Text").GetString());
    }

    [Fact]
    public void MetadataSnapshotsCannotChangeAfterLaterUpdates()
    {
        var trace = new AgentTrace("Cycle 1");
        trace.SetMetadata("cycleId", "first");
        var snapshot = trace.Metadata;
        trace.SetMetadata("cycleId", "second");

        Assert.Equal("first", snapshot["cycleId"]);
        Assert.Equal("second", trace.Metadata["cycleId"]);
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(trace.Metadata);
        Assert.Throws<NotSupportedException>(() => dictionary.Add("injected", "bad"));
    }
}
