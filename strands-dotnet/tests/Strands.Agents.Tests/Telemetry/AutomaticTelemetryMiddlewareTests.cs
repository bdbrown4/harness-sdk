using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Telemetry;

public sealed partial class AutomaticTelemetryTests
{
    [Fact]
    public async Task AModelCacheDoesNotReportAProviderOperation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
        await using var agent = new Agent(new() { Model = new BoundaryModel(), Middleware = registry });
        var result = await agent.InvokeAsync("cached");
        Assert.Equal("cached", result.ToString());
        Assert.Equal(new Usage(1, 2), result.Metrics!.AccumulatedUsage);
        Assert.DoesNotContain(stopped, span => span.DisplayName == "chat");
        Assert.Empty(Assert.Single(result.Traces).Children);

        static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new(1, 2)));
        }
    }

    [Fact]
    public async Task ProviderSpansUseTheEffectiveModelAndProviderAccounting()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var replacement = new BoundaryModel("replacement");
        var registry = new MiddlewareRegistry();
        registry.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new(context with { Model = replacement, SystemPrompt = "effective instructions" }));
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { Message = Message.Assistant("transformed"), Usage = new(99, 88) }));
        await using var agent = new Agent(new() { Model = new BoundaryModel("original"), Middleware = registry });
        var result = await agent.InvokeAsync("question");
        Assert.Equal("transformed", result.ToString());
        Assert.Equal(new Usage(99, 88), result.Metrics!.AccumulatedUsage);
        var model = Assert.Single(stopped, span => span.DisplayName == "chat");
        Assert.Equal("replacement", model.GetTagItem("gen_ai.request.model"));
        Assert.Equal(2L, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(3L, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal("effective instructions", Assert.Single(replacement.Requests).SystemPrompt);
        Assert.Equal("provider", Assert.Single(Assert.Single(result.Traces).Children).Message!.Text);
    }

    [Fact]
    public async Task MiddlewareProviderRetriesProduceOneSpanPerAttempt()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var failure = new IOException("transient provider error");
        var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Retry);
        await using var agent = new Agent(new() { Model = new BoundaryModel() { FirstFailure = failure }, Middleware = registry });
        await agent.InvokeAsync("retry");
        var models = stopped.Where(span => span.DisplayName == "chat").ToArray();
        Assert.Equal(2, models.Length);
        Assert.Equal(ActivityStatusCode.Error, models[0].Status);
        Assert.Equal(ActivityStatusCode.Ok, models[1].Status);
        Assert.Equal(models[0].ParentSpanId, models[1].ParentSpanId);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Retry(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using (var first = next(context).GetAsyncEnumerator(cancellationToken))
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.MoveNextAsync().AsTask()));
            await foreach (var item in next(context)) yield return item;
        }
    }

    [Fact]
    public async Task AToolCacheDoesNotReportABodyExecution()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.ExecuteTool, Cached);
        var calls = 0;
        var tool = BoundaryTool("calculate", (_, _, _) => { calls++; return new("body"); });
        await using var agent = new Agent(new() { Model = new TimingModel(true), Middleware = registry, Tools = [tool] });
        var result = await agent.InvokeAsync("cached tool");
        Assert.Equal(0, calls);
        Assert.Empty(result.Metrics!.ToolMetrics);
        Assert.DoesNotContain(stopped, span => span.DisplayName.StartsWith("execute_tool ", StringComparison.Ordinal));

        static async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Cached(ExecuteToolContext context,
            MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(context.ToolUse.ToolUseId, "cached")));
        }
    }

    [Fact]
    public async Task ToolHookRetriesAndRenamesReportEveryEffectiveAttempt()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var calls = 0;
        var tool = BoundaryTool("replacement", (_, _, _) => ++calls == 1 ? throw new IOException("first attempt") : new("success"));
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.ToolUse = value.ToolUse with { Name = "replacement" }; return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = value.Result.Status == ToolResultStatus.Error; return default; });
        await using var agent = new Agent(new() { Model = new TimingModel(true), Hooks = hooks, Tools = [tool] });
        var result = await agent.InvokeAsync("retry tool");
        Assert.Equal(2, calls);
        var metric = Assert.Single(result.Metrics!.ToolMetrics);
        Assert.Equal("replacement", metric.Key);
        Assert.Equal(2, metric.Value.CallCount);
        Assert.Equal(1, metric.Value.SuccessCount);
        Assert.Equal(1, metric.Value.ErrorCount);
        Assert.Equal(2, stopped.Count(span => span.DisplayName == "execute_tool replacement"));
    }

    [Fact]
    public async Task AnInterruptedToolAttemptIsCountedWithoutACompletedResult()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tool = BoundaryTool("calculate", (_, context, _) => { context.Interrupt("approval"); return new("approved"); });
        await using var agent = new Agent(new() { Model = new TimingModel(true), Tools = [tool] });
        var result = await agent.InvokeAsync("approval");
        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        var metric = Assert.Single(result.Metrics!.ToolMetrics).Value;
        Assert.Equal(1, metric.CallCount);
        Assert.Equal(1, metric.ErrorCount);
        Assert.Equal(0, metric.SuccessCount);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(stopped, span => span.DisplayName == "execute_tool calculate").Status);
    }

    [Fact]
    public async Task EarlyDisposalFailuresSetTheInvocationErrorStatus()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var failure = new IOException("provider disposal failed");
        await using var agent = new Agent(new() { Model = new BoundaryModel() { DisposalFailure = failure } });
        var iterator = agent.StreamAsync("partial").GetAsyncEnumerator();
        while (await iterator.MoveNextAsync())
            if (iterator.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) break;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => iterator.DisposeAsync().AsTask()));
        Assert.All(stopped, span => Assert.Equal(ActivityStatusCode.Error, span.Status));
        Assert.Equal(3, stopped.Count);
    }

    private static FunctionTool BoundaryTool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body)
        => new(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), body);

    private sealed class BoundaryModel(string id = "provider") : IModel, IModelMetadataProvider
    {
        public ModelMetadata Metadata { get; } = new(id);
        public List<ModelRequest> Requests { get; } = [];
        public Exception? FirstFailure { get; init; }
        public Exception? DisposalFailure { get; init; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            if (Requests.Count == 1 && FirstFailure is { } failure) throw failure;
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "provider");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
                yield return new ModelMetadataEvent(new(2, 3));
            }
            finally { if (DisposalFailure is { } cleanup) throw cleanup; }
        }
    }
}
