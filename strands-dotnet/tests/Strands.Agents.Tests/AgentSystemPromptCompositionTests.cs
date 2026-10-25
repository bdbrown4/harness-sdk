using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Goals;
using Strands.Agents.Skills;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests;

[Collection("Telemetry listeners")]
public sealed class AgentSystemPromptCompositionTests
{
    [Fact]
    public async Task OrdinaryStreamCarriesTypedInstructionsWithoutChangingResultEvents()
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        AgentResult? result = null;
        await foreach (var value in agent.StreamAsync("question"))
            if (value is AgentResultEvent completed) result = completed.Result;
        Assert.NotNull(result);
        Assert.Equal("answer", result.ToString());
        AssertPrompt(Assert.Single(model.Requests));
    }

    [Fact]
    public async Task StatefulDeltaHistoryRetainsInstructionsAcrossAcknowledgedTurns()
    {
        var model = new StatefulResponses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await agent.InvokeAsync("first question");
        await agent.InvokeAsync("second question");
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, AssertPrompt);
        Assert.Single(model.Requests[1].Messages);
        Assert.Equal("second question", model.Requests[1].Messages[0].Text);
        Assert.True(model.Requests[1].ModelState!.ContainsKey("server-state"));
    }

    [Fact]
    public async Task StructuredForceRoundKeepsTheAcceptedTypedInstructions()
    {
        var model = new Responses
        {
            Reply = index => index == 0 ? Message.Assistant("plain first")
                : new(Role.Assistant, [new ToolUseBlock("structured", StructuredOutputTool<JsonElement>.ToolName, JsonSerializer.SerializeToElement(new { value = 7 }))])
        };
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        var result = await agent.InvokeStructuredAsync<JsonElement>("question", new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } })
        });
        Assert.Equal(7, result.Output.GetProperty("value").GetInt32());
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, AssertPrompt);
        Assert.Equal(ToolChoiceMode.Tool, model.Requests[1].ToolChoice!.Mode);
        Assert.Equal(StructuredOutputTool<JsonElement>.ToolName, model.Requests[1].ToolChoice!.Name);
    }

    [Fact]
    public async Task HumanInterruptResumeRetainsTypedPromptAndExecutesBodyOnce()
    {
        var model = new Responses
        {
            Reply = index => index == 0 ? new(Role.Assistant, [new ToolUseBlock("approval", "approve", JsonSerializer.SerializeToElement(new { }))])
                : Message.Assistant("approved")
        };
        var effects = 0;
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            context.Interrupt("approval");
            effects++;
            return new ValueTask<string>("yes");
        });
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt(), Tools = [tool] });
        var paused = await agent.InvokeAsync("question");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        Assert.Equal(0, effects);
        var completed = await agent.InvokeAsync(null, new()
        {
            InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]
        });
        Assert.Equal("approved", completed.ToString());
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, AssertPrompt);
    }

    [Fact]
    public async Task GoalResetRewindsFullTypedPromptBeforeTheNextAttempt()
    {
        var attempts = 0;
        var goal = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(++attempts == 2)),
            new() { PreserveContext = false, MaxAttempts = 2 });
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt(), Plugins = [goal] });
        await agent.InvokeAsync("question");
        Assert.Equal(2, attempts);
        Assert.True(goal.LastResult(agent)!.Passed);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, AssertPrompt);
        AssertPrompt(new([], agent.SystemPrompt) { SystemPromptContent = agent.SystemPromptContent });
    }

    [Fact]
    public async Task TypedAgentToolResetRestoresCapturedPromptRatherThanLaterExternalPrompt()
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await using var parent = new Agent(new() { Model = new Responses() });
        var tool = agent.AsTool(new AgentToolOptions());
        for (var index = 0; index < 2; index++)
        {
            await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
            {
                ["systemPrompt"] = JsonSerializer.SerializeToElement("external " + index)
            }));
            var input = JsonSerializer.SerializeToElement(new { input = "question " + index });
            Assert.Equal("answer", await tool.InvokeAsync(input, new(parent, new("nested-" + index, tool.Spec.Name, input), new Dictionary<string, object?>())));
        }
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, AssertPrompt);
        Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task LegacyAgentToolOverloadUsesFreshTypedChildWithoutMutatingParent()
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        var tool = agent.AsTool();
        var input = JsonSerializer.SerializeToElement(new { task = "question" });
        Assert.Equal("answer", await tool.InvokeAsync(input, new(agent, new("nested", tool.Spec.Name, input), new Dictionary<string, object?>())));
        AssertPrompt(Assert.Single(model.Requests));
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task AgenticReductionCountsTheFullTypedPromptOnBothCandidateRequests()
    {
        var model = new CountingResponses();
        var manager = new ContextManager(new() { Mode = ContextMode.Agentic, Strategies = [], StashEnabled = false });
        await using var agent = new Agent(new()
        {
            Model = model,
            SystemPromptContent = Prompt(),
            ContextManager = manager,
            Messages = [Message.User("first"), Message.Assistant(new string('x', 200)), Message.User(new string('y', 200)),
                Message.Assistant("bridge"), Message.User("latest"), Message.Assistant("recent")]
        });
        await agent.InitializeAsync();
        var tool = Assert.Single(agent.Tools, value => value.Spec.Name == "truncate_context");
        var input = JsonSerializer.SerializeToElement(new { keepRecent = 2 });
        var result = await tool.InvokeAsync(input, new(agent, new("trim", tool.Spec.Name, input), new Dictionary<string, object?>()));
        Assert.StartsWith("Dropped ", result);
        Assert.Equal(2, model.CountRequests.Count);
        Assert.All(model.CountRequests, AssertPrompt);
        Assert.True(model.CountRequests[1].Messages.Count < model.CountRequests[0].Messages.Count);
    }

    [Fact]
    public async Task OverflowRecoveryCountsTypedPromptInsteadOfFlatteningIt()
    {
        var model = new CountingResponses { OverflowFirst = true };
        var manager = new ContextManager(new() { Strategies = [new RemoveOldExchangeOnOverflow()], StashEnabled = false });
        await using var agent = new Agent(new()
        {
            Model = model,
            SystemPromptContent = Prompt(),
            ContextManager = manager,
            Messages = [Message.User("first"), Message.Assistant("first answer"), Message.User("old"), Message.Assistant("old answer")]
        });
        Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(2, model.Requests.Count);
        Assert.True(model.CountRequests.Count >= 4);
        Assert.All(model.CountRequests, AssertPrompt);
        Assert.All(model.Requests, AssertPrompt);
    }

    [Fact]
    public async Task LocalTokenEstimateMatchesPlainProjectionWithoutCountingCacheMarkersTwice()
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        var typed = await model.CountTokensAsync(request);
        var plain = await model.CountTokensAsync(new ModelRequest(request.Messages, "first 雪\nsecond", request.Tools));
        Assert.Equal(plain, typed);
        AssertPrompt(request);
    }

    [Fact]
    public async Task SharedSkillsKeepIndependentAgentPromptRepresentations()
    {
        var shared = new AgentSkills(new() { Skills = [] });
        var firstModel = new Responses();
        var secondModel = new Responses();
        await using var first = new Agent(new() { Model = firstModel, SystemPromptContent = Prompt(), Plugins = [shared] });
        await using var second = new Agent(new() { Model = secondModel, SystemPrompt = "second plain", Plugins = [shared] });
        await first.InvokeAsync("first question");
        await second.InvokeAsync("second question");
        Assert.Equal(4, Assert.Single(firstModel.Requests).SystemPromptContent!.Count);
        Assert.StartsWith("first 雪\nsecond\n<available_skills>", firstModel.Requests[0].SystemPrompt);
        Assert.Null(Assert.Single(secondModel.Requests).SystemPromptContent);
        Assert.StartsWith("second plain\n\n<available_skills>", secondModel.Requests[0].SystemPrompt);
        Assert.Equal(3, first.SystemPromptContent!.Count);
        Assert.Equal("second plain", second.SystemPrompt);
    }

    [Fact]
    public async Task TypedAgentsAndChildrenContinueBorrowingTheModel()
    {
        var model = new DisposableResponses();
        await using (var parent = new Agent(new() { Model = model, SystemPromptContent = Prompt() }))
        {
            await using var child = parent.CreateChild();
            await parent.InvokeAsync("parent");
            await child.InvokeAsync("child");
        }
        Assert.Equal(0, model.Disposals);
        Assert.All(model.Requests, AssertPrompt);
        model.Dispose();
        Assert.Equal(1, model.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationAndModelTelemetryKeepExistingProjectionAndRedactionPolicy(bool allowInstructions)
    {
        var previous = Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN");
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN",
            "gen_ai_latest_experimental,gen_ai_span_attributes_only,gen_ai_unredacted_attributes=" + (allowInstructions ? "gen_ai.system_instructions" : ""));
        try
        {
            var spans = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = spans.Enqueue
            };
            ActivitySource.AddActivityListener(listener);
            await using var agent = new Agent(new() { Model = new Responses(), SystemPromptContent = Prompt() });
            await agent.InvokeAsync("question");
            var relevant = spans.Where(span => span.DisplayName == "chat" || span.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, relevant.Length);
            foreach (var span in relevant)
            {
                var instructions = Assert.IsType<string>(span.GetTagItem("gen_ai.system_instructions"));
                if (allowInstructions)
                {
                    Assert.Contains("first", instructions);
                    Assert.Contains("second", instructions);
                    Assert.DoesNotContain("caller-owned", instructions);
                }
                else Assert.Equal("[REDACTED]", instructions);
            }
        }
        finally { Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", previous); }
    }

    private static IReadOnlyList<ContentBlock> Prompt() => [new TextBlock("first 雪"), new CachePointBlock("caller-owned"), new TextBlock("second")];

    private static void AssertPrompt(ModelRequest request)
    {
        Assert.Equal("first 雪\nsecond", request.SystemPrompt);
        Assert.NotNull(request.SystemPromptContent);
        Assert.Collection(request.SystemPromptContent,
            block => Assert.Equal("first 雪", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal("caller-owned", Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("second", Assert.IsType<TextBlock>(block).Text));
    }

    private class Responses : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal Func<int, Message> Reply { get; init; } = _ => Message.Assistant("answer");
        public virtual async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            await Task.Yield();
            var message = Reply(index);
            yield return new ModelMessageStartEvent();
            foreach (var (block, offset) in message.Content.Select((block, offset) => (block, offset)))
            {
                yield return block is ToolUseBlock call ? new ModelContentBlockStartEvent(offset, call)
                    : new ModelContentBlockStartEvent(offset) { ContentBlock = block };
                yield return new ModelContentBlockStopEvent(offset);
            }
            yield return new ModelMessageStopEvent(message.Content.Any(block => block is ToolUseBlock) ? StopReasons.ToolUse : StopReasons.EndTurn)
            {
                ModelState = this is IStatefulModel ? new Dictionary<string, JsonElement> { ["server-state"] = JsonSerializer.SerializeToElement(index) } : null
            };
        }
    }

    private sealed class StatefulResponses : Responses, IStatefulModel { public bool Stateful => true; }
    private sealed class DisposableResponses : Responses, IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class CountingResponses : Responses, IModelTokenCounter
    {
        internal List<ModelRequest> CountRequests { get; } = [];
        internal bool OverflowFirst { get; init; }
        private bool _overflowed;
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountRequests.Add(request);
            return new(request.Messages.Sum(message => (long)message.Text.Length));
        }
        public override async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (OverflowFirst && !_overflowed)
            {
                _overflowed = true;
                Requests.Add(request);
                await Task.Yield();
                throw new ContextWindowOverflowException("owned fake overflow before any model event");
            }
            await foreach (var value in base.StreamAsync(request, cancellationToken)) yield return value;
        }
    }

    private sealed class RemoveOldExchangeOnOverflow : ContextStrategy
    {
        public override string Name => "typed-prompt-overflow-test";
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Overflow || context.Messages.Count < 5) return new(false);
            context.Messages.RemoveRange(2, 2);
            return new(true);
        }
    }
}
