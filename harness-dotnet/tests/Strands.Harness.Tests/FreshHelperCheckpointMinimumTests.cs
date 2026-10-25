using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class FreshHelperCheckpointMinimumTests
{
    [Theory]
    [InlineData(CheckpointBehavior.PythonReplay)]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    public Task StandardSubagentCompletesItsFreshChildWithoutInheritingParentPauses(CheckpointBehavior behavior) =>
        VerifyStandardSubagentAsync(behavior);

    [Theory]
    [InlineData(CheckpointBehavior.PythonReplay)]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    public Task SwarmCompletesItsFreshDefaultChildWithoutInheritingParentPauses(CheckpointBehavior behavior) =>
        VerifySwarmAsync(behavior);

    [Fact]
    public Task OmittedCheckpointConfigurationKeepsStandardSubagentOrdinaryExecution() =>
        VerifyStandardSubagentAsync(null);

    [Fact]
    public Task OmittedCheckpointConfigurationKeepsSwarmOrdinaryExecution() =>
        VerifySwarmAsync(null);

    private static async Task VerifyStandardSubagentAsync(CheckpointBehavior? behavior)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var model = new EffectModel();
        var effects = 0;
        var effect = Effect(() => effects++);
        var options = ParentOptions(model, behavior);
        await using var parent = await Harness.CreateAsync(new()
        {
            Model = model,
            AgentOptions = options,
            Tools = [effect],
            BuiltinTools = ["subagent"],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }, deadline.Token);
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(parent.Tools, value => value.Spec.Name == "subagent"));
        var input = JsonSerializer.SerializeToElement(new { task = "Do the work", tools = new[] { "effect" } });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input), deadline.Token);

        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("helper-call", result.ToolUseId);
        Assert.Equal("child done", result.Content);
        await VerifyParentConfigurationAsync(parent, model, behavior, () => effects, deadline.Token);
    }

    private static async Task VerifySwarmAsync(CheckpointBehavior? behavior)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var model = new EffectModel();
        var effects = 0;
        var effect = Effect(() => effects++);
        var tool = SwarmTool.Create(new() { Tools = new Fixed(new[] { "effect" }) });
        await using var parent = new Agent(ParentOptions(model, behavior) with { Tools = [effect, tool] });
        await parent.InitializeAsync(deadline.Token);
        var input = JsonSerializer.SerializeToElement(new
        {
            task = "Do the work",
            agents = new[] { new { name = "worker", instructions = "Finish the work" } },
        });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input), deadline.Token);

        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("helper-call", result.ToolUseId);
        Assert.Equal("worker: child done", result.Content);
        await VerifyParentConfigurationAsync(parent, model, behavior, () => effects, deadline.Token);
    }

    private static AgentOptions ParentOptions(IModel model, CheckpointBehavior? behavior)
    {
        var options = new AgentOptions { Model = model };
        Assert.Equal(0, (int)options.CheckpointBehavior);
        Assert.Equal(CheckpointBehavior.Disabled, options.CheckpointBehavior);
        return behavior is { } selected ? options with { CheckpointBehavior = selected } : options;
    }

    private static async Task VerifyParentConfigurationAsync(Agent parent, EffectModel model, CheckpointBehavior? behavior,
        Func<int> effects, CancellationToken cancellationToken)
    {
        if (behavior is null) return;
        var result = await parent.InvokeAsync("Independent parent work", cancellationToken: cancellationToken);
        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal(1, effects());
        Assert.Equal(3, model.Requests);
    }

    private static FunctionTool Effect(Action effect) => new(new("effect", "Record work", ObjectSchema), (_, _, _) =>
    {
        effect();
        return new("effect done");
    });

    private static ToolContext Context(Agent parent, ITool tool, JsonElement input) =>
        new(parent, new("helper-call", tool.Spec.Name, input), new Dictionary<string, object?>());

    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });

    private sealed class EffectModel : IModel
    {
        public int Requests { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            var completed = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()
                .Any(result => result.ToolUseId == "effect-call");
            yield return new ModelMessageStartEvent();
            yield return completed ? new ModelContentBlockStartEvent(0) : new ModelContentBlockStartEvent(0, new("effect-call", "effect", Empty));
            if (completed) yield return new ModelContentBlockDeltaEvent(0, Text: "child done");
            yield return new ModelContentBlockStopEvent(0);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStopEvent(completed ? StopReasons.EndTurn : StopReasons.ToolUse);
            await Task.CompletedTask;
        }
    }
}
