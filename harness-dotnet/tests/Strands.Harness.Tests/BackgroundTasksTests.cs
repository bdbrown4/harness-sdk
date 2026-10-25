using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class BackgroundTasksTests
{
    [Fact]
    public async Task DefaultRegistersManagementAndExplicitNullDisablesUnlessCoreConfigurationWins()
    {
        await using var enabled = await Harness.CreateAsync(Options());
        Assert.Contains(enabled.Tools, tool => tool.Spec.Name == "strands_manage_background_task");
        await using var disabled = await Harness.CreateAsync(Options() with { BackgroundTasks = null });
        Assert.Empty(disabled.Tools);
        await using var core = await Harness.CreateAsync(Options() with { BackgroundTasks = null, AgentOptions = new() { Model = new Model(), BackgroundTasks = new() } });
        Assert.Contains(core.Tools, tool => tool.Spec.Name == "strands_manage_background_task");
    }

    [Fact]
    public async Task AgentConfigCanDisableBackgroundTasksBeforeResolvingExecutableBindings()
    {
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"skills\":false,\"memory\":false,\"session\":false,\"contextManager\":false,\"agentConfig\":{\"backgroundTasks\":false}}");
        var options = config.ToOptions(new() { ModelResolver = _ => new Model() });
        await using var agent = await Harness.CreateAsync(options);
        Assert.DoesNotContain(agent.Tools, tool => tool.Spec.Name == "strands_manage_background_task");
    }

    [Fact]
    public async Task StandardDelegationIsAlwaysBackgroundAndKeepsTheChildsToolSubset()
    {
        var model = new DelegationModel();
        await using var agent = await Harness.CreateAsync(Options() with
        {
            Model = model,
            BuiltinTools = new[] { "subagent" },
            BackgroundTasks = new() { Agentic = Array.Empty<string>(), Never = new[] { "subagent" } }
        });
        var result = await agent.InvokeAsync("delegate").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("received", result.ToString()); Assert.Equal(1, model.Children);
        var acknowledgement = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == "delegate-call");
        Assert.Contains("Background task dispatched.", acknowledgement.Content);
        var delivered = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == "strands_manage_background_task");
        Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == delivered.ToolUseId && value.ContentBlocks.OfType<TextBlock>().Any(text => text.Text == "child result"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedApplicationSubagentIsForcedIntoTheBackground(bool coreTools)
    {
        var model = new DelegationModel();
        var tool = new FunctionTool(new("subagent", "Application delegation", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("child result"));
        var options = Options() with
        {
            Model = model,
            Tools = coreTools ? Array.Empty<ITool>() : new[] { tool },
            AgentOptions = coreTools ? new() { Model = model, Tools = new[] { tool } } : null,
            BackgroundTasks = new() { Agentic = Array.Empty<string>(), Never = new[] { "subagent" } },
        };
        await using var agent = await Harness.CreateAsync(options);
        var result = await agent.InvokeAsync("delegate").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("received", result.ToString());
        var acknowledgement = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == "delegate-call");
        Assert.Contains("Background task dispatched.", acknowledgement.Content);
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.Name == "strands_manage_background_task");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedDelegationOverridesConflictingExactSelectors(bool standard)
    {
        var tool = new FunctionTool(new("subagent", "Application delegation", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("child result"));
        await using var agent = await Harness.CreateAsync(Options() with
        {
            Model = new DelegationModel(),
            BuiltinTools = standard ? new[] { "subagent" } : Array.Empty<string>(),
            Tools = standard ? Array.Empty<ITool>() : new[] { tool },
            BackgroundTasks = new() { Always = new[] { "subagent" }, Agentic = new[] { "subagent" }, Never = new[] { "subagent" } },
        });
        Assert.Equal("received", (await agent.InvokeAsync("delegate").WaitAsync(TimeSpan.FromSeconds(5))).ToString());
    }

    [Theory]
    [InlineData("{\"maxConcurrency\":0}")]
    [InlineData("{\"waitForCompletion\":\"yes\"}")]
    [InlineData("{\"timeout\":-1}")]
    [InlineData("{\"always\":[\"work\"],\"never\":[\"work\"]}")]
    public void InvalidBackgroundSettingsFailBeforeCallingModelBindings(string background)
    {
        var resolved = 0;
        var config = HarnessAgentConfig.Parse("{\"agentConfig\":{\"backgroundTasks\":" + background + "}}");
        Assert.ThrowsAny<ArgumentException>(() => config.ToOptions(new() { ModelResolver = _ => { resolved++; return new Model(); } }));
        Assert.Equal(0, resolved);
    }

    private static HarnessOptions Options() => new()
    {
        Model = new Model(),
        BuiltinTools = Array.Empty<string>(),
        BuiltinPlugins = Array.Empty<string>(),
        Session = new() { Enabled = false },
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
    };
    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); await Task.CompletedTask;
        }
    }
    private sealed class DelegationModel : IModel
    {
        private int _parentCalls;
        public int Children { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var child = request.SystemPrompt?.Contains("You are a general-purpose subagent", StringComparison.Ordinal) == true;
            var first = !child && Interlocked.Increment(ref _parentCalls) == 1;
            if (child) { Children++; Assert.Empty(request.Tools!); }
            var delivered = request.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(call => call.Name == "strands_manage_background_task");
            yield return new ModelMessageStartEvent();
            if (first)
            {
                yield return new ModelContentBlockStartEvent(0, new("delegate-call", "subagent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{\"task\":\"child task\",\"tools\":[]}");
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: child ? "child result" : delivered ? "received" : "waiting"); }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }
}
