using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions;

namespace Strands.Agents.Tests;

public sealed class DirectToolTests
{
    [Fact]
    public async Task DirectCallsHonorHookSelectionInputAndResultsWithoutAddingHistory()
    {
        var original = Tool("original", (_, _, _) => throw new InvalidOperationException("Hook must select the replacement."));
        var replacement = Tool("replacement", (input, context, _) =>
        {
            Assert.Equal(3, input.GetProperty("value").GetInt32());
            ((IDictionary<string, object?>)context.InvocationState)["inside"] = true;
            return new("body");
        });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { original } });
        using var before = agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { value.SelectedTool = replacement; value.ToolUse = new("changed-id", "replacement", JsonSerializer.SerializeToElement(new { value = 3 })); return default; });
        using var after = agent.Hooks.Register<AfterToolCallEvent>((value, _) => { Assert.Equal("correlation", value.Result.ToolUseId); value.Result = new("changed-again", value.Result.Content + " then after"); return default; });
        var state = new Dictionary<string, object?> { ["principal"] = "user" };
        var result = await agent.InvokeToolAsync(Call("original"), state);
        Assert.Equal("correlation", result.ToolUseId);
        Assert.Equal("body then after", result.Content);
        Assert.False(state.ContainsKey("inside"));
        Assert.Empty(agent.Messages);
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("schema")]
    [InlineData("body")]
    public async Task UnknownInvalidAndFailingToolsProduceCorrelatedErrors(string failure)
    {
        var calls = 0;
        var effects = 0;
        var tool = Tool("target", (_, _, _) => { effects++; throw new IOException("body failed"); }, JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "needed" } }));
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        using var after = agent.Hooks.Register<AfterToolCallEvent>((value, _) => { calls++; Assert.Equal(ToolResultStatus.Error, value.Result.Status); return default; });
        var call = Call(failure == "unknown" ? "missing" : "target") with { Input = failure == "body" ? JsonSerializer.SerializeToElement(new { needed = true }) : JsonSerializer.SerializeToElement(new { }) };
        var result = await agent.InvokeToolAsync(call);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("correlation", result.ToolUseId);
        Assert.Equal(1, calls);
        Assert.Equal(failure == "body" ? 1 : 0, effects);
        Assert.Empty(agent.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanApprovalCannotBeSwallowedByProceedOrAnApplicationHook(bool applicationHook)
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { Tool("target", (_, _, _) => { effects++; return new("effect"); }) }, Interventions = applicationHook ? Array.Empty<InterventionHandler>() : new InterventionHandler[] { new Confirmer() } });
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            if (applicationHook) { try { value.Interrupt("cannot-prompt"); } catch (InvalidOperationException) { } }
            return default;
        });
        var result = await agent.InvokeToolAsync(Call("target"));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("cannot prompt", result.Content);
        Assert.Equal(0, effects);
        Assert.Empty(agent.Messages);
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreemptiveAnswersAreEvaluatedWithoutTouchingAPendingBatch(bool answer)
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { Tool("target", (_, _, _) => { effects++; return new("effect"); }) }, Interventions = new InterventionHandler[] { new Confirmer(JsonSerializer.SerializeToElement(answer)) } });
        var result = await agent.InvokeToolAsync(Call("target"));
        Assert.Equal(answer ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        Assert.Equal(answer ? 1 : 0, effects);
        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task ADirectCallInsideAParentToolPreservesTheBatchAndInvocationGuard()
    {
        Agent? observed = null;
        var effects = 0;
        var inner = Tool("inner", (_, _, _) => { effects++; return new("inside"); });
        var outer = Tool("outer", async (_, context, token) =>
        {
            observed = context.Agent;
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => context.Agent.InvokeAsync("recursive", cancellationToken: token));
            var result = await context.Agent.InvokeToolAsync(new("direct-id", "inner", JsonSerializer.SerializeToElement(new { })), context.InvocationState, token);
            return result.Content;
        });
        await using var agent = new Agent(new() { Model = new Model("outer"), Tools = new[] { outer, inner } });
        var result = await agent.InvokeAsync("hello");
        Assert.Same(agent, observed);
        Assert.Equal("done", result.ToString());
        Assert.Equal(1, effects);
        Assert.Equal(4, agent.Messages.Count);
        Assert.Single(agent.Messages.SelectMany(m => m.Content).OfType<ToolUseBlock>());
        Assert.Single(agent.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>());
        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task ActiveDirectCallsPreventDisposalRegistrationAndAnotherModelInvocation()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = Tool("target", async (_, _, token) => { started.TrySetResult(true); await release.Task; token.ThrowIfCancellationRequested(); return "released"; });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        var call = agent.InvokeToolAsync(Call("target")).AsTask();
        try
        {
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Throws<InvalidOperationException>(() => agent.DisposeAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
            Assert.Throws<InvalidOperationException>(() => agent.AddTool(Tool("other", (_, _, _) => new("unused"))));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("racing"));
        }
        finally { release.TrySetResult(true); await call; }
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("after")).StopReason);
    }

    [Fact]
    public async Task CancellationAwaitsTheBodyAndAfterHookThenReleasesTheLifetimeGuard()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = 0;
        using var cancellation = new CancellationTokenSource();
        var tool = Tool("target", async (_, _, token) => { started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unreachable"; });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        using var hook = agent.Hooks.Register<AfterToolCallEvent>((value, token) => { Assert.False(token.IsCancellationRequested); Assert.Equal(ToolResultStatus.Error, value.Result.Status); after++; return default; });
        var call = agent.InvokeToolAsync(Call("target"), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, after);
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("after")).StopReason);
    }

    [Fact]
    public async Task CancellationRemainsPrimaryWhenTheAfterHookAlsoFails()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tool = Tool("target", async (_, _, token) => { started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unreachable"; });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        var cleanup = new IOException("after failed");
        using var hook = agent.Hooks.Register<AfterToolCallEvent>((_, _) => throw cleanup);
        var call = agent.InvokeToolAsync(Call("target"), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task NestedRefusedApprovalDoesNotPauseTheParentsRealToolBatch()
    {
        var effects = 0;
        var inner = Tool("inner", (_, _, _) => { effects++; return new("effect"); });
        var outer = Tool("outer", async (_, context, token) => (await context.Agent.InvokeToolAsync(new("nested", "inner", JsonSerializer.SerializeToElement(new { })), context.InvocationState, token)).Content);
        await using var agent = new Agent(new() { Model = new Model("outer"), Tools = new[] { outer, inner }, Interventions = new InterventionHandler[] { new InnerConfirmer() } });
        var result = await agent.InvokeAsync("hello");
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(0, effects);
        Assert.Null(agent.InterruptState);
        Assert.Equal(4, agent.Messages.Count);
        Assert.Contains("cannot prompt", Assert.Single(agent.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task AToolCannotSwallowItsOwnDirectInterruptAndReportSuccess()
    {
        var tool = Tool("target", (_, context, _) => { try { context.Interrupt("permission"); } catch (InvalidOperationException) { } return new("pretended success"); });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        var result = await agent.InvokeToolAsync(Call("target"));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("cannot prompt", result.Content);
        Assert.Empty(agent.Messages);
        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task AnUndefinedPreemptiveResponseCannotBypassDirectApproval()
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { Tool("target", (_, _, _) => { effects++; return new("effect"); }) } });
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            try { value.Interrupt("permission", response: default(JsonElement)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            return default;
        });
        var result = await agent.InvokeToolAsync(Call("target"));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(0, effects);
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("disposed-reason")]
    public async Task MalformedApprovalCannotBeSwallowedToAuthorizeTheBody(string malformed)
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { Tool("target", (_, _, _) => { effects++; return new("effect"); }) } });
        using var document = JsonDocument.Parse("{\"reason\":true}");
        var disposedReason = document.RootElement;
        document.Dispose();
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            try { value.Interrupt(malformed == "blank" ? " " : "permission", malformed == "disposed-reason" ? disposedReason : null); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            return default;
        });
        var result = await agent.InvokeToolAsync(Call("target"));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(0, effects);
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveDirectCallsPreventCapturingOrRestoringCheckpoints(bool restore)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = Tool("target", async (_, _, _) => { started.TrySetResult(true); await release.Task; return "released"; });
        await using var agent = new Agent(new() { Model = new Model(), Tools = new[] { tool } });
        var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State });
        var call = agent.InvokeToolAsync(Call("target")).AsTask();
        try
        {
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            if (restore) await Assert.ThrowsAsync<InvalidOperationException>(() => agent.LoadSnapshotAsync(snapshot).AsTask());
            else await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State }).AsTask());
        }
        finally { release.TrySetResult(true); await call; }
        await agent.LoadSnapshotAsync(snapshot);
        Assert.Empty((await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State })).Data["state"].EnumerateObject());
    }

    private static ToolUseBlock Call(string name) => new("correlation", name, JsonSerializer.SerializeToElement(new { }));
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback, JsonElement? schema = null) => new(new(name, "Direct tool", schema ?? JsonSerializer.SerializeToElement(new { type = "object" })), callback);
    private sealed class Confirmer(JsonElement? response = null) : InterventionHandler
    {
        public override string Name => "confirm";
        public override InterventionErrorPolicy OnError => InterventionErrorPolicy.Proceed;
        public override ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(new Confirm("permission", response: response));
    }
    private sealed class InnerConfirmer : InterventionHandler
    {
        public override string Name => "inner-confirm";
        public override InterventionErrorPolicy OnError => InterventionErrorPolicy.Proceed;
        public override ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(value.ToolUse.Name == "inner" ? new Confirm("permission") : new Proceed());
    }
    private sealed class Model(string? tool = null) : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent();
            if (tool is not null && Interlocked.Increment(ref _calls) == 1) { yield return new ModelContentBlockStartEvent(0, Call(tool)); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse); }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
        }
    }
}
