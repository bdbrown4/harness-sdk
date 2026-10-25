using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Decisions;
using Strands.Agents.Telemetry;
using Xunit;

namespace Strands.Agents.Tests.Decisions;

[Collection("Telemetry listeners")]
public sealed class DecisionModelTests
{
    [Fact]
    public async Task MalformedMissingAndDisallowedAnswersFailWithoutHiddenRetries()
    {
        var invalid = new[]
        {
            "null", "{}", "{\"q\":null}", "{\"q\":{\"answer\":true}}", "{\"q\":{\"answer\":\"unknown\"}}",
            "{\"q\":{\"answer\":\"uncertain\"}}", "{\"q\":{\"answer\":\"true\",\"reason\":null}}"
        };
        foreach (var json in invalid)
        {
            var model = new Answers(json);
            var operation = new Func<Task>(async () => await new LLMDecisionModel(model).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") { AllowUncertain = false } }));
            if (json == "null") await Assert.ThrowsAsync<ModelException>(operation);
            else await Assert.ThrowsAsync<StructuredOutputException>(operation);
            Assert.Single(model.Requests);
        }
    }

    [Fact]
    public async Task ATextResponseWrongToolOrTruncatedTurnCannotBecomeASuccessfulDecision()
    {
        var models = new[]
        {
            new Answers("""{"q":{"answer":"true"}}""") { EmitTool = false, StopReason = StopReasons.EndTurn },
            new Answers("""{"q":{"answer":"true"}}""") { ToolName = "another_tool" },
            new Answers("""{"q":{"answer":"true"}}""") { StopReason = StopReasons.MaxTokens }
        };
        foreach (var model in models)
        {
            await Assert.ThrowsAsync<StructuredOutputException>(async () => await new LLMDecisionModel(model).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") }));
            Assert.Single(model.Requests);
        }
    }

    [Fact]
    public void TypedAnswersSerializeAsBooleanStringAndSourceCompatibleSentinels()
    {
        var values = new Dictionary<string, DecisionAnswer>
        {
            ["boolean"] = DecisionAnswer.FromBoolean(false),
            ["choice"] = DecisionAnswer.FromChoice("red"),
            ["unknown"] = DecisionAnswer.FromUncertain("insufficient evidence")
        };
        var json = JsonSerializer.Serialize(values);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("boolean").GetBoolean());
        Assert.Equal("red", document.RootElement.GetProperty("choice").GetString());
        Assert.True(document.RootElement.GetProperty("unknown").GetProperty("uncertain").GetBoolean());
        Assert.Equal("insufficient evidence", document.RootElement.GetProperty("unknown").GetProperty("reason").GetString());
        var restored = JsonSerializer.Deserialize<Dictionary<string, DecisionAnswer>>(json)!;
        Assert.False(restored["boolean"].BooleanValue);
        Assert.Equal("red", restored["choice"].ChoiceValue);
        Assert.Equal("insufficient evidence", restored["unknown"].UncertainValue!.Reason);
        Assert.Equal("{\"uncertain\":true}", JsonSerializer.Serialize(new Uncertain()));
        Assert.Equal("Uncertain", new Uncertain().ToString());
    }

    [Fact]
    public async Task TypedContentHistoriesAndExplicitEmptyInputsKeepTheirNativeShape()
    {
        var model = new Answers("""{"q":{"answer":"true"}}""");
        var decision = new LLMDecisionModel(model);
        var questions = new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") };
        using (var document = JsonDocument.Parse("{\"value\":7}"))
        {
            var operation = decision.AskContentAsync(new ContentBlock[] { new TextBlock("image"), new ImageBlock("image/png", "AQID"), new JsonBlock(document.RootElement) }, questions);
            document.Dispose();
            await operation;
        }
        var content = Assert.Single(model.Requests[0].Messages).Content;
        Assert.Equal("image", Assert.IsType<TextBlock>(content[0]).Text);
        Assert.Equal("AQID", Assert.IsType<ImageBlock>(content[1]).Data);
        Assert.Equal(7, Assert.IsType<JsonBlock>(content[2]).Json.GetProperty("value").GetInt32());
        await decision.AskMessagesAsync(new[] { new Message(Role.Assistant, new[] { new TextBlock("prior") }, "retained-id") }, questions);
        Assert.Equal("retained-id", Assert.Single(model.Requests[1].Messages).TrackingId);
        await decision.AskMessagesAsync(Array.Empty<Message>(), questions);
        Assert.Empty(model.Requests[2].Messages);
        await decision.AskContentAsync(Array.Empty<ContentBlock>(), questions);
        Assert.Empty(Assert.Single(model.Requests[3].Messages).Content);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task AbsentMetadataLeavesUsageUnknownAndUsesMeasuredModelLatency()
    {
        var model = new Answers("""{"q":{"answer":"true"}}""") { EmitMetadata = false, BeforeResponse = token => Task.Delay(20, token) };

        var result = await new LLMDecisionModel(model).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") });

        Assert.Null(result.Usage);
        Assert.True(result.Metadata.Latency > TimeSpan.Zero);
        Assert.Equal(0, model.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringTheStreamOrItsDisposalRetainsTheCallerToken(bool duringDisposal)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Answers("""{"q":{"answer":"true"}}""")
        {
            BeforeResponse = duringDisposal ? null : async token => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); },
            OnDispose = duringDisposal ? cancellation.Cancel : null
        };
        var operation = new LLMDecisionModel(model).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") }, cancellation.Token).AsTask();
        if (!duringDisposal)
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            cancellation.Cancel();
        }

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(model.Requests);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task FailedDecisionTracingAndFailingListenersRetainTheOriginalProviderError()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { stopped.Enqueue(activity); throw new IOException("exporter failed"); }
        };
        ActivitySource.AddActivityListener(listener);
        var primary = new IOException("provider failed"); var cleanup = new IOException("provider cleanup failed");

        var error = await Assert.ThrowsAsync<IOException>(async () => await new LLMDecisionModel(new BrokenModel(primary, cleanup)).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") }));

        Assert.Same(primary, error);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped, activity => activity.DisplayName == "decision").Status);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task CustomDecisionImplementationsReceiveTheSameValidatedDetachedQuestions()
    {
        var decision = new ProgrammaticDecision();
        await Assert.ThrowsAsync<ArgumentException>(async () => await decision.AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new(" ") }));
        Assert.False(decision.Called);

        var result = await decision.AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") { MinConfidence = 0.7 } });

        Assert.True(decision.Called);
        Assert.True(Assert.Single(result.Answers).Value.BooleanValue);
        Assert.Null(result.Usage);
    }

    [Fact]
    public void ContradictoryUnreportedUsageIsRejectedWithoutChangingAccumulatedTimingOrPresence()
    {
        var invalid = new[] { new Usage(2, 3), new Usage(0, 0, CacheReadInputTokens: 1), new Usage(0, 0, CacheWriteInputTokens: 1), new Usage(0, 0, ReportedTotalTokens: 1) };
        foreach (var usage in invalid)
        {
            var accumulator = new ModelStreamAccumulator();
            var timing = new ModelPerformanceMetrics(TimeSpan.FromMilliseconds(7));
            accumulator.Process(new ModelMetadataEvent(new()) { UsageReported = false, PerformanceMetrics = timing });
            var originalUsage = accumulator.Usage;

            Assert.Throws<ModelException>(() => accumulator.Process(new ModelMetadataEvent(usage) { UsageReported = false, PerformanceMetrics = new(TimeSpan.FromMilliseconds(99)) }));

            Assert.False(accumulator.UsageReported);
            Assert.Same(originalUsage, accumulator.Usage);
            Assert.Same(timing, accumulator.PerformanceMetrics);
            Assert.Throws<ModelException>(() => accumulator.Process(new ModelMetadataEvent(new(-1, 0))));
            Assert.Throws<ModelException>(() => accumulator.Process(new ModelMetadataEvent(new()) { PerformanceMetrics = new(TimeSpan.FromTicks(-1)) }));
            Assert.False(accumulator.UsageReported);
            Assert.Same(originalUsage, accumulator.Usage);
            Assert.Same(timing, accumulator.PerformanceMetrics);
        }
    }

    [Fact]
    public async Task TimingOnlyMetadataDoesNotInventReportedZeroUsage()
    {
        var model = new Answers("""{"q":{"answer":"true"}}""") { ReportedUsage = new(), UsageReported = false };

        var result = await new LLMDecisionModel(model).AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") });

        Assert.Null(result.Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(17), result.Metadata.Latency);
    }

    [Fact]
    public void AReportedZeroIsDistinctFromNoProviderUsage()
    {
        var accumulator = new ModelStreamAccumulator();
        Assert.False(accumulator.UsageReported);

        accumulator.Process(new ModelMetadataEvent(new Usage(0, 0)));

        Assert.True(accumulator.UsageReported);
        Assert.Equal(new Usage(0, 0), accumulator.Usage);
    }

    [Fact]
    public async Task TheAdvertisedSchemaIsClosedWhileLocalParsingStripsExtraFieldsLikeTheSource()
    {
        var model = new Answers("""{"q":{"answer":"true","extra":"discarded"},"extra-question":{"answer":"false"}}""");
        var decision = new LLMDecisionModel(model, new() { SystemPrompt = "Custom framing." });

        var result = await decision.AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") });

        Assert.True(Assert.Single(result.Answers).Value.BooleanValue);
        var request = Assert.Single(model.Requests);
        var schema = Assert.Single(request.Tools!).InputSchema;
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("properties").GetProperty("q").GetProperty("additionalProperties").GetBoolean());
        Assert.StartsWith("Custom framing.", request.SystemPrompt);
        Assert.Contains("MANDATORY RULES", request.SystemPrompt);
        Assert.Contains("untrusted data", request.SystemPrompt);
    }

    [Fact]
    public async Task DecisionTracingInheritsAmbientContextAndRecordsOnlyQuestionIdsAndAnswers()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var decision = new LLMDecisionModel(new Answers("""{"q":{"answer":"true"}}"""));

        await decision.AskAsync("private state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") });

        var span = Assert.Single(stopped, value => value.DisplayName == "decision");
        Assert.Equal(parent.SpanId, span.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("decision", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(17d, span.GetTagItem("gen_ai.decision.latency_ms"));
        Assert.Equal(3L, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(new[] { "q" }, Assert.IsType<string[]>(Assert.Single(span.Events, value => value.Name == "gen_ai.decision.questions").Tags.Single().Value));
        Assert.Equal("{\"q\":true}", Assert.Single(span.Events, value => value.Name == "gen_ai.decision.answers").Tags.Single().Value);
        Assert.DoesNotContain("private state", string.Join(";", span.TagObjects.Select(pair => pair.Value)));
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task AProviderFailureRetainsIdentityWhenStreamDisposalAlsoFails()
    {
        var primary = new IOException("stream failed");
        var cleanup = new IOException("stream cleanup failed");
        var model = new BrokenModel(primary, cleanup);
        var decision = new LLMDecisionModel(model);

        var error = await Assert.ThrowsAsync<IOException>(async () => await decision.AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = new("Boolean?") }));

        Assert.Same(primary, error);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task InvalidQuestionShapesAreRejectedBeforeTheBorrowedProviderIsTouched()
    {
        var invalid = new DecisionQuestion[]
        {
            null!, new(" "), new("Choose", Array.Empty<string>()), new("Choose", new[] { "a", "a" }),
            new("Choose", new[] { "" }), new("Choose", new[] { "uncertain" }),
            new("Boolean?") { MinConfidence = double.NaN }, new("Boolean?") { MinConfidence = -0.1 }, new("Boolean?") { MinConfidence = 1.1 }
        };
        foreach (var question in invalid)
        {
            var model = new Answers("""{"q":{"answer":"true"}}""");
            var decision = new LLMDecisionModel(model);

            await Assert.ThrowsAsync<ArgumentException>(async () => await decision.AskAsync("state", new Dictionary<string, DecisionQuestion> { ["q"] = question }));

            Assert.Empty(model.Requests);
        }
    }

    [Fact]
    public async Task InputsAndQuestionCollectionsAreDetachedBeforeTheProviderAwaits()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Answers("""{"q":{"answer":"red"}}""") { BeforeResponse = async _ => { entered.TrySetResult(true); await release.Task.ConfigureAwait(false); } };
        var decision = new LLMDecisionModel(model);
        var messages = new List<Message> { Message.User("red apple") };
        var choices = new List<string> { "red", "green" };
        var questions = new Dictionary<string, DecisionQuestion> { ["q"] = new("Which color?", choices) };
        var operation = decision.AskMessagesAsync(messages, questions).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        messages.Clear(); choices.Clear(); questions.Clear(); release.TrySetResult(true);
        var result = await operation;

        Assert.Equal("red apple", Assert.Single(Assert.Single(model.Requests).Messages).Text);
        Assert.Equal("red", Assert.Single(result.Answers).Value.ChoiceValue);
    }

    [Fact]
    public async Task AnEmptyQuestionMapIsRejectedBeforeProviderEffects()
    {
        var model = new Answers("{}");
        var decision = new LLMDecisionModel(model);

        await Assert.ThrowsAsync<ArgumentException>(async () => await decision.AskAsync("state", new Dictionary<string, DecisionQuestion>()));

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task UncertaintyRetainsItsReasonWithoutInventingLlmConfidenceEnforcement()
    {
        var model = new Answers("""{"q":{"answer":"uncertain","reason":"No evidence was supplied."}}""");
        var decision = new LLMDecisionModel(model);

        var result = await decision.AskAsync("unknown", new Dictionary<string, DecisionQuestion> { ["q"] = new("Is it a fruit?") { MinConfidence = 1 } });

        var answer = Assert.Single(result.Answers).Value;
        Assert.Equal(DecisionAnswerKind.Uncertain, answer.Kind);
        Assert.Equal("No evidence was supplied.", answer.UncertainValue!.Reason);
        Assert.Equal("Uncertain(No evidence was supplied.)", answer.UncertainValue.ToString());
        var literals = Assert.Single(model.Requests).Tools![0].InputSchema.GetProperty("properties").GetProperty("q").GetProperty("properties").GetProperty("answer").GetProperty("enum");
        Assert.Equal(new[] { "true", "false", "uncertain" }, literals.EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task ADecisionUsesOneForcedStructuredCallAndReturnsTypedAnswersAndProviderAccounting()
    {
        var model = new Answers("""{"fruit":{"answer":"true"},"color":{"answer":"red"}}""");
        var decision = new LLMDecisionModel(model);

        var result = await decision.AskAsync("A red apple.", new Dictionary<string, DecisionQuestion>
        {
            ["fruit"] = new("Is it a fruit?") { AllowUncertain = false },
            ["color"] = new("Which color?", new[] { "red", "green" })
        });

        Assert.True(result.Answers["fruit"].BooleanValue);
        Assert.Equal("red", result.Answers["color"].ChoiceValue);
        Assert.Equal(new Usage(3, 2), result.Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(17), result.Metadata.Latency);
        var request = Assert.Single(model.Requests);
        Assert.Equal("A red apple.", Assert.Single(request.Messages).Text);
        Assert.Equal(ToolChoiceMode.Tool, request.ToolChoice!.Mode);
        Assert.Equal(StructuredOutputTool<JsonElement>.ToolName, request.ToolChoice.Name);
        Assert.Equal(StructuredOutputTool<JsonElement>.ToolName, Assert.Single(request.Tools!).Name);
        Assert.Equal(0, model.Disposals);
    }

    private sealed class Answers(string answers) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = new();
        internal int Disposals { get; private set; }
        internal Func<CancellationToken, Task>? BeforeResponse { get; init; }
        internal Usage ReportedUsage { get; init; } = new(3, 2);
        internal bool UsageReported { get; init; } = true;
        internal bool EmitMetadata { get; init; } = true;
        internal ModelPerformanceMetrics? Timing { get; init; } = new(TimeSpan.FromMilliseconds(17));
        internal string StopReason { get; init; } = StopReasons.ToolUse;
        internal string ToolName { get; init; } = StructuredOutputTool<JsonElement>.ToolName;
        internal bool EmitTool { get; init; } = true;
        internal Action? OnDispose { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            try
            {
                if (BeforeResponse is { } before) await before(cancellationToken).ConfigureAwait(false);
                await Task.Yield();
                yield return new ModelMessageStartEvent();
                if (EmitTool) yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("decision", ToolName, JsonSerializer.Deserialize<JsonElement>(answers)));
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "No structured answers.");
                }
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReason);
                if (EmitMetadata) yield return new ModelMetadataEvent(ReportedUsage) { UsageReported = UsageReported, PerformanceMetrics = Timing };
            }
            finally { OnDispose?.Invoke(); }
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class BrokenModel(Exception primary, Exception cleanup) : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return new Events(primary, cleanup); }

        private sealed class Events(Exception primary, Exception cleanup) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            public ModelStreamEvent Current => throw new InvalidOperationException();
            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() => new(Task.FromException<bool>(primary));
            public ValueTask DisposeAsync() => new(Task.FromException(cleanup));
        }
    }

    private sealed class ProgrammaticDecision : DecisionModel
    {
        internal bool Called { get; private set; }
        protected override ValueTask<DecisionResult> AskCoreAsync(IReadOnlyList<Message> state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Called = true;
            Assert.Equal("state", Assert.Single(state).Text);
            Assert.Equal(0.7, Assert.Single(questions).Value.MinConfidence);
            return new(new DecisionResult(questions.ToDictionary(pair => pair.Key, _ => DecisionAnswer.FromBoolean(true), StringComparer.Ordinal), new(TimeSpan.Zero)));
        }
    }
}
