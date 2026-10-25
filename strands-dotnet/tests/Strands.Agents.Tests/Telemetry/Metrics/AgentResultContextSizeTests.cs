using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.Telemetry.Metrics;

public sealed class AgentResultContextSizeTests
{
    [Fact]
    public void ManuallyCreatedResultHasNoInventedContextSize()
    {
        var result = new AgentResult(StopReasons.EndTurn, null, new(), 0, new Dictionary<string, object?>());
        Assert.Null(Read(result, "ContextSize"));
        Assert.Null(Read(result, "ProjectedContextSize"));
    }

    [Fact]
    public async Task ActualInvocationExposesTheSelectedMetricSnapshot()
    {
        await using var agent = new Agent(new() { Model = new UsageModel() });
        var result = await agent.InvokeAsync("hello");
        var metrics = Assert.IsType<Strands.Agents.Telemetry.AgentMetrics>(result.Metrics);
        Assert.Equal(18, metrics.LatestContextSize);
        Assert.Equal(20, metrics.ProjectedContextSize);
        Assert.Equal(metrics.LatestContextSize, Read(result, "ContextSize"));
        Assert.Equal(metrics.ProjectedContextSize, Read(result, "ProjectedContextSize"));

        var withoutMetrics = result with { Metrics = null };
        Assert.Null(Read(withoutMetrics, "ContextSize"));
        Assert.Null(Read(withoutMetrics, "ProjectedContextSize"));
        Assert.Equal(18, Read(result, "ContextSize"));
    }

    [Fact]
    public void ConveniencePropertiesAreReadOnlyAndExcludedFromLegacyJson()
    {
        foreach (var name in new[] { "ContextSize", "ProjectedContextSize" })
        {
            var property = Assert.IsAssignableFrom<PropertyInfo>(typeof(AgentResult).GetProperty(name));
            Assert.Equal(typeof(long?), property.PropertyType);
            Assert.Null(property.SetMethod);
        }
        var result = new AgentResult(StopReasons.EndTurn, null, new(), 0, new Dictionary<string, object?>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.TryGetProperty("ContextSize", out _));
        Assert.False(json.RootElement.TryGetProperty("ProjectedContextSize", out _));
        Assert.True(json.RootElement.TryGetProperty("Metrics", out _));
    }

    [Fact]
    public void LegacyJsonShapeIsUnchangedWhenNoMetricsWereCollected()
    {
        var result = new AgentResult(StopReasons.EndTurn, null, new(), 0, new Dictionary<string, object?>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(new[] { "StopReason", "Message", "Usage", "Turns", "InvocationState", "Metrics", "Traces", "Interrupts" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static long? Read(AgentResult result, string name)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(typeof(AgentResult).GetProperty(name));
        return (long?)property.GetValue(result);
    }

    private sealed class UsageModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelMetadataEvent(new Usage(10, 2, 5, 3, 20));
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
