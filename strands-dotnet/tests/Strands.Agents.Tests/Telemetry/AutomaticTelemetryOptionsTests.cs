using System.Collections.Concurrent;
using System.Diagnostics;

namespace Strands.Agents.Tests.Telemetry;

public sealed partial class AutomaticTelemetryTests
{
    [Fact]
    public async Task CustomAttributesAreDetachedAndInheritedByForegroundAndBackgroundOperations()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var values = new[] { "original" };
        var attributes = new Dictionary<string, object?> { ["tenant"] = "original", ["labels"] = values };
        var tool = BoundaryTool("calculate", (_, _, _) => new("success"));
        await using var agent = new Agent(new()
        {
            Model = new TimingModel(true),
            Tools = [tool],
            TraceAttributes = attributes,
            BackgroundTasks = new() { Always = ["calculate"] }
        });
        values[0] = "mutated";
        attributes["tenant"] = "mutated";
        await agent.InvokeAsync("attributes");
        await using var child = agent.CreateChild();
        await child.InvokeAsync("child");
        Assert.Contains(stopped, span => span.DisplayName == "execute_tool calculate");
        Assert.All(stopped, span =>
        {
            Assert.Equal("original", span.GetTagItem("tenant"));
            Assert.Equal(new[] { "original" }, Assert.IsType<string[]>(span.GetTagItem("labels")));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolResultStatusPolicyIsAnExplicitAgentChoice(bool pythonPolicy)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        await using var agent = new Agent(new() { Model = new TimingModel(true), UsePythonToolErrorStatus = pythonPolicy });
        await agent.InvokeAsync("missing tool");
        var span = Assert.Single(stopped, item => item.DisplayName == "execute_tool calculate");
        Assert.Equal(pythonPolicy ? ActivityStatusCode.Error : ActivityStatusCode.Ok, span.Status);
        Assert.DoesNotContain(span.Events, item => item.Name == "exception");
        Assert.Equal(1, Assert.Single(agent.Metrics.ToolMetrics).Value.ErrorCount);
    }

    [Fact]
    public async Task InvocationContentRecordsOnlyNewInputAndTheConfiguredInstructions()
    {
        var previous = Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN");
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", null);
        try
        {
            var stopped = new ConcurrentQueue<Activity>();
            using var listener = Listen(stopped);
            await using var agent = new Agent(new()
            {
                Model = new TimingModel(false),
                SystemPrompt = "current instructions",
                Messages = [Message.User("historical input"), Message.Assistant("historical output")]
            });
            await agent.InvokeAsync("new input");
            var span = Assert.Single(stopped, item => item.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
            var input = Assert.Single(span.Events, item => item.Name == "gen_ai.user.message");
            Assert.Contains("new input", input.Tags.Single(pair => pair.Key == "content").Value!.ToString());
            Assert.DoesNotContain("historical", input.Tags.Single(pair => pair.Key == "content").Value!.ToString());
            var instructions = Assert.Single(span.Events, item => item.Name == "gen_ai.system.message");
            Assert.Contains("current instructions", instructions.Tags.Single(pair => pair.Key == "content").Value!.ToString());
        }
        finally { Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", previous); }
    }
}
