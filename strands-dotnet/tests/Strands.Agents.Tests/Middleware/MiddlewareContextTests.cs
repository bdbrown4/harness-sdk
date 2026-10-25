using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Middleware;

public sealed class MiddlewareContextTests
{
    [Fact]
    public void ModelContextDetachesCollectionsAndJsonButBorrowsInvocationStateAndModel()
    {
        var initial = MiddlewareRegistryTests.Context();
        var messages = new[] { Message.User("original") };
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var tools = new[] { new ToolSpec("first", "First tool", schema.RootElement) { OutputSchema = schema.RootElement, Annotations = schema.RootElement } };
        var state = new Dictionary<string, object?> { ["caller"] = 1 };
        var context = new InvokeModelContext(initial.Agent, initial.Model, new(messages, "system", tools)
        {
            ModelState = new Dictionary<string, JsonElement> { ["secret"] = schema.RootElement },
            ToolChoice = new(ToolChoiceMode.Auto),
            DynamicTrailingBlocks = 1,
        }, state);
        messages[0] = Message.User("changed"); tools[0] = new("second", "Second tool", schema.RootElement);
        schema.Dispose(); state["caller"] = 2;

        Assert.Equal("original", Assert.Single(context.Messages).Text);
        var spec = Assert.Single(context.ToolSpecs);
        Assert.Equal("first", spec.Name); Assert.Equal("object", spec.OutputSchema!.Value.GetProperty("type").GetString());
        Assert.Equal("object", spec.Annotations!.Value.GetProperty("type").GetString());
        Assert.Same(initial.Model, context.Model); Assert.Same(state, context.InvocationState);
        Assert.Equal(2, context.InvocationState["caller"]);
        Assert.Null(typeof(InvokeModelContext).GetProperty("ModelState"));
    }

    [Fact]
    public async Task NextSnapshotsTransformedInputBeforeItsProducerCanReuseTheArray()
    {
        var registry = new MiddlewareRegistry();
        registry.RegisterWrap(MiddlewareStages.InvokeModel, Change);
        var completed = await MiddlewareRegistryTests.Collect(registry.InvokeAsync(MiddlewareStages.InvokeModel, MiddlewareRegistryTests.Context(), Terminal));
        Assert.Equal("transformed", completed.Result.Message.Text);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Change(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var changed = new[] { Message.User("transformed") };
            var inner = next(context with { Messages = changed });
            changed[0] = Message.User("corrupted");
            await foreach (var item in inner) yield return item;
        }
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Terminal(InvokeModelContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield return MiddlewareRegistryTests.Complete(context.Messages[0].Text); }
    }

    [Fact]
    public async Task ToolStageRetainsCancellationAndCanShortCircuitAReplacementImplementation()
    {
        var initial = MiddlewareRegistryTests.Context(); using var cancellation = new CancellationTokenSource();
        var state = new Dictionary<string, object?>();
        var call = new ToolUseBlock("original-id", "missing", JsonSerializer.SerializeToElement(new { value = 1 }));
        var registry = new MiddlewareRegistry();
        registry.RegisterInput(MiddlewareStages.ExecuteTool, (context, token) =>
        {
            Assert.Equal(cancellation.Token, token); Assert.Equal(cancellation.Token, context.CancellationToken);
            Assert.Same(state, context.InvocationState);
            return new(context with { ToolUse = context.ToolUse with { Input = JsonSerializer.SerializeToElement(new { value = 2 }) } });
        });
        registry.RegisterWrap(MiddlewareStages.ExecuteTool, Cached);
        registry.RegisterOutput(MiddlewareStages.ExecuteTool, (result, _) => new(new ExecuteToolResult(new(result.Result.ToolUseId, "output"))));
        ExecuteToolResult? completed = null;
        await foreach (var item in registry.InvokeAsync(MiddlewareStages.ExecuteTool, new(initial.Agent, null, call, state, cancellation.Token), Never, cancellation.Token))
            if (item is MiddlewareResultEvent<ExecuteToolResult> result) completed = result.Result;

        Assert.Equal("output", completed!.Result.Content);
        Assert.Equal(1, call.Input.GetProperty("value").GetInt32());

        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Cached(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; Assert.Equal(2, context.ToolUse.Input.GetProperty("value").GetInt32()); yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(context.ToolUse.ToolUseId, "cached"))); }
        IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> Never(ExecuteToolContext context, CancellationToken token) => throw new InvalidOperationException("Tool terminal must be skipped.");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task InvalidTransformedModelHintsAreRejectedBeforeTheTerminal(long estimate, int trailing)
    {
        var registry = new MiddlewareRegistry();
        registry.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new(context with { ProjectedInputTokens = estimate, DynamicTrailingBlocks = trailing }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MiddlewareRegistryTests.Collect(registry.InvokeAsync(MiddlewareStages.InvokeModel, MiddlewareRegistryTests.Context(), MiddlewareRegistryTests.Done)));
    }

    [Fact]
    public async Task InvalidAuthoritativeUsageAndNonAssistantResultsAreRejected()
    {
        var registry = new MiddlewareRegistry();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (_, _) => new(new InvokeModelResult(Message.Assistant("answer"), StopReasons.EndTurn, new(-1, 0))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MiddlewareRegistryTests.Collect(registry.InvokeAsync(MiddlewareStages.InvokeModel, MiddlewareRegistryTests.Context(), MiddlewareRegistryTests.Done)));
        var invalidRole = new MiddlewareRegistry();
        invalidRole.RegisterOutput(MiddlewareStages.InvokeModel, (_, _) => new(new InvokeModelResult(Message.User("wrong role"), StopReasons.EndTurn, new())));
        await Assert.ThrowsAsync<ArgumentException>(() => MiddlewareRegistryTests.Collect(invalidRole.InvokeAsync(MiddlewareStages.InvokeModel, MiddlewareRegistryTests.Context(), MiddlewareRegistryTests.Done)));
    }
}
