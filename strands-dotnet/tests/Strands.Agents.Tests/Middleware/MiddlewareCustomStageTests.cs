using System.Reflection;
using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Middleware;

public sealed class MiddlewareCustomStageTests
{
    [Fact]
    public async Task SameNamedStandaloneTokensKeepIndependentRegistrations()
    {
        var first = Create("custom"); var second = Create("custom");
        Assert.Equal(first.Name, second.Name); Assert.NotSame(first, second);
        var registry = new MiddlewareRegistry();
        using var firstHandle = registry.RegisterInput(first, (value, _) => new(value with { Words = value.Words.Append("first").ToArray() }));
        using var secondHandle = registry.RegisterInput(second, (value, _) => new(value with { Words = value.Words.Append("second").ToArray() }));

        var a = await Collect(registry.InvokeAsync(first, new(["seed"]), Echo));
        var b = await Collect(registry.InvokeAsync(second, new(["seed"]), Echo));

        Assert.Equal(new[] { "seed", "first" }, a.Result.Words);
        Assert.Equal(new[] { "seed", "second" }, b.Result.Words);
    }

    [Fact]
    public async Task StandalonePhasesUseInputThenWrappedTerminalThenReverseOutputs()
    {
        var stage = Create("phases"); var registry = new MiddlewareRegistry(); var order = new List<string>();
        using var outputA = registry.RegisterOutput(stage, (value, _) => { order.Add("output-a"); return new(value with { Words = value.Words.Append("a").ToArray() }); });
        using var wrapA = registry.RegisterWrap(stage, (value, next, _) => Forward("wrap-a", value, next));
        using var inputA = registry.RegisterInput(stage, (value, _) => { order.Add("input-a"); return new(value with { Words = value.Words.Append("a").ToArray() }); });
        using var outputB = registry.RegisterOutput(stage, (value, _) => { order.Add("output-b"); return new(value with { Words = value.Words.Append("b").ToArray() }); });
        using var wrapB = registry.RegisterWrap(stage, (value, next, _) => Forward("wrap-b", value, next));
        using var inputB = registry.RegisterInput(stage, (value, _) => { order.Add("input-b"); return new(value with { Words = value.Words.Append("b").ToArray() }); });

        var captured = await Collect(registry.InvokeAsync(stage, new([]), Terminal));

        Assert.Equal(new[] { "input-a", "input-b", "wrap-a-before", "wrap-b-before", "terminal-a,b", "wrap-b-after", "wrap-a-after", "output-b", "output-a" }, order);
        Assert.Equal(new[] { "terminal", "b", "a" }, captured.Result.Words);

        async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Forward(string name, StepContext value, MiddlewareNext<StepContext, StepResult> next)
        {
            order.Add(name + "-before");
            await foreach (var item in next(value)) yield return item;
            order.Add(name + "-after");
        }
        async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Terminal(StepContext value, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            order.Add("terminal-" + string.Join(",", value.Words));
            yield return new MiddlewareResultEvent<StepResult>(new(["terminal"]));
        }
    }

    [Fact]
    public async Task StandaloneShortCircuitForwardsCustomProgressAndSkipsTerminal()
    {
        var stage = Create("cache"); var registry = new MiddlewareRegistry(); var calls = 0; var progress = new StepProgress("cached");
        using var wrap = registry.RegisterWrap(stage, Cached);
        using var output = registry.RegisterOutput(stage, (value, _) => new(value with { Words = value.Words.Append("output").ToArray() }));

        var captured = await Collect(registry.InvokeAsync(stage, new([]), Terminal));

        Assert.Equal(0, calls);
        Assert.Same(progress, Assert.Single(captured.Events));
        Assert.Equal(new[] { "cached", "output" }, captured.Result.Words);

        async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Cached(StepContext value, MiddlewareNext<StepContext, StepResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            yield return new MiddlewareStreamEvent<StepResult>(progress);
            yield return new MiddlewareResultEvent<StepResult>(new(["cached"]));
        }
        async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Terminal(StepContext value, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); calls++;
            yield return new MiddlewareResultEvent<StepResult>(new(["unexpected"]));
        }
    }

    [Fact]
    public async Task StandaloneUsesExplicitSnapshotsBeforeEnumerationAndAtCompletedResult()
    {
        var stage = Create("snapshots"); var registry = new MiddlewareRegistry();
        var input = new List<string> { "before" }; var output = new List<string> { "result" };
        var rawResult = new StepResult(output);
        var stream = registry.InvokeAsync(stage, new(input), Terminal);
        input.Add("late-input");

        var captured = await Collect(stream);
        output.Add("late-output");

        Assert.Equal(new[] { "result" }, captured.Result.Words);
        Assert.NotSame(rawResult, captured.Result);

        async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Terminal(StepContext value, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new[] { "before" }, value.Words);
            yield return new MiddlewareResultEvent<StepResult>(rawResult);
        }
    }

    private static MiddlewareStage<StepContext, StepResult> Create(string name)
    {
        var definition = typeof(MiddlewareStages).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(value => value.Name == "Create" && value.IsGenericMethodDefinition && value.GetGenericArguments().Length == 2 && value.GetParameters().Length == 3);
        Assert.NotNull(definition);
        var method = definition.MakeGenericMethod(typeof(StepContext), typeof(StepResult));
        Assert.Equal(typeof(MiddlewareStage<StepContext, StepResult>), method.ReturnType);
        var factory = (Func<string, Func<StepContext, StepContext>, Func<StepResult, StepResult>, MiddlewareStage<StepContext, StepResult>>)method.CreateDelegate(
            typeof(Func<string, Func<StepContext, StepContext>, Func<StepResult, StepResult>, MiddlewareStage<StepContext, StepResult>>));
        return factory(name, value => new(value.Words.ToArray()), value => new(value.Words.ToArray()));
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<StepResult>> Echo(StepContext value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
        yield return new MiddlewareResultEvent<StepResult>(new(value.Words));
    }

    private static async Task<(IReadOnlyList<IAgentStreamEvent> Events, StepResult Result)> Collect(IAsyncEnumerable<IMiddlewareEvent<StepResult>> stream)
    {
        var events = new List<IAgentStreamEvent>(); StepResult? result = null;
        await foreach (var item in stream)
        {
            if (item is MiddlewareStreamEvent<StepResult> update) events.Add(update.Event);
            else if (item is MiddlewareResultEvent<StepResult> completed) { Assert.Null(result); result = completed.Result; }
        }
        return (events, Assert.IsType<StepResult>(result));
    }

    private sealed record StepContext(IReadOnlyList<string> Words);
    private sealed record StepResult(IReadOnlyList<string> Words);
    private sealed record StepProgress(string Text) : IAgentStreamEvent;
}
