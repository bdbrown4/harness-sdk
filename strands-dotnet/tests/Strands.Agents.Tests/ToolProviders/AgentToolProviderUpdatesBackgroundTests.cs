using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesBackgroundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedBodyRetainsItsOriginalCapabilityAndCapturedValidatorAfterPublication(bool invalidInput)
    {
        var first = new GatedTool("first");
        var queued = new CapturedTool("queued");
        var sentinel = new GatedTool("sentinel");
        var provider = new AgentToolProviderUpdatesTests.UpdatingProvider([first, queued, sentinel]);
        await using var agent = new Agent(new()
        {
            Model = new CallsModel("first", "queued", "sentinel") { InvalidInput = invalidInput },
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            MaxParallelTools = 1,
            BackgroundTasks = new() { Always = ["*"], WaitForCompletion = false, MaxConcurrency = 1 },
        });
        try
        {
            Assert.Equal("dispatched", (await agent.InvokeAsync("run")).ToString());
            await Deadline(first.Entered.Task);
            var replacement = new CapturedTool("queued", "string");
            provider.Emit([first, replacement, sentinel]);
            Assert.Contains(replacement, agent.Tools);
            queued.RefuseSpec = true;
            first.Release.TrySetResult(true);
            // One physical scheduler slot: entering this last body proves the earlier queued body exited.
            await Deadline(sentinel.Entered.Task);
            Assert.Equal(invalidInput ? 0 : 1, queued.Calls);
            Assert.Equal(0, replacement.Calls);
            var listed = await agent.InvokeToolAsync(new("list", BackgroundTaskPolicy.ManageToolName,
                JsonSerializer.SerializeToElement(new { mode = "list" })));
            var completed = Assert.Single(Assert.Single(listed.ContentBlocks.OfType<JsonBlock>()).Json.GetProperty("tasks").EnumerateArray(), task => task.GetProperty("toolName").GetString() == "queued");
            Assert.Equal(invalidInput ? "failed" : "completed", completed.GetProperty("status").GetString());
            if (invalidInput)
            {
                var detail = await agent.InvokeToolAsync(new("get", BackgroundTaskPolicy.ManageToolName,
                    JsonSerializer.SerializeToElement(new { mode = "get", taskId = completed.GetProperty("taskId").GetString() })));
                Assert.Contains(detail.ContentBlocks, block => block is TextBlock text && text.Text.StartsWith("Input does not match the schema", StringComparison.Ordinal));
            }
        }
        finally { first.Release.TrySetResult(true); sentinel.Release.TrySetResult(true); }
    }

    [Fact]
    public async Task ShutdownHookFailureStillJoinsPhysicalBodiesBeforeClosingProviderSubscription()
    {
        var tool = new GatedTool("first");
        var provider = new AgentToolProviderUpdatesTests.UpdatingProvider([tool]);
        var agent = new Agent(new()
        {
            Model = new CallsModel("first"),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            BackgroundTasks = new() { Always = ["*"], WaitForCompletion = false },
        });
        var expected = new IOException("earlier shutdown hook");
        agent.Hooks.Register<ShutdownEvent>((_, _) => throw expected, int.MinValue);
        await agent.InvokeAsync("run");
        await Deadline(tool.Entered.Task);
        Task? closing = null;
        try
        {
            closing = agent.ShutdownAsync();
            Assert.False(closing.IsCompleted);
            Assert.Equal(0, provider.Lease.Disposals);
            Assert.Equal(0, provider.Removes);
        }
        finally { tool.Release.TrySetResult(true); }
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => closing!));
        Assert.Equal(1, provider.Lease.Disposals);
        Assert.Equal(1, provider.Removes);
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task RetryKeepsTheAdmittedBodyAndValidatorAfterItsFirstResultIsReplaced()
    {
        var tool = new CapturedTool("queued"); var replacement = new CapturedTool("queued", "string");
        var provider = new AgentToolProviderUpdatesTests.UpdatingProvider([tool]);
        var completed = new TaskCompletionSource<ToolResultBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = new Agent(new()
        {
            Model = new CallsModel("queued"),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            BackgroundTasks = new() { Always = ["*"], WaitForCompletion = false },
        });
        agent.Hooks.Register<AfterToolCallEvent>((boundary, _) =>
        {
            if (boundary.ToolUse.Name != "queued") return default;
            if (tool.Calls == 1)
            {
                provider.Emit([replacement]); tool.RefuseSpec = true; boundary.Retry = true;
            }
            else completed.TrySetResult(boundary.Result);
            return default;
        });
        await agent.InvokeAsync("run");
        var result = await Deadline(completed.Task);
        Assert.Equal(ToolResultStatus.Success, result.Status); Assert.Equal("original:7", result.Content);
        Assert.Equal(2, tool.Calls); Assert.Equal(0, replacement.Calls); Assert.Contains(replacement, agent.Tools);
    }

    [Fact]
    public async Task CancellationTerminalDoesNotReleaseMetadataWhileThePhysicalBodyStillRuns()
    {
        var tool = new GatedTool("first"); var replacement = new GatedTool("first");
        var provider = new AgentToolProviderUpdatesTests.UpdatingProvider([tool]);
        await using var agent = new Agent(new()
        {
            Model = new CallsModel("first"),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            BackgroundTasks = new() { Always = ["*"], WaitForCompletion = false },
        });
        Task? shutdown = null;
        try
        {
            await agent.InvokeAsync("run"); await Deadline(tool.Entered.Task);
            var taskId = Assert.Single(agent.State.Get("strands.backgroundTasks")!.Value.EnumerateArray()).GetProperty("taskId").GetString();
            await agent.InvokeToolAsync(new("cancel", BackgroundTaskPolicy.ManageToolName,
                JsonSerializer.SerializeToElement(new { mode = "cancel", taskId })));
            Assert.Equal("cancelled", Assert.Single(agent.State.Get("strands.backgroundTasks")!.Value.EnumerateArray()).GetProperty("status").GetString());
            provider.Emit([replacement]); tool.RefuseSpec = true;
            shutdown = agent.ShutdownAsync();
            Assert.False(shutdown.IsCompleted); Assert.False(tool.Exited.Task.IsCompleted);
            Assert.Equal(0, provider.Lease.Disposals); Assert.Equal(0, provider.Removes);
        }
        finally { tool.Release.TrySetResult(true); }
        await Deadline(shutdown!); Assert.True(tool.Exited.Task.IsCompleted);
        Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes);
        Assert.Contains(replacement, agent.Tools);
    }

    [Fact]
    public async Task InputRequiredResumeRetainsTheOldReferenceAndValidatorAfterAnIdlePublication()
    {
        var tool = new QuestionTool(); var replacement = new CapturedTool("queued", "string");
        var provider = new AgentToolProviderUpdatesTests.UpdatingProvider([tool]);
        await using var agent = new Agent(new()
        {
            Model = new CallsModel("queued"),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            BackgroundTasks = new() { Always = ["*"], WaitForCompletion = true },
        });
        var paused = await agent.InvokeAsync("run");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Null(agent.InterruptState);
        var question = Assert.Single(paused.Interrupts);
        provider.Emit([replacement]); tool.RefuseSpec = true;
        Assert.Contains(replacement, agent.Tools);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement("accepted"))] });
        Assert.Equal("dispatched", result.ToString()); Assert.Equal(2, tool.Calls); Assert.Equal(0, replacement.Calls);
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    private sealed class CapturedTool(string name, string type = "integer") : ITool
    {
        private readonly ToolSpec _spec = new(name, "captured input", JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { value = new { type } }, required = new[] { "value" }, additionalProperties = false }));
        internal bool RefuseSpec;
        internal int Calls;
        public ToolSpec Spec => RefuseSpec ? throw new InvalidOperationException("Retired Spec was reread.") : _spec;
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return new("original:" + input.GetProperty("value").GetInt32()); }
    }

    private sealed class GatedTool(string name) : ITool
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal TaskCompletionSource<bool> Exited { get; } = Signal();
        internal bool RefuseSpec;
        private readonly ToolSpec _spec = new(name, "physical body", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ToolSpec Spec => RefuseSpec ? throw new InvalidOperationException("Retired physical body's Spec was reread.") : _spec;
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); try { await Release.Task; return "finished"; } finally { Exited.TrySetResult(true); } }
    }

    private sealed class QuestionTool : ITool
    {
        private readonly ToolSpec _spec = new("queued", "approval", JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } }));
        internal bool RefuseSpec; internal int Calls;
        public ToolSpec Spec => RefuseSpec ? throw new InvalidOperationException("Paused Spec was reread.") : _spec;
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return new(context.Interrupt("approve").GetString()!); }
    }

    private sealed class CallsModel(params string[] names) : IModel
    {
        private int _calls;
        internal bool InvalidInput { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                for (var index = 0; index < names.Length; index++)
                {
                    yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, names[index], JsonSerializer.SerializeToElement(new { })));
                    yield return new ModelContentBlockDeltaEvent(index, ToolInput: names[index] == "queued" ? InvalidInput ? "{\"value\":\"invalid\"}" : "{\"value\":7}" : "{}");
                    yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("dispatched") };
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Deadline(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static async Task<T> Deadline<T>(Task<T> task)
    { await Deadline((Task)task); return await task; }
}
