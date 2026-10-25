using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelReassignmentTests
{
    [Fact]
    public async Task CurrentPrimaryControlsNullAuxiliaryWhileExplicitAuxiliaryStaysSelected()
    {
        var original = new RecordingModel("original");
        var replacement = new RecordingModel("replacement");
        var auxiliary = new RecordingModel("auxiliary");
        var originalOptions = new AgentOptions { Model = original };
        await using var following = new Agent(originalOptions);
        await using var explicitAuxiliary = new Agent(new() { Model = original, AuxModel = auxiliary });

        following.SetModel(replacement);
        explicitAuxiliary.SetModel(replacement);

        Assert.Same(replacement, following.Model);
        Assert.Same(replacement, following.AuxModel);
        Assert.Same(replacement, explicitAuxiliary.Model);
        Assert.Same(auxiliary, explicitAuxiliary.AuxModel);
        Assert.Same(original, originalOptions.Model);
        Assert.Empty(original.Requests);
        Assert.Empty(replacement.Requests);
        Assert.Empty(auxiliary.Requests);
    }

    [Fact]
    public async Task ToolReplacementControlsTheNextMainModelTurn()
    {
        var original = new RecordingModel("old final", selectOnFirstRequest: true);
        var replacement = new RecordingModel("new final");
        var called = false;
        var select = new FunctionTool(new("select_model", "Select the ready model.", Json("{\"type\":\"object\",\"additionalProperties\":false}")),
            (_, context, _) =>
            {
                context.Agent.SetModel(replacement);
                called = true;
                return new ValueTask<string>("selected");
            });
        await using var agent = new Agent(new() { Model = original, Tools = [select] });

        var result = await agent.InvokeAsync("choose the next model");

        Assert.True(called);
        Assert.Equal("new final", result.Message!.Text);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(2, result.Turns);
        Assert.Single(original.Requests);
        Assert.Single(replacement.Requests);
        Assert.Equal([Role.User, Role.Assistant, Role.User, Role.Assistant], agent.Messages.Select(message => message.Role));
        var call = Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[1].Content));
        Assert.Equal("model-swap", call.ToolUseId);
        Assert.Equal("select_model", call.Name);
        var response = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[2].Content));
        Assert.Equal(call.ToolUseId, response.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, response.Status);
        Assert.Equal("selected", response.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalConstructionOwnershipSurvivesBorrowedReplacement(bool ownsOriginal)
    {
        var order = new List<string>();
        var original = new RecordingModel("original") { OnDispose = () => order.Add("original") };
        var replacement = new RecordingModel("replacement");
        var resource = new OwnedResource(order);
        var options = new AgentOptions { Model = original, OwnsModel = ownsOriginal, OwnedResources = [resource] };
        var agent = new Agent(options);
        try
        {
            agent.SetModel(replacement);
            Assert.Same(replacement, agent.Model);
            Assert.Equal(0, original.DisposeCalls);
            Assert.Equal(0, replacement.DisposeCalls);
        }
        finally { await agent.DisposeAsync(); }

        Assert.Equal(ownsOriginal ? 1 : 0, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(ownsOriginal ? new[] { "resource", "original" } : new[] { "resource" }, order);
        Assert.Same(original, options.Model);
    }

    [Fact]
    public async Task ChildrenInheritCurrentSelectionsAndKeepBorrowedOwnership()
    {
        var original = new RecordingModel("original");
        var replacement = new RecordingModel("replacement");
        var oldAuxiliary = new RecordingModel("old auxiliary");
        var currentAuxiliary = new RecordingModel("current auxiliary");
        var configuredChildModel = new RecordingModel("configured child");
        await using var parent = new Agent(new() { Model = original, AuxModel = oldAuxiliary, OwnsModel = true });
        parent.SetModel(replacement);
        parent.SetAuxModel(currentAuxiliary);

        await using (var selected = parent.CreateChild())
        {
            Assert.Same(replacement, selected.Model);
            Assert.Same(currentAuxiliary, selected.AuxModel);
            Assert.Empty(selected.ModelState.GetAll());
        }
        Assert.Equal(0, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
        Assert.Equal(0, currentAuxiliary.DisposeCalls);

        parent.SetAuxModel(null);
        await using (var configured = parent.CreateChild(value => value with { Model = configuredChildModel }))
        {
            Assert.Same(configuredChildModel, configured.Model);
            Assert.Same(configuredChildModel, configured.AuxModel);
            Assert.Empty(configured.Messages);
        }
        Assert.Same(replacement, parent.AuxModel);
        Assert.Equal(0, configuredChildModel.DisposeCalls);
        Assert.Equal(0, oldAuxiliary.DisposeCalls);
    }

    [Theory]
    [InlineData(ModelSelectionTiming.BeforeModelHooks, "original")]
    [InlineData(ModelSelectionTiming.AfterModelHooks, "replacement")]
    public async Task BeforeModelHookReplacementUsesTheExplicitSelectionTiming(ModelSelectionTiming timing, string expectedFirst)
    {
        var original = new RecordingModel("original");
        var replacement = new RecordingModel("replacement");
        var hooks = new HookRegistry();
        var changed = false;
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            if (!changed)
            {
                changed = true;
                value.Agent.SetModel(replacement);
            }
            return default;
        });
        await using var agent = new Agent(new() { Model = original, Hooks = hooks, ModelSelectionTiming = timing });

        var first = await agent.InvokeAsync("first");
        var second = await agent.InvokeAsync("second");

        Assert.True(changed);
        Assert.Equal(expectedFirst, first.Message!.Text);
        Assert.Equal("replacement", second.Message!.Text);
        Assert.Same(replacement, agent.Model);
        if (timing == ModelSelectionTiming.BeforeModelHooks)
        {
            Assert.Single(original.Requests);
            Assert.Single(replacement.Requests);
        }
        else
        {
            Assert.Empty(original.Requests);
            Assert.Collection(replacement.Requests, _ => { }, _ => { });
        }
    }

    [Fact]
    public async Task DefaultTimingAndConstructionModelRemainUnchangedWithoutReplacement()
    {
        var original = new RecordingModel("original");
        var options = new AgentOptions { Model = original };
        await using var agent = new Agent(options);

        Assert.Equal(ModelSelectionTiming.BeforeModelHooks, options.ModelSelectionTiming);
        Assert.Equal(0, (int)ModelSelectionTiming.BeforeModelHooks);
        Assert.Same(original, agent.Model);
        Assert.Same(original, agent.AuxModel);
        Assert.Empty(original.Requests);
        Assert.Equal(0, original.DisposeCalls);
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private sealed class RecordingModel(string text, bool selectOnFirstRequest = false) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal int DisposeCalls { get; private set; }
        internal Action? OnDispose { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            if (selectOnFirstRequest && Requests.Count == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("model-swap", "select_model", Json("{}")));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: text);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            OnDispose?.Invoke();
            return default;
        }
    }

    private sealed class OwnedResource(List<string> order) : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            order.Add("resource");
            return default;
        }
    }
}
