using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.Tasks;

public sealed class BackgroundTaskAgentTests
{
    [Fact]
    public async Task DispatchRunsThroughMiddlewareAndDeliversACorrelatedRichResultBeforeReturning()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "work", "{\"_background_execution\":true}")), AgentTests.Text("dispatched"), AgentTests.Text("received"));
        var middleware = 0; var before = 0; var after = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })), async (input, context, ct) =>
        { Assert.False(input.TryGetProperty(BackgroundTaskPolicy.SelectionProperty, out _)); await TaskCancellation.WaitAsync(finish.Task, ct); return "real result"; });
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, BackgroundTasks = new() });
        agent.Hooks.Register<BeforeToolCallEvent>((_, _) => { Interlocked.Increment(ref before); return default; });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { Interlocked.Increment(ref after); return default; });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { if (model.Requests.Count == 1) finish.TrySetResult(true); return default; });
        agent.Middleware.RegisterInput(Strands.Agents.Middleware.MiddlewareStages.ExecuteTool, (context, _) => { Interlocked.Increment(ref middleware); return new ValueTask<Strands.Agents.Middleware.ExecuteToolContext>(context); });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentResult result;
        try { result = await agent.InvokeAsync("go", cancellationToken: deadline.Token); }
        finally { finish.TrySetResult(true); }
        Assert.Equal("received", result.ToString());
        Assert.Equal(1, middleware); Assert.Equal(1, before); Assert.Equal(1, after);
        var acknowledgement = model.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Single(value => value.ToolUseId == "call");
        Assert.Contains("Background task dispatched.", acknowledgement.Content);
        var delivery = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), value => value.Name == BackgroundTaskPolicy.ManageToolName);
        var actual = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == delivery.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, actual.Status);
        Assert.Contains(actual.ContentBlocks, value => value is TextBlock { Text: "real result" });
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    [Fact]
    public async Task TaskLocalBodyInterruptResumesWithoutAPendingForegroundBatch()
    {
        var bodies = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (input, context, ct) =>
        {
            Interlocked.Increment(ref bodies);
            var answer = context.Interrupt("approve", JsonSerializer.SerializeToElement("proceed?"));
            return new ValueTask<string>(answer.GetString()!);
        });
        await using var agent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() });
        var paused = await agent.InvokeAsync("go").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        Assert.Null(agent.InterruptState);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("received", result.ToString());
        Assert.Equal(2, bodies);
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddlewareInterruptResumesAndRetainsACompletedBody(bool afterBody)
    {
        var bodies = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Interlocked.Increment(ref bodies); return new ValueTask<string>("side effect"); });
        await using var agent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() });
        agent.Middleware.RegisterWrap(MiddlewareStages.ExecuteTool, Approve);
        var paused = await agent.InvokeAsync("go").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        Assert.Equal(afterBody ? 1 : 0, bodies);
        var response = new InterruptResponse(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true));
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = new[] { response } }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("received", result.ToString()); Assert.Equal(1, bodies);

        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Approve(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!afterBody) context.Interrupt("middleware", JsonSerializer.SerializeToElement("before"));
            IMiddlewareEvent<ExecuteToolResult>? completed = null;
            await foreach (var value in next(context).WithCancellation(cancellationToken)) completed = value;
            if (afterBody) context.Interrupt("middleware", JsonSerializer.SerializeToElement("after"));
            yield return completed!;
        }
    }

    [Fact]
    public async Task BeforeToolApprovalStaysForegroundAndCanBeAbortedBeforeAdmission()
    {
        var bodies = 0;
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Interlocked.Increment(ref bodies); return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() });
        agent.Hooks.Register<BeforeToolCallEvent>((context, _) => { if (context.ToolUse.Name == "work") context.Interrupt("before"); return default; });
        var result = await agent.InvokeAsync("go").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReasons.Interrupt, result.StopReason); Assert.NotNull(agent.InterruptState);
        Assert.Null(agent.State.Get("strands.backgroundTasks")); Assert.Equal(0, bodies);
        Assert.True(await agent.AbortInterruptsAsync("denied"));
        Assert.Null(agent.InterruptState); Assert.Equal(0, bodies);
    }

    [Fact]
    public async Task DeliveryHookFailureKeepsBothMessagesAndCannotRedeliverOrRerunTheBody()
    {
        var bodies = 0; var fail = true;
        var expected = new InvalidOperationException("delivery callback");
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Interlocked.Increment(ref bodies); return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() });
        agent.Hooks.Register<MessageAddedEvent>((context, _) =>
        {
            if (fail && context.Message.Content.OfType<ToolResultBlock>().Any(value => value.ToolUseId != "call")) { fail = false; throw expected; }
            return default;
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go"));
        Assert.Same(expected, error);
        var delivered = agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Where(value => value.Name == BackgroundTaskPolicy.ManageToolName).ToArray();
        Assert.Single(delivered); Assert.Null(agent.State.Get("strands.backgroundTasks"));
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.ToolUseId == delivered[0].ToolUseId);
        Assert.Equal("received", (await agent.InvokeAsync(null)).ToString());
        Assert.Equal(1, bodies);
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), value => value.Name == BackgroundTaskPolicy.ManageToolName);
    }

    internal sealed class DynamicModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            var delivered = request.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(value => value.Name == BackgroundTaskPolicy.ManageToolName);
            foreach (var item in first ? AgentTests.Call(("call", "work", "{\"_background_execution\":true}")) : AgentTests.Text(delivered ? "received" : "waiting"))
            { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
    }

    [Theory]
    [InlineData("{\"status\":\"invalid\"}")]
    [InlineData("{\"status\":\"completed\"}")]
    [InlineData("{\"status\":\"failed\"}")]
    [InlineData("{\"status\":\"failed\",\"error\":{\"type\":999,\"message\":\"invalid\"}}")]
    [InlineData("{\"status\":\"working\",\"result\":{\"toolUseId\":\"old-call\",\"content\":\"done\",\"status\":\"success\"}}")]
    [InlineData("{\"status\":\"queued\",\"error\":{\"type\":\"execution_error\",\"message\":\"stale\"}}")]
    public async Task MalformedBackgroundCheckpointFailsBeforeChangingAnyAgentState(string variant)
    {
        await using var agent = new Agent(new() { Model = new DynamicModel(), BackgroundTasks = new(), Messages = new[] { Message.User("original") } });
        agent.State.Set("value", 1);
        var task = System.Text.Json.Nodes.JsonNode.Parse("{\"taskId\":\"bad\",\"toolUseId\":\"old-call\",\"toolName\":\"work\",\"createdAt\":\"2026-10-20T00:00:00Z\",\"lastUpdatedAt\":\"2026-10-20T00:00:00Z\"}")!.AsObject();
        foreach (var field in System.Text.Json.Nodes.JsonNode.Parse(variant)!.AsObject()) task[field.Key] = field.Value?.DeepClone();
        var invalid = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") }),
            ["state"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["value"] = 2, ["strands.backgroundTasks"] = new[] { task } })
        });
        await Assert.ThrowsAnyAsync<Exception>(() => agent.LoadSnapshotAsync(invalid).AsTask());
        Assert.Equal("original", Assert.Single(agent.Messages).Text);
        Assert.Equal(1, agent.State.Get<int>("value"));
    }

    [Fact]
    public async Task DetachedTasksRemainManageableAndPreventCheckpointReplacementUntilDelivered()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) =>
        { started.TrySetResult(true); await finish.Task; return "late result"; });
        await using var agent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() { WaitForCompletion = false } });
        try
        {
            Assert.Equal("waiting", (await agent.InvokeAsync("go").WaitAsync(TimeSpan.FromSeconds(5))).ToString());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var checkpoint = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.LoadSnapshotAsync(checkpoint).AsTask());
            var listed = await agent.InvokeToolAsync(new("manage", BackgroundTaskPolicy.ManageToolName, JsonSerializer.SerializeToElement(new { mode = "list" })));
            var tasks = Assert.IsType<JsonBlock>(Assert.Single(listed.ContentBlocks)).Json.GetProperty("tasks");
            var task = Assert.Single(tasks.EnumerateArray());
            var id = task.GetProperty("taskId").GetString();
            var cancelled = await agent.InvokeToolAsync(new("cancel", BackgroundTaskPolicy.ManageToolName, JsonSerializer.SerializeToElement(new { mode = "cancel", taskId = id })));
            Assert.Equal("cancelled", Assert.IsType<JsonBlock>(Assert.Single(cancelled.ContentBlocks)).Json.GetProperty("status").GetString());
            // Logical settlement still prevents replacement while its retained record awaits model delivery.
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.LoadSnapshotAsync(checkpoint).AsTask());
            Assert.Equal("received", (await agent.InvokeAsync(null).WaitAsync(TimeSpan.FromSeconds(5))).ToString());
            Assert.Null(agent.State.Get("strands.backgroundTasks"));
        }
        finally { finish.TrySetResult(true); }
    }

    [Fact]
    public async Task CallerCancellationStopsWaitingButDoesNotCancelAcceptedBackgroundWork()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, ct) =>
        { started.TrySetResult(ct); await TaskCancellation.WaitAsync(finish.Task, ct); return "late result"; });
        var model = new DynamicModel();
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, BackgroundTasks = new() });
        agent.Hooks.Register<BeforeModelCallEvent>((context, _) => { if (context.Request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Any(value => value.ToolUseId == "call")) cancellation.Cancel(); return default; });
        try
        {
            var result = await agent.InvokeAsync("go", cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StopReasons.Cancelled, result.StopReason);
            Assert.False((await started.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsCancellationRequested);
        }
        finally { finish.TrySetResult(true); }
    }

    [Fact]
    public async Task FreshChildDoesNotRecoverOrDeliverItsParentsLiveTasks()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) => { await finish.Task; return "parent only"; });
        await using var parent = new Agent(new() { Model = new DynamicModel(), Tools = new[] { tool }, BackgroundTasks = new() { WaitForCompletion = false } });
        parent.State.Set("application", 42);
        try
        {
            await parent.InvokeAsync("go").WaitAsync(TimeSpan.FromSeconds(5));
            await using var child = parent.CreateChild(options => options with { Model = new AgentTests.ScriptedModel(AgentTests.Text("child")) });
            await child.InitializeAsync();
            Assert.Equal(42, child.State.Get<int>("application"));
            Assert.Null(child.State.Get("strands.backgroundTasks"));
            Assert.Equal("child", (await child.InvokeAsync(null)).ToString());
            Assert.Empty(child.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
        }
        finally { finish.TrySetResult(true); }
    }

    [Fact]
    public async Task RecoveredRecordsDoNotPretendToBeLiveExecutionsWhenLoadingAnotherCheckpoint()
    {
        var state = new StateStore();
        state.Set("strands.backgroundTasks", new[] { new { taskId = "restored", toolUseId = "old-call", toolName = "work", status = "working", createdAt = DateTimeOffset.UtcNow, lastUpdatedAt = DateTimeOffset.UtcNow } });
        await using var agent = new Agent(new() { Model = new DynamicModel(), BackgroundTasks = new(), State = state.GetAll() });
        await agent.InitializeAsync();
        var replacement = new AgentSnapshot(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") }), ["state"] = JsonSerializer.SerializeToElement(new { value = 42 }) });
        await agent.LoadSnapshotAsync(replacement);
        Assert.Equal("replacement", Assert.Single(agent.Messages).Text);
        Assert.Equal(42, agent.State.Get<int>("value"));
        Assert.Null(agent.State.Get("strands.backgroundTasks"));
    }
}
