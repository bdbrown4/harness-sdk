using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Tasks;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class BackgroundTasksCompatibilityTests
{
    [Fact]
    public async Task RealNativeBackgroundToolPausesAndResumesOnTheSelectedAsset()
    {
        var calls = 0;
        var caller = new Dictionary<string, object?> { ["tenant"] = "portable" };
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })), (input, context, _) =>
        {
            Assert.Same(caller, context.InvocationState); Assert.False(input.TryGetProperty("_background_execution", out var ignored));
            Interlocked.Increment(ref calls);
            return new ValueTask<string>(context.Interrupt("approve").GetString()!);
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool }, BackgroundTasks = new() });
        var paused = await Bounded(agent.InvokeAsync("go", new() { InvocationState = caller }));
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Null(agent.InterruptState);
        var answer = new InterruptResponse(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement("yes"));
        var result = await Bounded(agent.InvokeAsync(null, new() { InvocationState = caller, InterruptResponses = new[] { answer } }));
        Assert.Equal("received", result.ToString()); Assert.Equal(2, calls); Assert.Single(caller);
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    [Fact]
    public async Task LogicalCancellationCannotReleaseAnUncooperativePhysicalSlot()
    {
        var finish = new TaskCompletionSource<BackgroundTaskOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new BackgroundTaskScheduler(1, null, execution =>
        {
            if (execution.ToolUseId == "first") { started.TrySetResult(true); return finish.Task; }
            next.TrySetResult(true); return Task.FromResult(new BackgroundTaskOutcome(BackgroundTaskStatus.Completed, new ToolResultBlock(execution.ToolUseId, "next")));
        });
        var first = scheduler.Submit("first", "work");
        try
        {
            await Bounded(started.Task);
            scheduler.Submit("second", "work"); scheduler.Cancel(first.TaskId);
            Assert.Equal(BackgroundTaskStatus.Cancelled, (await Bounded(scheduler.WaitAsync(first.TaskId))).Status);
            Assert.False(next.Task.IsCompleted); Assert.False(scheduler.WaitForIdleAsync().IsCompleted);
        }
        finally { finish.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("first", "late"))); await Bounded(scheduler.WaitForIdleAsync()); }
        Assert.True(next.Task.IsCompleted); Assert.Null(scheduler.Get(first.TaskId)!.Result);
    }

    [Fact]
    public async Task PersistedRunningWorkRecoversAsFailureWithoutExecutingItAgain()
    {
        var state = new StateStore();
        var now = DateTimeOffset.UtcNow;
        state.Set("strands.backgroundTasks", new[] { new { taskId = "saved-task", toolUseId = "old-call", toolName = "work", status = "working", createdAt = now, lastUpdatedAt = now } });
        var calls = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Interlocked.Increment(ref calls); return new ValueTask<string>("must not execute"); });
        await using var agent = new Agent(new() { Model = new Model(alreadyStarted: true), Tools = new[] { tool }, BackgroundTasks = new(), State = state.GetAll() });
        var result = await Bounded(agent.InvokeAsync(null));
        Assert.Equal("received", result.ToString()); Assert.Equal(0, calls);
        var delivery = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        var metadata = Assert.IsType<JsonBlock>(delivery.ContentBlocks[0]).Json;
        Assert.Equal("failed", metadata.GetProperty("status").GetString());
        Assert.Equal("execution_error", metadata.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("cannot resume", metadata.GetProperty("error").GetProperty("message").GetString());
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    [Fact]
    public async Task RealFileSessionRetainsDetachedAdmissionAndRecoversItWithoutReexecution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-background-" + Guid.NewGuid().ToString("N"));
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) => { Interlocked.Increment(ref calls); await finish.Task; return "original body"; });
        var store = new FileSessionStore(directory);
        var first = new Agent(new() { Model = new Model(), Tools = new[] { tool }, BackgroundTasks = new() { WaitForCompletion = false }, SessionStore = store, SessionId = "background-session" });
        try
        {
            try { await Bounded(first.InvokeAsync("go")); }
            finally { finish.TrySetResult(true); await first.DisposeAsync(); }
            var snapshot = await store.LoadAsync("background-session");
            Assert.NotNull(snapshot); Assert.NotNull(snapshot.State);
            Assert.True(snapshot.State.ContainsKey("strands.backgroundTasks"));
            await using var resumed = new Agent(new() { Model = new Model(alreadyStarted: true), Tools = new[] { tool }, BackgroundTasks = new(), SessionStore = store, SessionId = "background-session" });
            Assert.Equal("received", (await Bounded(resumed.InvokeAsync(null))).ToString());
            Assert.InRange(calls, 0, 1);
            var metadata = resumed.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().SelectMany(result => result.ContentBlocks).OfType<JsonBlock>()
                .Single(block => block.Json.TryGetProperty("taskId", out var ignored));
            Assert.Equal("failed", metadata.Json.GetProperty("status").GetString());
            Assert.Contains("cannot resume", metadata.Json.GetProperty("error").GetProperty("message").GetString());
        }
        finally { finish.TrySetResult(true); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HarnessDefaultAndPortableFalseUseTheRealCoreBackgroundComponent()
    {
        var options = new HarnessOptions
        {
            Model = new Model(),
            BuiltinTools = Array.Empty<string>(),
            BuiltinPlugins = Array.Empty<string>(),
            Context = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false }
        };
        await using var enabled = await HarnessFactory.CreateAsync(options);
        Assert.Contains(enabled.Tools, tool => tool.Spec.Name == "strands_manage_background_task");
        await using var disabled = await HarnessFactory.CreateAsync(options with { BackgroundTasks = null });
        Assert.Empty(disabled.Tools);
        var definition = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false,\"agentConfig\":{\"backgroundTasks\":false}}");
        await using var portable = await HarnessFactory.CreateAsync(definition.ToOptions(new() { ModelResolver = _ => new Model() }));
        Assert.Empty(portable.Tools);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task IncompleteTerminalCheckpointCannotChangeHistory(string status)
    {
        await using var agent = new Agent(new() { Model = new Model(), BackgroundTasks = new(), Messages = new[] { Message.User("original") } });
        agent.State.Set("value", 1);
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("changed") }),
            ["state"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["value"] = 2, ["strands.backgroundTasks"] = new[] { new { taskId = "invalid", toolUseId = "call", toolName = "work", status, createdAt = DateTimeOffset.UtcNow, lastUpdatedAt = DateTimeOffset.UtcNow } } })
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(snapshot).AsTask());
        Assert.Equal("original", Assert.Single(agent.Messages).Text); Assert.Equal(1, agent.State.Get<int>("value"));
    }

    private static async Task<T> Bounded<T>(Task<T> task) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await TaskCancellation.WaitAsync(task, deadline.Token); return await task; }
    private static async Task Bounded(Task task) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await TaskCancellation.WaitAsync(task, deadline.Token); }
    private sealed class Model(bool alreadyStarted = false) : IModel
    {
        private bool _started = alreadyStarted;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var delivered = request.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(call => call.Name == "strands_manage_background_task");
            yield return new ModelMessageStartEvent();
            if (!_started)
            {
                _started = true;
                yield return new ModelContentBlockStartEvent(0, new("call", "work", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{\"_background_execution\":true}");
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: delivered ? "received" : "waiting"); }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(_started && !delivered && request.Messages.All(message => !message.Content.OfType<ToolUseBlock>().Any()) ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }
}
