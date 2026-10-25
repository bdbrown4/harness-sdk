using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.SessionRepositories;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTimingCommitTests
{
    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task AcceptedStatefulAnswerAndArchiveSurviveTheSelectedTerminalResult(ExplicitCancellationTiming timing)
    {
        var directory = OwnedRoot("strands-timing-repository-");
        try
        {
            var firstEvents = Text("accepted answer");
            firstEvents[firstEvents.Length - 1] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State("receipt-one") };
            var repository = new FileSessionRepository(directory);
            await using (var first = new Agent(new()
            {
                Id = "agent",
                Model = new StatefulModel(firstEvents),
                ExplicitCancellationTiming = timing,
                Plugins = [new RepositorySessionManager("session", repository)]
            }))
            {
                first.Hooks.Register<AfterModelCallEvent>((_, _) => { first.Cancel(); return default; });
                var result = await first.InvokeAsync("first input");
                Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
                Assert.Equal("accepted answer", result.Message!.Text);
                Assert.Equal("receipt-one", first.ModelState.Get<string>("responseId"));
                Assert.Empty(first.Messages);
            }
            Assert.Equal(new[] { "first input", "accepted answer" }, (await repository.ListMessagesAsync("session", "agent")).Select(row => row.Message.Text));
            var secondEvents = Text("second answer");
            secondEvents[secondEvents.Length - 1] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State("receipt-two") };
            var secondModel = new StatefulModel(secondEvents);
            await using (var second = new Agent(new()
            {
                Id = "agent",
                Model = secondModel,
                ExplicitCancellationTiming = timing,
                Plugins = [new RepositorySessionManager("session", new FileSessionRepository(directory))]
            }))
            {
                Assert.Equal(StopReasons.EndTurn, (await second.InvokeAsync("second input")).StopReason);
                Assert.Empty(second.Messages);
            }
            var request = Assert.Single(secondModel.Requests);
            Assert.Equal("receipt-one", request.ModelState!["responseId"].GetString());
            Assert.Equal("second input", Assert.Single(request.Messages).Text);
            Assert.Equal(new long[] { 0, 1, 2, 3 }, (await repository.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task SyntheticCachedCompletionDoesNotAcknowledgeProviderStateOrSwallowTheRequest(ExplicitCancellationTiming timing)
    {
        var model = new StatefulModel(Text("not run"));
        await using var agent = new Agent(new() { Model = model, ModelState = State("prior"), ExplicitCancellationTiming = timing });
        agent.Middleware.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input")).StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal("prior", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(new[] { "input", "cached pending message" }, agent.Messages.Select(message => message.Text));

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            agent.Cancel();
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached pending message"), StopReasons.EndTurn, new()));
        }
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task DeferredRequestPrecedesAValidatedStructuredValue(ExplicitCancellationTiming timing)
    {
        var output = new StructuredOutputTool<int>(new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } }),
            Deserializer = input => input.GetProperty("value").GetInt32()
        });
        var model = new CancellationTestModel(Call(("structured", output.Spec.Name, "{\"value\":42}")));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { agent.Cancel(afterCurrentTools: true); return default; });
        var result = await agent.InvokeAsync("input", new() { StructuredOutputTool = output });
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.StructuredOutputToolUseId);
        Assert.True(output.TryGetResult("structured", out var value));
        Assert.Equal(42, value);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task FreshAgentKeepsPartialAnswersAndCompletedSiblingAcrossARequestBeforeResume(ExplicitCancellationTiming timing)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-timing-pause-" + Guid.NewGuid().ToString("N"));
        try
        {
            var effects = 0; var approvals = 0;
            var effect = Tool("effect", (_, _, _) => { effects++; return new("saved"); });
            var approve = Tool("approve", (_, context, _) => { context.Interrupt("approve"); approvals++; return new("approved"); });
            var model = new CancellationTestModel(Call(("effect", "effect", "{}"), ("first", "approve", "{}"), ("second", "approve", "{}")), Text("finished"));
            Interrupt[] questions;
            await using (var first = Create(model))
            {
                var paused = await first.InvokeAsync("input");
                Assert.Equal(StopReasons.Interrupt, paused.StopReason);
                questions = paused.Interrupts.OrderBy(question => question.ToolUseId, StringComparer.Ordinal).ToArray();
                Assert.Equal(2, questions.Length);
            }
            var beforeTools = 0;
            await using (var cancelled = Create(new CancellationTestModel(Text("not run"))))
            {
                cancelled.Hooks.Register<BeforeInvocationEvent>((_, _) => { cancelled.Cancel(); return default; });
                cancelled.Hooks.Register<BeforeToolsEvent>((_, _) => { beforeTools++; return default; });
                Assert.Equal(StopReasons.Cancelled, (await cancelled.InvokeAsync(null, new()
                { InterruptResponses = [new(questions[1].Id, JsonSerializer.SerializeToElement(true))] })).StopReason);
                Assert.Equal(timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0, beforeTools);
            }
            await using (var resumed = Create(new CancellationTestModel(Text("finished"))))
            {
                var result = await resumed.InvokeAsync(null, new() { InterruptResponses = [new(questions[0].Id, JsonSerializer.SerializeToElement(true))] });
                Assert.Equal(StopReasons.EndTurn, result.StopReason);
                Assert.Null(resumed.InterruptState);
                Assert.Equal(new[] { "effect", "first", "second" }, resumed.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Select(result => result.ToolUseId));
            }
            Assert.Equal(1, effects);
            Assert.Equal(2, approvals);

            Agent Create(IModel selected) => new(new()
            {
                Model = selected,
                Tools = [effect, approve],
                ExplicitCancellationTiming = timing,
                SessionId = "paused",
                SessionStore = new FileSessionStore(directory)
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task BatchInterruptPrecedesOnlyTheOwnedRequestAndKeepsItsDurableQuestion(ExplicitCancellationTiming timing)
    {
        var effects = 0;
        var tool = Tool("save", (_, _, _) => { effects++; return new("saved"); });
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Call(("save", "save", "{}"))), Tools = [tool], ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeToolsEvent>((value, _) => { agent.Cancel(); value.Interrupt("approval"); return default; });
        var result = await agent.InvokeAsync("input");
        Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? StopReasons.Cancelled : StopReasons.Interrupt, result.StopReason);
        Assert.Equal("approval", Assert.Single(agent.InterruptState!.Interrupts).Name);
        Assert.Equal(0, effects);
        Assert.Empty(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task CallerCancellationStillBeatsDenialAtBothHooks(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        var model = new CancellationTestModel(Text("not run"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((value, _) => { caller.Cancel(); value.CancelReason = "not authoritative"; return default; });
        var result = await agent.InvokeAsync("accepted input", cancellationToken: caller.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not authoritative");
    }

    private static IReadOnlyDictionary<string, JsonElement> State(string id) => new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement(id) };

    private static string OwnedRoot(string prefix)
    {
        var requested = Path.GetFullPath(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
        var actual = new FileSessionRepository(requested).RootPath;
        var comparable = actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + actual.Substring(8)
            : actual.StartsWith(@"\\?\", StringComparison.Ordinal) ? actual.Substring(4) : actual;
        if (!(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(requested, comparable))
            throw new InvalidOperationException("The fixture cleanup root differs from its unique selected directory.");
        return actual;
    }

    private sealed class StatefulModel(ModelStreamEvent[] events) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var value in events) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
        }
    }
}
