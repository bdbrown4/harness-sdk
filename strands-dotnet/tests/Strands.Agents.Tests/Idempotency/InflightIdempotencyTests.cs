using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.Idempotency;

public sealed class InflightIdempotencyTests
{
    [Fact]
    public async Task SameTokenJoinsOneGatedModelAndReturnsTheOriginalResult()
    {
        var entered = Signal(); var release = Signal(); var model = new GateModel(entered, release); var state = new Dictionary<string, object?> { ["request"] = 7 };
        await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeAsync("primary", new() { IdempotencyToken = "same", InvocationState = state }); Task<AgentResult>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeAsync("ignored duplicate", new() { IdempotencyToken = "same" }); Assert.False(duplicate.IsCompleted); }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(await primary, await duplicate!); Assert.Same(state, (await duplicate!).InvocationState); Assert.Equal(1, model.Calls); Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task PrimaryFailureIsSharedByReferenceWithoutRerunningTheModel()
    {
        var entered = Signal(); var release = Signal(); var expected = new IOException("primary model"); var model = new GateModel(entered, release, expected);
        await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeAsync("primary", new() { IdempotencyToken = "failure" }); Task<AgentResult>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeAsync("duplicate", new() { IdempotencyToken = "failure" }); }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => primary)); Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => duplicate!)); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task CancelingOneDuplicateDoesNotCancelThePrimaryOrAnotherWaiter()
    {
        var entered = Signal(); var release = Signal(); var model = new GateModel(entered, release); using var caller = new CancellationTokenSource();
        await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeAsync("primary", new() { IdempotencyToken = "wait" }); Task<AgentResult>? cancelled = null; Task<AgentResult>? surviving = null;
        try
        {
            await Ready(entered.Task); cancelled = agent.InvokeAsync("cancel this wait", new() { IdempotencyToken = "wait" }, caller.Token); surviving = agent.InvokeAsync("survive", new() { IdempotencyToken = "wait" });
            caller.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); Assert.Equal(caller.Token, error.CancellationToken); Assert.False(primary.IsCompleted); Assert.False(surviving.IsCompleted);
        }
        finally { release.TrySetResult(true); await Observe(primary); if (cancelled is not null) await Observe(cancelled); if (surviving is not null) await Observe(surviving); }
        Assert.Same(await primary, await surviving!); Assert.Equal(1, model.Calls); using var stillOwned = caller.Token.Register(() => { });
    }

    [Fact]
    public async Task EarlyPrimaryCloseJoinsCleanupBeforePublishingAbortedToDuplicate()
    {
        var cleanup = Signal(); var release = Signal(); var model = new PartialModel(cleanup, release);
        await using var agent = new Agent(new() { Model = model }); var iterator = agent.StreamAsync("primary", new() { IdempotencyToken = "partial" }).GetAsyncEnumerator(); Task? close = null; Task<AgentResult>? duplicate = null;
        try
        {
            while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) break;
            duplicate = agent.InvokeAsync("duplicate", new() { IdempotencyToken = "partial" }); close = iterator.DisposeAsync().AsTask(); await Ready(cleanup.Task); Assert.False(close.IsCompleted); Assert.False(duplicate.IsCompleted);
        }
        finally { release.TrySetResult(true); if (close is not null) await Observe(close); else await iterator.DisposeAsync(); if (duplicate is not null) await Observe(duplicate); }
        await close!; await Assert.ThrowsAsync<IdempotencyAbortedException>(() => duplicate!); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task StructuredDuplicatesUseOneAlreadyAcceptedReferenceValue()
    {
        var entered = Signal(); var release = Signal(); var model = new GateModel(entered, release, structured: true); var value = new Answer(7); var decoded = 0;
        var options = new StructuredOutputOptions<Answer> { Schema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"), Deserializer = _ => { decoded++; return value; } };
        await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeStructuredAsync("primary", options, new() { IdempotencyToken = "structured" }); Task<StructuredOutputResult<Answer>>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeStructuredAsync("duplicate", options, new() { IdempotencyToken = "structured" }); }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        var first = await primary; var second = await duplicate!; Assert.Same(first.Result, second.Result); Assert.Same(value, second.Output); Assert.Equal(1, decoded); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task AcceptedNullFromTheInterfaceContractIsDistinctFromMissingWithoutChangingDecoderPolicy()
    {
        var entered = Signal(); var release = Signal(); var model = new GateModel(entered, release, structured: true); var accepted = new AcceptedNullTool();
        await using var agent = new Agent(new() { Model = model });
        var primary = agent.InvokeAsync("primary", new() { IdempotencyToken = "null", StructuredOutputTool = accepted }); Task<StructuredOutputResult<string>>? duplicate = null;
        try
        {
            await Ready(entered.Task);
            duplicate = agent.InvokeStructuredAsync("duplicate", new StructuredOutputOptions<string> { Schema = accepted.Spec.InputSchema, Deserializer = _ => throw new InvalidOperationException("shared value must not decode") }, new() { IdempotencyToken = "null" });
        }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(await primary, (await duplicate!).Result); Assert.Null((await duplicate!).Output); Assert.Equal(1, accepted.Calls); Assert.Equal(1, model.Calls);
    }

    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Ready(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    internal static async Task Observe(Task task) { try { await task; } catch { } }
    public sealed record Answer(int Value);
    private sealed class AcceptedNullTool : IStructuredOutputTool
    {
        internal int Calls;
        public ToolSpec Spec { get; } = new(StructuredOutputTool<string>.ToolName, "Test an accepted nullable interface value", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"));
        public bool HasResult => Calls != 0;
        public object? Result => null;
        public bool TryGetResult(string toolUseId, out object? result) { result = null; return HasResult && toolUseId == "typed"; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { Calls++; return new("accepted by interface"); }
    }
    private sealed class GateModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, Exception? error = null, bool structured = false) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++; entered.TrySetResult(true); await release.Task; if (error is not null) throw error; yield return new ModelMessageStartEvent();
            yield return structured ? new ModelContentBlockStartEvent(0, new("typed", StructuredOutputTool<Answer?>.ToolName, JsonSerializer.Deserialize<JsonElement>("{}"))) : new ModelContentBlockStartEvent(0);
            yield return structured ? new ModelContentBlockDeltaEvent(0, ToolInput: "{}") : new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(structured ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
    private sealed class PartialModel(TaskCompletionSource<bool> cleanup, TaskCompletionSource<bool> release) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            try { yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "partial"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { cleanup.TrySetResult(true); await release.Task; }
        }
    }
}
