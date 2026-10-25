using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests;

public sealed class AuxModelReassignmentTests
{
    [Fact]
    public async Task ReadyAuxiliaryModelCanBeReassignedAndReset()
    {
        var primary = new RecordingModel("primary");
        var original = new RecordingModel("original");
        var replacement = new RecordingModel("replacement");
        await using var agent = new Agent(new() { Model = primary, AuxModel = original });

        Assert.Same(original, agent.AuxModel);
        agent.SetAuxModel(replacement);
        Assert.Same(replacement, agent.AuxModel);
        Assert.Same(primary, agent.Model);

        agent.SetAuxModel(null);
        Assert.Same(primary, agent.AuxModel);
        Assert.Empty(primary.Requests);
        Assert.Empty(original.Requests);
        Assert.Empty(replacement.Requests);
        Assert.Equal(0, primary.DisposeCalls);
        Assert.Equal(0, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    [Fact]
    public async Task ToolReassignmentControlsTheNextAgenticSummary()
    {
        var primary = new RecordingModel("primary");
        var original = new RecordingModel("original summary");
        var replacement = new RecordingModel("replacement summary");
        var selectionReached = false;
        var empty = JsonSerializer.Deserialize<JsonElement>("{}");
        var select = new FunctionTool(new("select_aux", "Select the ready auxiliary model.", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false}""")),
            (_, context, _) =>
            {
                context.Agent.SetAuxModel(replacement);
                selectionReached = true;
                return new ValueTask<string>("selected");
            });
        await using var agent = new Agent(new()
        {
            Model = primary,
            AuxModel = original,
            Tools = [select],
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [], StashEnabled = false }),
            Messages = SummaryHistory()
        });

        var selected = await agent.InvokeToolAsync(new("choose", "select_aux", empty));
        Assert.Equal(ToolResultStatus.Success, selected.Status);
        Assert.Equal("selected", selected.Content);
        Assert.True(selectionReached);
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":3,"summaryRatio":0.6}""");
        var result = await agent.InvokeToolAsync(new("summary", "summarize_context", input));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.StartsWith("Summarized ", result.Content);
        Assert.Single(replacement.Requests);
        Assert.Empty(original.Requests);
        Assert.Empty(primary.Requests);
        Assert.Contains(agent.Messages, message => message.Text == "replacement summary");
        Assert.Same(primary, agent.Model);
        Assert.Same(replacement, agent.AuxModel);
    }

    [Fact]
    public async Task ChildInheritsCurrentExplicitSelectionAndResetFollowsChildPrimary()
    {
        var primary = new RecordingModel("primary");
        var original = new RecordingModel("original");
        var replacement = new RecordingModel("replacement");
        var childPrimary = new RecordingModel("child primary");
        var options = new AgentOptions { Model = primary, AuxModel = original };
        await using var agent = new Agent(options);

        agent.SetAuxModel(replacement);
        await using var selectedChild = agent.CreateChild();
        Assert.Same(replacement, selectedChild.AuxModel);
        Assert.Same(primary, selectedChild.Model);

        agent.SetAuxModel(null);
        await using var followingChild = agent.CreateChild(value => value with { Model = childPrimary });
        Assert.Same(childPrimary, followingChild.AuxModel);
        Assert.Same(childPrimary, followingChild.Model);
        Assert.Same(primary, agent.AuxModel);
        Assert.Same(replacement, selectedChild.AuxModel);
        Assert.Same(original, options.AuxModel);
        Assert.Empty(primary.Requests);
        Assert.Empty(original.Requests);
        Assert.Empty(replacement.Requests);
        Assert.Empty(childPrimary.Requests);
    }

    [Fact]
    public async Task UnsetAuxiliaryModelFollowsPrimaryWithoutStartingIt()
    {
        var primary = new RecordingModel("primary");
        await using var agent = new Agent(new() { Model = primary });

        Assert.Same(primary, agent.AuxModel);
        Assert.Empty(primary.Requests);
        Assert.Equal(0, primary.DisposeCalls);
    }

    [Fact]
    public async Task ConfiguredAuxiliaryModelIsBorrowedDuringAgentDisposal()
    {
        var primary = new RecordingModel("primary");
        var auxiliary = new RecordingModel("auxiliary");
        var agent = new Agent(new() { Model = primary, AuxModel = auxiliary });
        Assert.Same(auxiliary, agent.AuxModel);

        await agent.DisposeAsync();

        Assert.Empty(primary.Requests);
        Assert.Empty(auxiliary.Requests);
        Assert.Equal(0, primary.DisposeCalls);
        Assert.Equal(0, auxiliary.DisposeCalls);
    }

    private static Message[] SummaryHistory() =>
    [
        Message.User("first"),
        Message.Assistant(new string('a', 2_000)),
        Message.User(new string('b', 2_000)),
        Message.Assistant(new string('c', 2_000)),
        Message.User("recent user"),
        Message.Assistant("recent reply"),
        Message.User("latest")
    ];

    private sealed class RecordingModel(string response) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal int DisposeCalls { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: response);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return default;
        }
    }
}
