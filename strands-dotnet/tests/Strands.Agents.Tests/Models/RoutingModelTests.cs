using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.RetryModelTests;

namespace Strands.Agents.Tests.Models;

public sealed class RoutingModelTests
{
    [Fact]
    public async Task StrategySnapshotsDetachOutputSchemaAndAnnotationsFromCallerJsonDocuments()
    {
        ToolSpec? captured = null;
        using (var metadata = JsonDocument.Parse("""{"output":{"type":"string"},"annotations":{"readOnlyHint":true}}"""))
        {
            var tool = new ToolSpec("read", "Read data", JsonSerializer.SerializeToElement(new { type = "object" }))
            {
                OutputSchema = metadata.RootElement.GetProperty("output"),
                Annotations = metadata.RootElement.GetProperty("annotations")
            };
            var router = new RoutingModel([new(new DelegateModel((_, _) => Success()))], new()
            {
                Strategy = new Selection(context => { captured = Assert.Single(context.Request.Tools!); return context.Candidates[0]; })
            });
            await foreach (var _ in router.StreamAsync(new([Message.User("go")], Tools: [tool]))) { }
        }

        Assert.NotNull(captured);
        Assert.True(captured.OutputSchema.HasValue);
        Assert.Equal("string", captured.OutputSchema.Value.GetProperty("type").GetString());
        Assert.True(captured.Annotations!.Value.GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public async Task FailsOverAndRetainsHealthAcrossToolTurnsWithinInvocation()
    {
        var first = 0;
        var second = 0;
        var router = new RoutingModel([
            new(new DelegateModel((_, _) => { first++; return Fail(new ModelException("bad")); }), "first"),
            new(new DelegateModel((_, _) => { second++; return Success(); }), "second")]);
        var invocation = router.CreateInvocationModel(new Dictionary<string, object?>());
        await ReadAsync(invocation);
        await ReadAsync(invocation);
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        await ReadAsync(router);
        Assert.Equal(2, first);
    }

    [Fact]
    public async Task InvalidSelectionAfterFailurePreservesPendingError()
    {
        var error = new ModelException("primary");
        var stranger = new RoutingCandidate(new DelegateModel((_, _) => Success()));
        var router = new RoutingModel([new(new DelegateModel((_, _) => Fail(error)))], new()
        {
            Strategy = new Selection(context => context.Attempts.Count == 0 ? context.Candidates[0] : stranger)
        });
        Assert.Same(error, await Assert.ThrowsAsync<ModelException>(() => ReadAsync(router)));
    }

    [Fact]
    public async Task DoesNotFailoverAfterPartialResponse()
    {
        var fallback = 0;
        var router = new RoutingModel([
            new(new DelegateModel((_, _) => Broken())),
            new(new DelegateModel((_, _) => { fallback++; return Success(); }))]);
        await Assert.ThrowsAsync<ModelException>(() => ReadAsync(router));
        Assert.Equal(0, fallback);
    }

    [Fact]
    public void RejectsDuplicateModelsAndNames()
    {
        var model = new DelegateModel((_, _) => Success());
        Assert.Throws<ArgumentException>(() => new RoutingModel([new(model), new(model)]));
        Assert.Throws<ArgumentException>(() => new RoutingModel([new(model, "same"), new(new DelegateModel((_, _) => Success()), "same")]));
    }

    [Fact]
    public async Task ZeroSwitchBudgetPreservesFailureWithoutCallingFallback()
    {
        var calls = 0;
        var error = new ModelException("primary");
        var router = new RoutingModel([new(new DelegateModel((_, _) => Fail(error))), new(new DelegateModel((_, _) => { calls++; return Success(); }))], new() { MaxSwitches = 0 });
        Assert.Same(error, await Assert.ThrowsAsync<ModelException>(() => ReadAsync(router)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NestedRouterIsOpaqueAndCannotPerformHiddenFailover()
    {
        var hidden = 0;
        var visible = 0;
        var inner = new RoutingModel([new(new DelegateModel((_, _) => Fail(new ModelException("inner")))), new(new DelegateModel((_, _) => { hidden++; return Success(); }))]);
        var outer = new RoutingModel([new(inner), new(new DelegateModel((_, _) => { visible++; return Success(); }))]);
        await ReadAsync(outer);
        Assert.Equal(0, hidden);
        Assert.Equal(1, visible);
    }

    [Fact]
    public async Task OpeningDeclineUsesDefaultWhileOpeningInvalidSelectionThrows()
    {
        var calls = 0;
        var model = new DelegateModel((_, _) => { calls++; return Success(); });
        await ReadAsync(new RoutingModel([new(model)], new() { Strategy = new Selection(_ => null) }));
        Assert.Equal(1, calls);
        var invalid = new RoutingCandidate(new DelegateModel((_, _) => Success()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(new RoutingModel([new(model)], new() { Strategy = new Selection(_ => invalid) })));
    }

    [Fact]
    public async Task StrategyCannotMutateModelInputThroughItsSnapshot()
    {
        var expected = Message.User("original");
        var model = new DelegateModel((request, _) => { Assert.Same(expected, request.Messages[0]); return Success(); });
        var router = new RoutingModel([new(model)], new()
        {
            Strategy = new Selection(context =>
            {
                Assert.NotSame(expected, context.Request.Messages[0]);
                Assert.Throws<NotSupportedException>(() => ((IList<Message>)context.Request.Messages).Clear());
                return context.Candidates[0];
            })
        });
        await foreach (var _ in router.StreamAsync(new([expected]))) { }
    }

    [Fact]
    public async Task EmptyInvalidStreamIsFailureAndUsesFallback()
    {
        var calls = 0;
        var router = new RoutingModel([new(new DelegateModel((_, _) => Empty())), new(new DelegateModel((_, _) => { calls++; return Success(); }))]);
        await ReadAsync(router);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClassifierUsesLatestRequestAndIgnoresAdditionalFields()
    {
        ModelRequest? captured = null;
        var classifier = new ClassifierRoutingStrategy(new DelegateModel((request, _) =>
        {
            captured = request;
            return Classification("{\"selectedCandidateIndex\":1,\"extra\":true}");
        }));
        var candidates = new[] { new RoutingCandidate(new DelegateModel((_, _) => Success()), "fast"), new RoutingCandidate(new DelegateModel((_, _) => Success()), "strong") };
        var context = new RoutingContext(new([Message.User("original"), new(Role.User, [new ToolResultBlock("id", "do not route on this")]), Message.User("latest")]), candidates, [], new Dictionary<string, object?>());
        Assert.Same(candidates[1], await classifier.SelectAsync(context));
        Assert.Equal("latest", captured!.Messages[0].Text);
        Assert.Contains("untrusted_classification_context", captured.SystemPrompt);
        Assert.Equal(new(ToolChoiceMode.Tool, "strands_structured_output"), captured.ToolChoice);
    }

    [Fact]
    public async Task ClassifierCanChooseNondefaultCandidateAndFailureDeclines()
    {
        var candidates = new[] { new RoutingCandidate(new DelegateModel((_, _) => Success())), new RoutingCandidate(new DelegateModel((_, _) => Success())) };
        var context = new RoutingContext(new([Message.User("complex")]), candidates, [], new Dictionary<string, object?>());
        var strategy = new ClassifierRoutingStrategy(new DelegateModel((_, _) => Classification("{\"selectedCandidateIndex\":1}")));
        Assert.Same(candidates[1], await strategy.SelectAsync(context));
        Assert.Null(await strategy.SelectAsync(context with { Attempts = [new(candidates[1], new ModelException("failed"))] }));
    }

    [Fact]
    public async Task ClassifierEvidenceBudgetErrorsAreNotSilentlyIgnored()
    {
        var candidates = new[] { new RoutingCandidate(new DelegateModel((_, _) => Success()), "large"), new RoutingCandidate(new DelegateModel((_, _) => Success())) };
        var context = new RoutingContext(new([Message.User("go")]), candidates, [], new Dictionary<string, object?>());
        var classifier = new ClassifierRoutingStrategy(new DelegateModel((_, _) => throw new InvalidOperationException("must not be called")), new() { MaxCandidateCharacters = 1 });
        await Assert.ThrowsAsync<ArgumentException>(async () => await classifier.SelectAsync(context));
    }

    [Fact]
    public async Task ClassifierTimeoutBoundsNoncooperativeProviderAndObservesFailure()
    {
        var candidates = new[] { new RoutingCandidate(new DelegateModel((_, _) => Success())), new RoutingCandidate(new DelegateModel((_, _) => Success())) };
        var context = new RoutingContext(new([Message.User("go")]), candidates, [], new Dictionary<string, object?>());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = new ClassifierRoutingStrategy(new DelegateModel((_, _) => Waiting(release.Task)), new() { Timeout = TimeSpan.FromMilliseconds(10) });
        try { Assert.Null(await classifier.SelectAsync(context).AsTask().WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { release.TrySetResult(true); }
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Classification(string input)
    {
        yield return new ModelMessageStartEvent();
        yield return new ModelContentBlockStartEvent(0, new("route", "strands_structured_output", JsonDocument.Parse(input).RootElement.Clone()));
        yield return new ModelContentBlockStopEvent(0);
        yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Waiting(Task release, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await release;
        await foreach (var item in Fail(new ModelException("late failure"))) yield return item;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Empty() { await Task.Yield(); yield break; }

    private sealed class Selection(Func<RoutingContext, RoutingCandidate?> callback) : IRoutingStrategy
    {
        public ValueTask<RoutingCandidate?> SelectAsync(RoutingContext context, CancellationToken cancellationToken = default) => new(callback(context));
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Broken()
    {
        yield return new ModelMessageStartEvent();
        await foreach (var item in Fail(new ModelException("partial"))) yield return item;
    }
}
