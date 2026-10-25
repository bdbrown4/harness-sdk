using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class StructuredOutputPreemptionTests
{
    [Theory]
    [InlineData(StructuredOutputBehavior.Python, "end-turn", true)]
    [InlineData(StructuredOutputBehavior.Python, "deferred-cancel", true)]
    [InlineData(StructuredOutputBehavior.Python, "immediate-cancel", true)]
    [InlineData(StructuredOutputBehavior.TypeScript, "end-turn", false)]
    [InlineData(StructuredOutputBehavior.TypeScript, "deferred-cancel", false)]
    [InlineData(StructuredOutputBehavior.TypeScript, "immediate-cancel", false)]
    [InlineData(StructuredOutputBehavior.Native, "end-turn", false)]
    [InlineData(StructuredOutputBehavior.Native, "deferred-cancel", false)]
    [InlineData(StructuredOutputBehavior.Native, "immediate-cancel", false)]
    public async Task AcceptedPythonCacheTravelsWithThePreemptingResult(StructuredOutputBehavior behavior, string preemption, bool expectStructured)
    {
        var model = new OneCallModel(false);
        var hooks = new HookRegistry();
        var boundaries = 0;
        using var registration = hooks.Register<AfterToolsEvent>((value, _) =>
        {
            boundaries++;
            if (preemption == "end-turn") value.EndTurn = [new TextBlock("hook completed")];
            else value.Agent.Cancel("cancel completed", afterCurrentTools: preemption == "deferred-cancel");
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(behavior) });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(preemption == "end-turn" ? StopReasons.EndTurn : StopReasons.Cancelled, result.StopReason);
        Assert.Equal(preemption == "end-turn" ? "hook completed" : "cancel completed", result.Message!.Text);
        Assert.Equal(1, boundaries);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.Disposals);
        var accepted = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("answer", accepted.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, accepted.Status);
        if (expectStructured) Assert.Equal(7, Assert.IsType<JsonElement>(result.StructuredOutput).GetProperty("value").GetInt32());
        else Assert.Null(result.StructuredOutput);
        Assert.Single(agent.Messages, message => message.Text == "question");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARejectedPythonBodyCannotCreateAPreemptedPayload(bool cancel)
    {
        var model = new OneCallModel(true);
        var hooks = new HookRegistry();
        using var registration = hooks.Register<AfterToolsEvent>((value, _) =>
        {
            if (cancel) value.Agent.Cancel("cancel completed", afterCurrentTools: true);
            else value.EndTurn = [new TextBlock("hook completed")];
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, StructuredOutput = Format(StructuredOutputBehavior.Python) });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(cancel ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.StructuredOutput);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.Disposals);
    }

    [Theory]
    [InlineData(StructuredOutputBehavior.Python, true)]
    [InlineData(StructuredOutputBehavior.TypeScript, false)]
    [InlineData(StructuredOutputBehavior.Native, false)]
    public async Task ACompletedStructuredSiblingKeepsPythonPayloadWhileHumanInputWins(StructuredOutputBehavior behavior, bool expectStructured)
    {
        var model = new SiblingModel();
        var validated = 0;
        var attempts = 0;
        var effects = 0;
        var approval = new FunctionTool(new("approval", "Test approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            attempts++;
            context.Interrupt("approval");
            effects++;
            return new("approved");
        });
        var options = Format(behavior) with { Deserializer = element => { validated++; return element; } };
        await using var agent = new Agent(new() { Model = model, ToolExecution = ToolExecutionStrategy.Sequential, Tools = [approval], StructuredOutput = options });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        var question = Assert.Single(result.Interrupts);
        Assert.Equal("approval", question.Name);
        Assert.Equal("pause", question.ToolUseId);
        Assert.Equal(InterruptSource.Tool, question.Source);
        Assert.Equal(question, Assert.Single(agent.InterruptState!.Interrupts));
        Assert.Equal(1, attempts);
        Assert.Equal(0, effects);
        Assert.Equal(1, validated);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.Disposals);
        var completed = Assert.Single(agent.InterruptState.CompletedToolResults);
        Assert.Equal("answer", completed.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, completed.Status);
        Assert.Equal(Role.Assistant, agent.Messages.Last().Role);
        Assert.Equal(new[] { "answer", "pause" }, agent.Messages.Last().Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
        if (expectStructured) Assert.Equal(7, Assert.IsType<JsonElement>(result.StructuredOutput).GetProperty("value").GetInt32());
        else Assert.Null(result.StructuredOutput);
    }

    private sealed class SiblingModel : IModel
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls != 1) throw new InvalidOperationException("A paused batch cannot call the model again.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("answer", StructuredOutputTool<JsonElement>.ToolName, JsonSerializer.SerializeToElement(new { value = 7 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelContentBlockStartEvent(1, new ToolUseBlock("pause", "approval", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(1);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
                await Task.CompletedTask;
            }
            finally { Disposals++; }
        }
    }

    private static StructuredOutputOptions<JsonElement> Format(StructuredOutputBehavior behavior) => new()
    {
        Behavior = behavior,
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };

    private sealed class OneCallModel(bool invalid) : IModel
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls != 1) throw new InvalidOperationException("Only one provider turn is permitted by this fixture.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                var input = invalid ? JsonSerializer.SerializeToElement(new { value = "rejected" }) : JsonSerializer.SerializeToElement(new { value = 7 });
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("answer", StructuredOutputTool<JsonElement>.ToolName, input));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
                await Task.CompletedTask;
            }
            finally { Disposals++; }
        }
    }
}
