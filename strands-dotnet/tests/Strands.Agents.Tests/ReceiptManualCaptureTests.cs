using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests;

public sealed class ReceiptManualCaptureTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ManualGoalCaptureAndRestorePreserveAnExistingReceipt()
    {
        await using var model = new ScriptStatefulModel(Output("first", "state-2"), Output("second", "state-2"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        await agent.LoadSnapshotAsync(Received());
        AgentSnapshot? captured = null;
        var restored = false;
        using var before = agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            captured ??= agent.CaptureGoalSnapshot(value);
            return default;
        });
        using var after = agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (!restored)
            {
                Assert.Null(value.Error);
                agent.State.Set("survives", true);
                agent.RestoreGoalSnapshot(value, captured!);
                restored = true;
            }
            return default;
        });

        await agent.InvokeAsync(null);

        Assert.NotNull(captured);
        Assert.Equal("2.0", captured.SchemaVersion);
        Assert.Equal(new ModelInputReceipt(1, "consumed-u", "discarded"), captured.Data["modelInputReceipt"].Deserialize<ModelInputReceipt>(Web));
        Assert.False(captured.Data.ContainsKey("state"));
        await agent.InvokeAsync(null);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request =>
        {
            Assert.Empty(request.Messages);
            Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        });
        Assert.True(agent.State.Get<bool>("survives"));
        Assert.Equal(2, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task ManualGoalVersionOneRestoreClearsAReceiptCreatedAfterItsBaseline()
    {
        var store = new RecordingStore();
        await using var model = new ScriptStatefulModel(Text("discarded", "state-1"), Output("second", "state-2"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ModelState = ProviderState("seed"),
            StructuredOutput = Format(),
            SessionStore = store,
            SessionId = "goal"
        });
        AgentSnapshot? captured = null;
        var restored = false;
        using var before = agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            captured ??= agent.CaptureGoalSnapshot(value);
            return default;
        });
        using var after = agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (!restored)
            {
                Assert.Null(value.Error);
                Assert.Equal(StopReasons.MaxTurns, value.Result!.StopReason);
                Assert.NotNull(store.Saved.Last().ModelInputReceipt);
                agent.RestoreGoalSnapshot(value, captured!);
                restored = true;
            }
            return default;
        });

        var first = await agent.InvokeAsync("question", new() { MaxTurns = 1 });

        Assert.Equal(StopReasons.MaxTurns, first.StopReason);
        Assert.Equal("1.0", captured!.SchemaVersion);
        Assert.False(captured.Data.ContainsKey("modelInputReceipt"));
        await agent.InvokeAsync(null);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request =>
        {
            Assert.Equal("question", Assert.Single(request.Messages).Text);
            Assert.Equal("seed", request.ModelState!["responseId"].GetString());
        });
        Assert.Equal(2, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task ResetAgentToolBaselinePreservesReceiptAcrossLaterExternalChanges()
    {
        await using var model = new ScriptStatefulModel(Output("external", "state-2"), Output("first", "state-2"), Output("second", "state-2"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        await using var parent = new Agent(new() { Model = new UnusedModel() });
        await agent.LoadSnapshotAsync(Received());
        agent.State.Set("marker", "creation");
        var tool = Assert.IsAssignableFrom<IRichTool>(agent.AsTool(new AgentToolOptions { Name = "worker" }));
        await agent.InvokeAsync(null);
        agent.State.Set("marker", "later");

        var first = await Call(tool, parent, "first fresh input", "first-call");

        Assert.Equal(ToolResultStatus.Success, first.Status);
        Assert.Equal("first fresh input", Assert.Single(model.Requests[1].Messages).Text);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal("creation", agent.State.Get<string>("marker"));
        var second = await Call(tool, parent, "second fresh input", "second-call");
        Assert.Equal(ToolResultStatus.Success, second.Status);
        Assert.Equal("second fresh input", Assert.Single(model.Requests[2].Messages).Text);
        Assert.Equal("state-1", model.Requests[2].ModelState!["responseId"].GetString());
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task PreservedAgentToolUsesItsCurrentReceiptWithoutResettingTheConversation()
    {
        await using var model = new ScriptStatefulModel(Output("answer", "state-2"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        await using var parent = new Agent(new() { Model = new UnusedModel() });
        await agent.LoadSnapshotAsync(Received());
        var tool = Assert.IsAssignableFrom<IRichTool>(agent.AsTool(new AgentToolOptions { Name = "worker", PreserveContext = true }));

        var result = await Call(tool, parent, "fresh input", "call");

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var request = Assert.Single(model.Requests);
        Assert.Equal("fresh input", Assert.Single(request.Messages).Text);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Equal(1, agent.Messages.Count(message => message.TrackingId == "consumed-u"));
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task LegacyFreshChildStartsWithoutTheParentsPendingConsumptionFact()
    {
        await using var model = new ScriptStatefulModel(Output("child-answer", "state-2"));
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });
        await using var parent = new Agent(new() { Model = new UnusedModel() });
        await agent.LoadSnapshotAsync(Received());
        var tool = agent.AsTool("legacy_worker");
        var input = JsonSerializer.SerializeToElement(new { task = "child task" });

        await tool.InvokeAsync(input, new(parent, new("legacy-call", tool.Spec.Name, input), new Dictionary<string, object?>()));

        var request = Assert.Single(model.Requests);
        Assert.Equal("child task", Assert.Single(request.Messages).Text);
        Assert.Empty(request.ModelState!);
        var original = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal("2.0", original.SchemaVersion);
        Assert.Equal("state-1", original.Data["modelState"].GetProperty("responseId").GetString());
        Assert.Equal(new ModelInputReceipt(1, "consumed-u", "discarded"), original.Data["modelInputReceipt"].Deserialize<ModelInputReceipt>(Web));
        Assert.Equal("consumed-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task FreshNestedJournalRoundTripsReceiptPairThroughSyntheticToolPause()
    {
        using var files = new FileFixture();
        var store = new FileSessionStore(files.Root);
        var effects = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            var answer = context.Interrupt("approval").GetString();
            Assert.Equal("approved", answer);
            effects++;
            return new ValueTask<string>("approved");
        });
        Interrupt question;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var firstModel = new ScriptStatefulModel())
        await using (var firstChild = new Agent(new() { Model = firstModel, Middleware = SyntheticApproval(), StructuredOutput = Format(), Tools = [approve] }))
        {
            await firstChild.LoadSnapshotAsync(Received(), deadline.Token);
            await using var firstParent = Parent(firstChild);
            var paused = await firstParent.InvokeAsync("request", cancellationToken: deadline.Token);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            question = Assert.Single(paused.Interrupts);
            Assert.Empty(firstModel.Requests);
            Assert.Equal(0, firstModel.IteratorDisposals);
            Assert.Equal(0, effects);
            var parentSnapshot = Assert.IsType<SessionSnapshot>(await store.LoadAsync("parent", deadline.Token));
            Assert.Equal(1, parentSnapshot.SchemaVersion);
            var childSnapshot = parentSnapshot.InterruptState!.Context[AgentToolInterruptJournal.TurnsKey]
                .GetProperty("parent-call").Deserialize<AgentSnapshot>(Web)!;
            Assert.Equal("2.0", childSnapshot.SchemaVersion);
            Assert.Equal(new ModelInputReceipt(1, "consumed-u", "discarded"), childSnapshot.Data["modelInputReceipt"].Deserialize<ModelInputReceipt>(Web));
            Assert.Equal("state-1", childSnapshot.Data["modelState"].GetProperty("responseId").GetString());
            Assert.Equal(JsonValueKind.Object, childSnapshot.Data["interruptState"].ValueKind);
            Assert.Equal(0, firstModel.Disposals);
        }

        await using var nextModel = new ScriptStatefulModel(Output("child-answer", "state-2"));
        await using var nextChild = new Agent(new() { Model = nextModel, StructuredOutput = Format(), Tools = [approve] });
        await using var nextParent = Parent(nextChild);
        var result = await nextParent.InvokeAsync(null, new()
        {
            InterruptResponses = [new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved"))]
        }, deadline.Token);

        Assert.Equal("finished", result.ToString());
        Assert.Equal(1, effects);
        Assert.Empty(result.Interrupts);
        Assert.Null(nextParent.InterruptState);
        Assert.Null(nextChild.InterruptState);
        var request = Assert.Single(nextModel.Requests);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Equal(new[] { Role.Assistant, Role.User }, request.Messages.Select(message => message.Role));
        Assert.DoesNotContain(request.Messages, message => message.TrackingId == "consumed-u");
        Assert.Equal("approve-call", Assert.Single(request.Messages[0].Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal("approved", Assert.Single(request.Messages[1].Content.OfType<ToolResultBlock>()).Content);
        var saved = Assert.IsType<SessionSnapshot>(await store.LoadAsync("parent", deadline.Token));
        Assert.Null(saved.InterruptState);
        Assert.Equal(1, nextModel.IteratorDisposals);
        Assert.Equal(0, nextModel.Disposals);

        Agent Parent(Agent child)
        {
            var specification = new ToolSpec("specialist", "Child", JsonSerializer.SerializeToElement(new { type = "object" }));
            var tool = child.AsFreshChildTool(specification, null, (childResult, context) =>
            {
                if (childResult.StopReason != StopReasons.ToolUse || childResult.StructuredOutput is not { } value || value.GetProperty("value").GetInt32() != 7)
                    throw new InvalidOperationException("The child must complete its accepted structured response.");
                return new(context.ToolUse.ToolUseId, "finished");
            });
            return new(new() { Model = new ParentModel(), Tools = [tool], SessionStore = store, SessionId = "parent" });
        }
    }

    private static Task<ToolResultBlock> Call(IRichTool tool, Agent parent, string text, string id)
    {
        var input = JsonSerializer.SerializeToElement(new { input = text });
        return tool.InvokeResultAsync(input, new(parent, new(id, tool.Spec.Name, input), new Dictionary<string, object?>())).AsTask();
    }

    private static AgentSnapshot Received() => new(new Dictionary<string, JsonElement>
    {
        ["messages"] = JsonSerializer.SerializeToElement(new[] { new Message(Role.User, [new TextBlock("consumed input")], "consumed-u") }, Web),
        ["modelState"] = JsonSerializer.SerializeToElement(ProviderState("state-1")),
        ["modelHistoryCount"] = JsonSerializer.SerializeToElement(0),
        ["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, "consumed-u", "discarded"), Web)
    }, schemaVersion: "2.0");

    private static StructuredOutputOptions<JsonElement> Format() => new()
    {
        Behavior = StructuredOutputBehavior.TypeScript,
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };

    private static Dictionary<string, JsonElement> ProviderState(string id) => new() { ["responseId"] = JsonSerializer.SerializeToElement(id) };
    private static Turn Output(string id, string state) => new(state, StopReasons.ToolUse, [new ToolUseBlock(id, "strands_structured_output", JsonSerializer.SerializeToElement(new { value = 7 }))]);
    private static Turn Text(string text, string state) => new(state, StopReasons.EndTurn, [new TextBlock(text)]);
    private sealed record Turn(string State, string Reason, ContentBlock[] Blocks);

    private static MiddlewareRegistry SyntheticApproval()
    {
        var middleware = new MiddlewareRegistry();
        var calls = 0;
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Once);
        return middleware;
        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Once(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // A real provider tool-use would acknowledge history and clear the receipt before the pause.
            if (++calls == 1)
            {
                var message = new Message(Role.Assistant, [new ToolUseBlock("approve-call", "approve", JsonSerializer.SerializeToElement(new { }))], "synthetic-a");
                yield return new MiddlewareResultEvent<InvokeModelResult>(new(message, StopReasons.ToolUse, new()));
                yield break;
            }
            await foreach (var item in next(context).WithCancellation(cancellationToken)) yield return item;
        }
    }

    private sealed class ScriptStatefulModel(params Turn[] turns) : IStatefulModel, IAsyncDisposable
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            });
            if (Requests.Count > turns.Length) throw new InvalidOperationException("No extra fake provider call is allowed.");
            var turn = turns[Requests.Count - 1];
            try
            {
                yield return new ModelMessageStartEvent();
                for (var index = 0; index < turn.Blocks.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (turn.Blocks[index] is ToolUseBlock call)
                    {
                        yield return new ModelContentBlockStartEvent(index, call);
                        yield return new ModelContentBlockStopEvent(index);
                    }
                    else
                    {
                        yield return new ModelContentBlockStartEvent(index) { ContentBlock = turn.Blocks[index] };
                        yield return new ModelContentBlockStopEvent(index) { ContentBlock = turn.Blocks[index] };
                    }
                }
                yield return new ModelMessageStopEvent(turn.Reason) { ModelState = ProviderState(turn.State) };
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The ToolContext parent must not open a model call.");
    }

    private sealed class ParentModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().LastOrDefault();
            yield return new ModelMessageStartEvent();
            if (accepted is null)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("parent-call", "specialist", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: accepted.Content);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingStore : ISessionStore
    {
        internal List<SessionSnapshot> Saved { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.SerializeToUtf8Bytes(snapshot, Web), Web)!);
            return default;
        }
    }

    private sealed class FileFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "strands-receipt-manual-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }
}
