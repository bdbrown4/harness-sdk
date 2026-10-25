using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelIndependentImportHistoryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, "snapshot")]
    [InlineData(ModelHistoryBehavior.TypeScript, "session")]
    [InlineData(ModelHistoryBehavior.Python, "snapshot")]
    [InlineData(ModelHistoryBehavior.Python, "session")]
    public async Task IndependentBatchRejectsAnUnfinishedDifferentOwnedHistoryBeforeEffects(ModelHistoryBehavior profile, string seam)
    {
        var unfinished = new Message(Role.Assistant,
            [new ToolUseBlock("unresolved-X", "approve", Json("{}"))], "retained-request-X");
        await AssertRejectedBeforeEffects(profile, seam, [unfinished]);
    }

    [Theory]
    [InlineData("ordinary-user")]
    [InlineData("completed-python-prefix")]
    public async Task PythonIndependentBatchRejectsNonemptyCompletedHistoryBeforeEffects(string kind)
    {
        Message retained = kind == "ordinary-user"
            ? OrdinaryHistory()
            : new Message(Role.User, [new ToolResultBlock("completed-X", "already completed")], "retained-result-X");
        await AssertRejectedBeforeEffects(ModelHistoryBehavior.Python, "session", [retained]);
    }

    [Fact]
    public async Task TypeScriptIndependentBatchCanResumeAfterCompleteOrdinaryHistory()
    {
        var producerModel = new FiniteModel(pause: true);
        var resumedModel = new FiniteModel(pause: false);
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(producerModel);
            var imported = IndependentSeed(seed, ModelHistoryBehavior.TypeScript) with { Messages = [OrdinaryHistory()] };
            var store = new MemoryStore { Incoming = imported };
            var effects = 0;
            target = new(new()
            {
                Model = resumedModel,
                ModelHistoryBehavior = ModelHistoryBehavior.TypeScript,
                SessionStore = store,
                SessionId = "independent-import",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools = [Approve(context => { effects++; return context.Interrupt("approval").GetBoolean() ? "approved" : "declined"; })]
            });

            var result = await target.InvokeAsync(null, Answer(seed));

            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("finished", result.ToString());
            Assert.Equal(1, effects);
            Assert.Equal(1, resumedModel.StreamCalls);
            var sent = Assert.Single(resumedModel.Requests);
            Assert.Collection(sent,
                message =>
                {
                    Assert.Equal(Role.User, message.Role);
                    Assert.Equal("retained ordinary", message.Text);
                    Assert.Equal("retained-user", message.TrackingId);
                },
                message =>
                {
                    Assert.Equal(Role.Assistant, message.Role);
                    Assert.Equal(seed.Session.InterruptState!.AssistantMessage.TrackingId, message.TrackingId);
                    Assert.Equal("pending-Y", Assert.Single(message.Content.OfType<ToolUseBlock>()).ToolUseId);
                },
                message =>
                {
                    Assert.Equal(Role.User, message.Role);
                    var toolResult = Assert.Single(message.Content.OfType<ToolResultBlock>());
                    Assert.Equal("pending-Y", toolResult.ToolUseId);
                    Assert.Equal("approved", toolResult.Content);
                    Assert.Equal(ToolResultStatus.Success, toolResult.Status);
                });
            Assert.Null(target.InterruptState);
            Assert.True(target.State.Get<bool>("seedOnly"));
            Assert.Equal(42, target.ModelState.Get<int>("seedProvider"));
            Assert.True(store.Saves > 0);
            var saved = Assert.IsType<SessionSnapshot>(store.LastSaved);
            Assert.Equal(ModelHistoryBehavior.TypeScript, saved.HistoryProfile);
            Assert.Null(saved.InterruptState);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.Equal(4, saved.Messages.Count);
            Assert.Equal("finished", saved.Messages[3].Text);
            await target.DisposeAsync(); target = null;
            Assert.Equal(0, resumedModel.Disposals);
            Assert.Equal(0, producerModel.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, resumedModel, producerModel); }
    }

    private static async Task AssertRejectedBeforeEffects(ModelHistoryBehavior profile, string seam, Message[] retained)
    {
        var producerModel = new FiniteModel(pause: true);
        var resumedModel = new FiniteModel(pause: false);
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var seed = await CapturePublicPause(producerModel);
            var independent = IndependentSeed(seed, profile);
            // The independently owned batch is valid with empty history. Only the retained history is changed below.
            var imported = independent with { Messages = retained };
            Assert.NotEqual("pending-Y", retained[0].Content.OfType<ToolUseBlock>().FirstOrDefault()?.ToolUseId);
            var store = new MemoryStore { Incoming = seam == "session" ? imported : null };
            var incomingBefore = JsonSerializer.SerializeToElement(store.Incoming, Web);
            var checkpoint = WithIndependentHistory(seed.Checkpoint, independent, retained);
            var checkpointBefore = JsonSerializer.SerializeToElement(checkpoint, Web);
            var hooks = new HookRegistry();
            var initialized = 0;
            var before = 0;
            var beforeModel = 0;
            var added = 0;
            var effects = 0;
            hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
            hooks.Register<BeforeInvocationEvent>((_, _) => { before++; return default; });
            hooks.Register<BeforeModelCallEvent>((_, _) => { beforeModel++; return default; });
            hooks.Register<MessageAddedEvent>((_, _) => { added++; return default; });
            var local = new Message(Role.User, [new TextBlock("local history")], "local-user");
            target = new(new()
            {
                Id = "local-owner",
                Model = resumedModel,
                ModelHistoryBehavior = profile,
                Hooks = hooks,
                Messages = [local],
                SystemPrompt = "local-system",
                State = new Dictionary<string, JsonElement> { ["local"] = Json("true") },
                ModelState = new Dictionary<string, JsonElement> { ["localProvider"] = Json("17") },
                SessionStore = store,
                SessionId = "independent-import",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools = [Approve(context => { effects++; return context.Interrupt("approval").GetBoolean() ? "approved" : "declined"; })]
            });

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                if (seam == "snapshot") await target.LoadSnapshotAsync(checkpoint);
                await target.InvokeAsync(null, Answer(seed));
            });

            Assert.Equal(0, initialized);
            Assert.Equal(0, before);
            Assert.Equal(0, beforeModel);
            Assert.Equal(0, added);
            Assert.Equal(0, effects);
            Assert.Equal(0, resumedModel.StreamCalls);
            Assert.Equal(0, resumedModel.CountCalls);
            Assert.Empty(resumedModel.Requests);
            Assert.Null(target.InterruptState);
            Assert.Equal("local-owner", target.Id);
            Assert.Equal("local-system", target.SystemPrompt);
            var actualLocal = Assert.Single(target.Messages);
            Assert.Equal("local-user", actualLocal.TrackingId);
            Assert.Equal("local history", actualLocal.Text);
            Assert.True(Assert.Single(target.State.GetAll()).Value.GetBoolean());
            Assert.True(target.State.Get<bool>("local"));
            Assert.Equal("localProvider", Assert.Single(target.ModelState.GetAll()).Key);
            Assert.Equal(17, target.ModelState.Get<int>("localProvider"));
            Assert.Equal(seam == "session" ? 1 : 0, store.Loads);
            Assert.Equal(0, store.Saves);
            Assert.Null(store.LastSaved);
            Assert.True(JsonElement.DeepEquals(incomingBefore, JsonSerializer.SerializeToElement(store.Incoming, Web)));
            Assert.True(JsonElement.DeepEquals(checkpointBefore, JsonSerializer.SerializeToElement(checkpoint, Web)));
            await target.DisposeAsync(); target = null;
            Assert.Equal(0, resumedModel.Disposals);
            Assert.Equal(0, producerModel.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, resumedModel, producerModel); }
    }

    private static async Task<Seed> CapturePublicPause(FiniteModel model)
    {
        var store = new MemoryStore();
        var effects = 0;
        await using var producer = new Agent(new()
        {
            Id = "seed-owner",
            Model = model,
            ModelHistoryBehavior = ModelHistoryBehavior.Native,
            SystemPrompt = "stored-system",
            State = new Dictionary<string, JsonElement> { ["seedOnly"] = Json("true") },
            SessionStore = store,
            SessionId = "independent-import",
            ToolExecution = ToolExecutionStrategy.Sequential,
            Tools = [Approve(context => { effects++; return context.Interrupt("approval").ToString(); })]
        });
        var result = await producer.InvokeAsync("seed input");
        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        var question = Assert.Single(result.Interrupts);
        Assert.Equal("pending-Y", question.ToolUseId);
        Assert.Equal(1, effects);
        Assert.Equal(1, model.StreamCalls);
        var saved = Assert.IsType<SessionSnapshot>(store.LastSaved);
        Assert.Equal(ModelHistoryBehavior.Native, saved.HistoryProfile);
        Assert.Null(saved.ModelHistoryCount);
        Assert.Null(saved.ModelInputReceipt);
        var pending = Assert.IsType<InterruptState>(saved.InterruptState);
        Assert.False(pending.AssistantMessageIsIndependent);
        Assert.Equal(question.Id, Assert.Single(pending.Interrupts).Id);
        Assert.Equal(pending.AssistantMessage.TrackingId, saved.Messages[1].TrackingId);
        Assert.Equal("pending-Y", Assert.Single(pending.AssistantMessage.Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal(42, saved.ModelState!["seedProvider"].GetInt32());
        var checkpoint = await producer.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal("agent", checkpoint.Scope);
        Assert.Equal("1.0", checkpoint.SchemaVersion);
        return new(saved, checkpoint, question.Id);
    }

    private static SessionSnapshot IndependentSeed(Seed seed, ModelHistoryBehavior profile)
        => seed.Session with
        {
            HistoryProfile = profile,
            Messages = Array.Empty<Message>(),
            InterruptState = seed.Session.InterruptState! with { AssistantMessageIsIndependent = true }
        };

    private static AgentSnapshot WithIndependentHistory(AgentSnapshot original, SessionSnapshot independent, Message[] retained)
    {
        var fields = original.Data.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        fields["historyProfile"] = JsonSerializer.SerializeToElement(independent.HistoryProfile, Web);
        fields["interruptState"] = JsonSerializer.SerializeToElement(independent.InterruptState, Web);
        fields["messages"] = JsonSerializer.SerializeToElement(retained, Web);
        return new(fields, original.AppData, original.Scope, original.SchemaVersion, original.CreatedAt);
    }

    private static InvocationOptions Answer(Seed seed) => new()
    {
        InterruptResponses = [new(seed.QuestionId, Json("true"))]
    };
    private static Message OrdinaryHistory() => new(Role.User, [new TextBlock("retained ordinary")], "retained-user");
    private static FunctionTool Approve(Func<ToolContext, string> body)
        => new(new("approve", "approve", Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private static async ValueTask DisposeOwned(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var index = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["IndependentImport.Cleanup." + index++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed record Seed(SessionSnapshot Session, AgentSnapshot Checkpoint, string QuestionId);

    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Incoming { get; init; }
        internal SessionSnapshot? LastSaved { get; private set; }
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Loads++;
            return new(Incoming is null ? null : Clone(Incoming));
        }
        public ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Saves++; LastSaved = Clone(snapshot); return default;
        }
        private static SessionSnapshot Clone(SessionSnapshot snapshot)
            => JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot, Web), Web)!;
    }

    private sealed class FiniteModel(bool pause) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => false;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int Disposals { get; private set; }
        internal List<IReadOnlyList<Message>> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("Each finite model permits one public provider request.");
            Requests.Add(request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
            return Produce(cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            token.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (pause)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("pending-Y", "approve", Json("{}")));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn)
            {
                ModelState = new Dictionary<string, JsonElement> { ["seedProvider"] = Json("42") }
            };
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
