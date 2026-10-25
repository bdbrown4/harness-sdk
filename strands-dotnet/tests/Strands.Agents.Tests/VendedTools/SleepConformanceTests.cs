using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class SleepConformanceTests
{
    [Theory]
    [InlineData("name-empty")]
    [InlineData("name-whitespace")]
    [InlineData("name-invalid")]
    [InlineData("name-long")]
    [InlineData("description-empty")]
    [InlineData("description-whitespace")]
    public void InvalidFactoryMetadataUsesOrdinaryToolSpecValidation(string condition)
    {
        var options = condition switch
        {
            "name-empty" => new SleepOptions { Name = "" },
            "name-whitespace" => new SleepOptions { Name = " " },
            "name-invalid" => new SleepOptions { Name = "sleep.tool" },
            "name-long" => new SleepOptions { Name = new string('a', 65) },
            "description-empty" => new SleepOptions { Description = "" },
            _ => new SleepOptions { Description = " " },
        };
        Assert.ThrowsAny<ArgumentException>(() => Sleep.Create(options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"duration\":null}")]
    [InlineData("{\"duration\":false}")]
    [InlineData("{\"duration\":\"0\"}")]
    [InlineData("{\"duration\":[]}")]
    [InlineData("{\"duration\":{}}")]
    [InlineData("{\"duration\":-1}")]
    [InlineData("{\"duration\":61}")]
    [InlineData("{\"duration\":1e400}")]
    public async Task InvalidDirectInputIsRejectedWithoutAdmittingAnAsynchronousWait(string raw)
    {
        using var input = JsonDocument.Parse(raw);
        var operation = Sleep.Create().InvokeAsync(input.RootElement, Context()).AsTask();
        Assert.True(operation.IsCompleted);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await operation);
    }

    [Fact]
    public async Task UndefinedInputAndNullContextHavePredictableValidation()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Sleep.Create().InvokeAsync(default, Context()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Sleep.Create().InvokeAsync(Input(0), null!));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("signal")]
    [InlineData("both")]
    public async Task PreCancelledZeroUsesAnOriginalTokenBeforeReturningSuccess(string source)
    {
        using var operation = new CancellationTokenSource();
        using var signal = new CancellationTokenSource();
        if (source is "operation" or "both") operation.Cancel();
        if (source is "signal" or "both") signal.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Sleep.Create().InvokeAsync(Input(0), Context(signal.Token), operation.Token));
        Assert.Equal(source == "signal" ? signal.Token : operation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("distinct-operation")]
    [InlineData("distinct-signal")]
    public async Task SameAndDistinctTokensRemainAttributableAndPhysicallySettle(string source)
    {
        using var operation = new CancellationTokenSource();
        using var signal = new CancellationTokenSource();
        var selectedSignal = source == "same" ? operation.Token : signal.Token;
        var pending = Sleep.Create().InvokeAsync(Input(30), Context(selectedSignal), operation.Token).AsTask();
        var admitted = !pending.IsCompleted;
        if (source == "distinct-signal") signal.Cancel();
        else operation.Cancel();
        var observed = await Record.ExceptionAsync(async () => await Join(pending));
        Assert.True(admitted);
        var error = Assert.IsAssignableFrom<OperationCanceledException>(observed);
        Assert.Equal(source == "distinct-signal" ? signal.Token : operation.Token, error.CancellationToken);
        Assert.True(pending.IsCompleted);
        // After settlement, sources remain application-owned and usable independently.
        operation.Cancel();
        signal.Cancel();
    }

    [Fact]
    public async Task UncancellableFractionalDelayUsesInvariantResultRendering()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("Slept for 0.00125 seconds", await Sleep.Create().InvokeAsync(Input(0.00125), Context()));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public async Task MaximumNativeTimeSpanAdmitsPortableFirstChunkWithoutOverflowOrClamping()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = Sleep.Create(new() { MaximumDuration = TimeSpan.MaxValue });
        Assert.Equal(TimeSpan.MaxValue.TotalSeconds, tool.Spec.InputSchema.GetProperty("properties").GetProperty("duration").GetProperty("maximum").GetDouble());
        var pending = tool.InvokeAsync(Input(TimeSpan.MaxValue.TotalSeconds), Context(), cancellation.Token).AsTask();
        var admitted = !pending.IsCompleted;
        cancellation.Cancel();
        var observed = await Record.ExceptionAsync(async () => await Join(pending));
        Assert.True(admitted);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
    }

    [Fact]
    public async Task ContextRetainsItsPubliclyCapturedSignalWhenTheAgentLaterQueuesCancellation()
    {
        var model = new NeverModel();
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        var context = new ToolContext(agent, new("old-call", "sleep", Input(0)), new Dictionary<string, object?>());
        Assert.False(context.CancelSignal.CanBeCanceled);
        agent.Cancel();
        Assert.True(agent.CancelSignal.IsCancellationRequested);
        Assert.False(context.CancelSignal.CanBeCanceled);
        Assert.Equal("Slept for 0 seconds", await Sleep.Create().InvokeAsync(Input(0), context));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task OneSharedToolIsIndependentAcrossAgentsAndReusableAfterCancellation()
    {
        var real = Sleep.Create();
        var admissions = new ConcurrentDictionary<Agent, TaskCompletionSource<bool>>();
        var bodies = new ConcurrentDictionary<Agent, int>();
        var tracked = new FunctionTool(real.Spec, async (input, context, token) =>
        {
            bodies.AddOrUpdate(context.Agent, 1, (_, count) => count + 1);
            var pending = real.InvokeAsync(input, context, token).AsTask();
            if (input.GetProperty("duration").GetDouble() > 0)
                admissions[context.Agent].TrySetResult(!pending.IsCompleted);
            return await pending;
        });
        await using var first = new Agent(new() { Model = new ScriptedModel(Call("30"), Call("0"), Text("reused")), Tools = [tracked] });
        await using var second = new Agent(new() { Model = new ScriptedModel(Call("30")), Tools = [tracked] });
        admissions[first] = Gate();
        admissions[second] = Gate();
        var firstPass = first.InvokeAsync("first");
        var secondPass = second.InvokeAsync("second");
        try
        {
            Assert.True(await Join(admissions[first].Task));
            Assert.True(await Join(admissions[second].Task));
            first.Cancel();
            Assert.Equal(StopReasons.Cancelled, (await Join(firstPass)).StopReason);
            Assert.False(secondPass.IsCompleted);
            second.Cancel();
            Assert.Equal(StopReasons.Cancelled, (await Join(secondPass)).StopReason);
            var result = await first.InvokeAsync("reuse");
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("reused", result.Message!.Text);
            Assert.Equal(2, bodies[first]);
            Assert.Equal(1, bodies[second]);
        }
        finally
        {
            first.Cancel();
            second.Cancel();
            await Record.ExceptionAsync(async () => await Join(firstPass));
            await Record.ExceptionAsync(async () => await Join(secondPass));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionCancellationRaceNeverExposesAnInternalLinkedToken(bool useSignal)
    {
        var tool = Sleep.Create();
        for (var count = 0; count < 16; count++)
        {
            using var operation = new CancellationTokenSource();
            using var signal = new CancellationTokenSource();
            var start = Gate();
            var cancel = Task.Run(async () =>
            {
                await start.Task;
                if (useSignal) signal.Cancel();
                else operation.Cancel();
                return true;
            });
            start.SetResult(true);
            string? result = null;
            var error = await Record.ExceptionAsync(async () => result = await tool.InvokeAsync(Input(0), Context(signal.Token), operation.Token));
            await Join(cancel);
            if (error is null) Assert.Equal("Slept for 0 seconds", result);
            else Assert.Equal(useSignal ? signal.Token : operation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        }
    }

    [Fact]
    public async Task PositiveSubmillisecondCapIsValidWithinNativeTimerGranularity()
    {
        var cap = TimeSpan.FromTicks(1);
        var tool = Sleep.Create(new() { MaximumDuration = cap });
        var result = await tool.InvokeAsync(Input(cap.TotalSeconds), Context());
        Assert.Equal(cap.TotalSeconds.ToString("R", CultureInfo.InvariantCulture), result.Substring("Slept for ".Length, result.Length - "Slept for ".Length - " seconds".Length));
    }

    [Fact]
    public void GeneratedDescriptionAndSchemaCaptureCustomOptionsWithoutChangingDefaults()
    {
        var defaults = Sleep.Create();
        var options = new SleepOptions { Name = "pause", MaximumDuration = TimeSpan.FromSeconds(1.25) };
        var selected = Sleep.Create(options);
        var replacement = options with { Name = "later", MaximumDuration = TimeSpan.FromSeconds(3) };
        Assert.Equal("later", replacement.Name);
        Assert.Equal("pause", selected.Spec.Name);
        Assert.Contains("1.25", selected.Spec.Description);
        Assert.Equal(1.25, selected.Spec.InputSchema.GetProperty("properties").GetProperty("duration").GetProperty("maximum").GetDouble());
        Assert.Equal("sleep", defaults.Spec.Name);
        Assert.Equal(60, defaults.Spec.InputSchema.GetProperty("properties").GetProperty("duration").GetProperty("maximum").GetDouble());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"duration\":false}")]
    [InlineData("{\"duration\":61}")]
    public async Task RegisteredSchemaReturnsTheExistingAgentErrorResultPolicy(string input)
    {
        var model = new ScriptedModel(CallRaw(input), Text("complete"));
        await using var agent = new Agent(new() { Model = model, Tools = [Sleep.Create()] });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("go")).StopReason);
        var error = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(ToolResultStatus.Error, error.Status);
        Assert.Equal("sleep-use", error.ToolUseId);
        Assert.Equal(2, model.Calls);
    }

    private static JsonElement Input(double seconds) => JsonSerializer.SerializeToElement(new { duration = seconds });
    private static ToolContext Context(CancellationToken signal = default) => new(null!, new("sleep-use", "sleep", Input(0)), new Dictionary<string, object?>()) { ExecutionCancelSignal = signal };
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Join<T>(Task<T> operation)
    {
        using var watchdog = new CancellationTokenSource();
        try
        {
            Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token)));
            return await operation;
        }
        finally { watchdog.Cancel(); }
    }

    private static ModelStreamEvent[] Call(string duration) => CallRaw("{\"duration\":" + duration + "}");
    private static ModelStreamEvent[] CallRaw(string input) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("sleep-use", "sleep", JsonSerializer.SerializeToElement(new { }))),
        new ModelContentBlockDeltaEvent(0, ToolInput: input), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
    private static ModelStreamEvent[] Text(string text) =>
    [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];

    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in turns[Calls++])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }

    private sealed class NeverModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Factory and direct execution must not invoke a model.");
        }
    }
}
