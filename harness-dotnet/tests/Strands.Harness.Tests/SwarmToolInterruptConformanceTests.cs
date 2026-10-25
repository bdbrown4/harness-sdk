using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Conversation;
using Strands.Harness.Tools;
using static Strands.Harness.Tests.SwarmToolLifecycleConformanceTests;

namespace Strands.Harness.Tests;

public sealed class SwarmToolInterruptConformanceTests
{
    [Fact]
    public async Task SnapshotCleanupFailureAfterChildInterruptClosesBrokenOwnership()
    {
        var builds = 0;
        var closed = 0;
        Exception? expected = null;
        ToolResultBlock? observed = null;
        var ask = Ask();
        var input = Team("a");
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) =>
            {
                builds++;
                expected = new IOException("paused child snapshot failed " + builds);
                Agent? child = null;
                var failure = expected;
                var manager = new BrokenSnapshot(() => child?.InterruptState is not null, failure);
                child = new(new()
                {
                    Model = new Events(_ => Calls("ask-child", "ask", JsonSerializer.SerializeToElement(new { }))),
                    Tools = [ask],
                    ConversationManager = manager,
                    OwnedResources = [new Resource(() => closed++)]
                });
                return new(child);
            }
        });
        var hooks = new HookRegistry();
        hooks.Register<ToolResultEvent>((value, _) => { observed = value.Result; return default; });
        await using var parent = new Agent(new()
        {
            Model = new Events(request => HasResult(request) ? Answer("parent complete") : Calls("outer", tool.Spec.Name, input)),
            Hooks = hooks,
            Tools = [tool, ask]
        });
        for (var index = 1; index <= 2; index++)
        {
            var result = await parent.InvokeAsync("start a fresh call");
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Null(parent.InterruptState);
            Assert.Equal(index, builds);
            Assert.Equal(index, closed);
            Assert.NotNull(observed);
            Assert.Equal(ToolResultStatus.Error, observed.Status);
            Assert.Same(expected, observed.Error);
        }
    }

    [Theory]
    [InlineData("question")]
    [InlineData("input")]
    [InlineData("call")]
    public async Task ChangedSavedIdentityCannotBorrowThePausedTeam(string changed)
    {
        var builds = 0;
        var answered = 0;
        var closed = 0;
        var ask = Ask(() => answered++);
        var input = Team("a");
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) =>
            {
                builds++;
                return new(new Agent(new()
                {
                    Model = new Events(request => HasResult(request) ? Answer("child complete") : Calls("child-ask", "ask", JsonSerializer.SerializeToElement(new { }))),
                    Tools = [ask],
                    OwnedResources = [new Resource(() => closed++)]
                }));
            }
        });
        ToolResultBlock? failure = null;
        var hooks = new HookRegistry();
        hooks.Register<ToolResultEvent>((value, _) => { failure = value.Result; return default; });
        await using var parent = new Agent(new()
        {
            Model = new Events(request => HasResult(request) ? Answer("parent complete") : Calls("outer", tool.Spec.Name, input)),
            Tools = [tool, ask],
            Hooks = hooks
        });
        var first = await parent.InvokeAsync("start");
        Assert.Equal(StopReasons.Interrupt, first.StopReason);
        var saved = await parent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        var pending = parent.InterruptState!;
        var call = Assert.Single(pending.AssistantMessage.Content.OfType<ToolUseBlock>());
        var question = Assert.Single(pending.Interrupts);
        var changedCall = changed == "call" ? call with { ToolUseId = "different-call" }
            : changed == "input" ? call with { Input = JsonSerializer.SerializeToElement(new { task = "different", agents = new[] { new { name = "a", instructions = "Do." } } }) } : call;
        var changedQuestion = new Interrupt(changed == "question" ? "different-question" : question.Id, question.Name,
            changedCall.ToolUseId, question.Reason, question.Source, question.IsBatch);
        var message = new Message(Role.Assistant, [changedCall], pending.AssistantMessage.TrackingId, pending.AssistantMessage.Metadata);
        var altered = new InterruptState(message, [], [changedQuestion], []);
        var data = saved.Data.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        data["messages"] = JsonSerializer.SerializeToElement(parent.Messages.Take(parent.Messages.Count - 1).Concat([message]).ToArray(), Web);
        data["interruptState"] = JsonSerializer.SerializeToElement(altered, Web);
        await parent.LoadSnapshotAsync(new AgentSnapshot(data));
        var result = await parent.InvokeAsync(null, new()
        {
            InterruptResponses = [new(changedQuestion.Id, JsonSerializer.SerializeToElement("approved"))]
        });
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.NotNull(failure);
        Assert.Equal(ToolResultStatus.Error, failure.Status);
        Assert.Equal(1, builds);
        Assert.Equal(0, answered);
        Assert.Equal(0, closed);
        await parent.ShutdownAsync();
        Assert.Equal(1, closed);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static bool HasResult(ModelRequest request) => request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true;
    private static ITool Ask(Action? answered = null) => new FunctionTool(new("ask", "Ask", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
    {
        var response = context.Interrupt("approval", JsonSerializer.SerializeToElement("approve"));
        answered?.Invoke();
        return new(response.GetString() ?? "null");
    });
    private static ModelStreamEvent[] Calls(string id, string name, JsonElement input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new(id, name, input)),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)
    ];
    private sealed class BrokenSnapshot(Func<bool> armed, Exception failure) : ConversationManager
    {
        protected override void WriteState(IDictionary<string, object?> state)
        {
            if (armed()) throw failure;
        }
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) => default;
    }
}
