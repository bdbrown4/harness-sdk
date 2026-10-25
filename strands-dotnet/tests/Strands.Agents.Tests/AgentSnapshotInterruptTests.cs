using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentSnapshotInterruptTests
{
    private const SnapshotFields InterruptField = SnapshotFields.InterruptState;

    [Fact]
    public async Task SessionCheckpointRestoresPauseAndSkipsPreviouslyCompletedEffects()
    {
        var effects = new Effects();
        await using var source = Create(effects, new PauseModel());
        var paused = await source.InvokeAsync("task");
        var checkpoint = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.True(checkpoint.Data.ContainsKey("interruptState"));
        var roundTrip = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(checkpoint))!;
        var model = new CompletionModel();
        await using var target = Create(effects, model);

        await target.LoadSnapshotAsync(roundTrip);
        var question = Assert.Single(target.InterruptState!.Interrupts);
        var completed = await target.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });

        Assert.Equal(StopReasons.EndTurn, completed.StopReason);
        Assert.Equal(1, effects.Done);
        Assert.Equal(1, effects.Approved);
        Assert.Equal(1, model.Calls);
        Assert.Null(target.InterruptState);
        Assert.Equal(new[] { "done", "approve" }, target.Messages[2].Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
        Assert.Equal(Assert.Single(paused.Interrupts).Id, question.Id);
    }

    [Fact]
    public async Task PartialStateLoadPreservesPauseAndExplicitNullClearsIt()
    {
        await using var agent = Create(new(), new PauseModel());
        await agent.InvokeAsync("task");
        var paused = agent.InterruptState!;
        var checkpoint = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State });

        await agent.LoadSnapshotAsync(checkpoint);
        Assert.Equal(paused.Interrupts[0].Id, agent.InterruptState!.Interrupts[0].Id);
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["interruptState"] = JsonSerializer.SerializeToElement<object?>(null) }));

        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task IncompatibleMessagesCannotLeaveAStalePendingBatchOrPartiallyChangeState()
    {
        await using var agent = Create(new(), new PauseModel());
        await agent.InvokeAsync("task");
        agent.State.Set("keep", 1);
        var paused = agent.InterruptState!;
        var replacement = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("different") }),
            ["state"] = JsonSerializer.SerializeToElement(new { keep = 2 })
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(replacement).AsTask());

        Assert.Equal(1, agent.State.Get<int>("keep"));
        Assert.Equal(paused.AssistantMessage.TrackingId, agent.Messages.Last().TrackingId);
        Assert.Equal(paused.Interrupts[0].Id, agent.InterruptState!.Interrupts[0].Id);
    }

    [Fact]
    public async Task ProvidedPauseAndHistoryMismatchIsRejectedBeforeSessionReads()
    {
        await using var source = Create(new(), new PauseModel());
        await source.InvokeAsync("task");
        var store = new CountingStore();
        await using var target = new Agent(new() { Model = new CompletionModel(), SessionStore = store, SessionId = "session" });
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("different") }),
            ["interruptState"] = JsonSerializer.SerializeToElement(source.InterruptState)
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(snapshot).AsTask());

        Assert.Equal(0, store.Loads);
        Assert.Empty(target.Messages);
    }

    [Fact]
    public async Task PauseOnlyCheckpointCanBindToItsExistingAssistantHistory()
    {
        var effects = new Effects();
        await using var source = Create(effects, new PauseModel());
        await source.InvokeAsync("task");
        var checkpoint = await source.TakeSnapshotAsync(new() { Include = InterruptField });
        Assert.Equal(new[] { "interruptState" }, checkpoint.Data.Keys);
        var model = new CompletionModel();
        await using var target = Create(effects, model, source.Messages);

        await target.LoadSnapshotAsync(checkpoint);
        var question = Assert.Single(target.InterruptState!.Interrupts);
        await target.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });

        Assert.Equal(1, effects.Done);
        Assert.Equal(1, effects.Approved);
        Assert.Equal(1, model.Calls);
    }

    private static Agent Create(Effects effects, IModel model, IReadOnlyList<Message>? messages = null) => new(new()
    {
        Model = model,
        ToolExecution = ToolExecutionStrategy.Sequential,
        Messages = messages ?? [],
        Tools =
        [
            new FunctionTool(new("done", "Completes one external effect.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effects.Done++; return new("saved"); }),
            new FunctionTool(new("approve", "Requests approval.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
            {
                Assert.True(context.Interrupt("approval").GetBoolean());
                effects.Approved++;
                return new("approved");
            })
        ]
    });

    private sealed class Effects { internal int Done, Approved; }
    private sealed class CountingStore : ISessionStore
    {
        internal int Loads;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }
    private sealed class PauseModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            foreach (var name in new[] { "done", "approve" })
            {
                var index = name == "done" ? 0 : 1;
                yield return new ModelContentBlockStartEvent(index) { ContentBlock = new ToolUseBlock(name, name, JsonSerializer.SerializeToElement(new { })) };
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
    private sealed class CompletionModel : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("complete") };
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
