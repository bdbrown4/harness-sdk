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

public sealed class PrimaryModelSourceIndependentPendingTests
{
    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, true)]
    [InlineData(ModelHistoryBehavior.Python, false)]
    public async Task IndependentPendingRestoreProjectsCompletedToolsToTheNextModelRequest(ModelHistoryBehavior profile, bool reinsertAssistant)
    {
        var producerModel = new PauseModel();
        var sourceEffects = new BodyCounts();
        var resumedEffects = new BodyCounts();
        var restoredModel = new CompletionModel(sourceEffects, resumedEffects);
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var seed = await CaptureNativePairAsync(producerModel, sourceEffects);
            var store = new MemoryStore { Saved = IndependentSourceSnapshot(seed.Snapshot) };
            target = new Agent(new()
            {
                Model = restoredModel,
                ModelHistoryBehavior = profile,
                SessionStore = store,
                SessionId = "source-independent-pending",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools =
                [
                    Tool("done", _ => { resumedEffects.Done++; throw new InvalidOperationException("A completed sibling must not execute again."); }),
                    Tool("approve", context =>
                    {
                        resumedEffects.Approve++;
                        return context.Interrupt("approval").GetBoolean() ? "approved" : "declined";
                    })
                ]
            });

            await target.InitializeAsync();

            Assert.Empty(target.Messages);
            var restoredPending = Assert.IsType<InterruptState>(target.InterruptState);
            Assert.Equal(seed.Question.Id, Assert.Single(restoredPending.Interrupts).Id);
            Assert.Equal(seed.AssistantTrackingId, restoredPending.AssistantMessage.TrackingId);
            Assert.Equal("committed", Assert.Single(restoredPending.CompletedToolResults).Content);
            AssertProviderState(target.ModelState.GetAll());
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, restoredModel.StreamCalls);
            Assert.Equal(0, restoredModel.CountCalls);
            Assert.Equal(0, resumedEffects.Done);
            Assert.Equal(0, resumedEffects.Approve);

            var result = await target.InvokeAsync(null, new InvocationOptions
            {
                InterruptResponses = [new(seed.Question.Id, Json("true"))]
            });

            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("finished", result.ToString());
            Assert.Equal(1, restoredModel.StreamCalls);
            var observed = Assert.Single(restoredModel.Requests);
            Assert.Equal(1, observed.SourceDone);
            Assert.Equal(1, observed.SourceApprove);
            Assert.Equal(0, observed.ResumedDone);
            Assert.Equal(1, observed.ResumedApprove);
            AssertProviderState(observed.ModelState!);
            if (reinsertAssistant)
            {
                Assert.Collection(observed.Messages,
                    message =>
                    {
                        Assert.Equal(Role.Assistant, message.Role);
                        Assert.Equal(seed.AssistantTrackingId, message.TrackingId);
                        Assert.Equal(new[] { "call-0", "call-1" }, message.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
                        Assert.Equal(new[] { "done", "approve" }, message.Content.OfType<ToolUseBlock>().Select(call => call.Name));
                    },
                    AssertCompletedResults);
            }
            else AssertCompletedResults(Assert.Single(observed.Messages));
            Assert.Equal(1, sourceEffects.Done);
            Assert.Equal(1, sourceEffects.Approve);
            Assert.Equal(0, resumedEffects.Done);
            Assert.Equal(1, resumedEffects.Approve);
            Assert.Equal(1, producerModel.StreamCalls);
            Assert.Null(target.InterruptState);
            Assert.Empty(target.Messages);
            AssertProviderState(target.ModelState.GetAll());
            var final = Assert.IsType<SessionSnapshot>(store.Saved);
            Assert.Empty(final.Messages);
            Assert.Null(final.InterruptState);
            Assert.Null(final.ModelHistoryCount);
            Assert.Null(final.ModelInputReceipt);
            AssertProviderState(final.ModelState!);
            await target.DisposeAsync();
            target = null;
            Assert.Equal(0, producerModel.DisposeCalls);
            Assert.Equal(0, restoredModel.DisposeCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwnedAsync(primary, target, restoredModel, producerModel); }
    }

    [Fact]
    public async Task NativeRejectsAnExplicitIndependentPendingDomainBeforeAnyWork()
    {
        var producerModel = new PauseModel();
        var sourceEffects = new BodyCounts();
        var resumedEffects = new BodyCounts();
        var restoredModel = new CompletionModel(sourceEffects, resumedEffects);
        Agent? target = null;
        Exception? primary = null;
        try
        {
            var seed = await CaptureNativePairAsync(producerModel, sourceEffects);
            var store = new MemoryStore { Saved = IndependentSourceSnapshot(seed.Snapshot) };
            target = new Agent(new()
            {
                Model = restoredModel,
                ModelHistoryBehavior = ModelHistoryBehavior.Native,
                Id = "native-admission-target",
                State = new Dictionary<string, JsonElement> { ["local"] = Json("true") },
                ModelState = new Dictionary<string, JsonElement> { ["localProvider"] = Json("\"unchanged\"") },
                SessionStore = store,
                SessionId = "source-independent-pending",
                Tools =
                [
                    Tool("done", _ => { resumedEffects.Done++; throw new InvalidOperationException("Admission cannot run a tool."); }),
                    Tool("approve", _ => { resumedEffects.Approve++; throw new InvalidOperationException("Admission cannot run a tool."); })
                ]
            });

            await Assert.ThrowsAsync<InvalidDataException>(() => target.InitializeAsync());

            Assert.Equal(1, store.Loads);
            Assert.Equal(0, store.Saves);
            Assert.Empty(target.Messages);
            Assert.Null(target.InterruptState);
            Assert.Equal("native-admission-target", target.Id);
            Assert.True(target.State.Get<bool>("local"));
            Assert.False(target.State.GetAll().ContainsKey("imported"));
            var provider = Assert.Single(target.ModelState.GetAll());
            Assert.Equal("localProvider", provider.Key);
            Assert.Equal("unchanged", provider.Value.GetString());
            Assert.Equal(0, restoredModel.StreamCalls);
            Assert.Equal(0, restoredModel.CountCalls);
            Assert.Empty(restoredModel.Requests);
            Assert.Equal(0, resumedEffects.Done);
            Assert.Equal(0, resumedEffects.Approve);
            await target.DisposeAsync();
            target = null;
            Assert.Equal(0, producerModel.DisposeCalls);
            Assert.Equal(0, restoredModel.DisposeCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwnedAsync(primary, target, restoredModel, producerModel); }
    }

    [Fact]
    public void LegacyPairedPendingJsonStaysUnmarkedAndRoundTripsItsOwnedData()
    {
        var assistant = new Message(Role.Assistant,
        [
            new ToolUseBlock("call-0", "done", Json("{\"value\":7}")),
            new ToolUseBlock("call-1", "approve", Json("{}"))
        ]);
        var original = new InterruptState(assistant,
            [new("call-0", "committed")],
            [new("saved-question", "approval", "call-1", source: InterruptSource.Tool)],
            [new("saved-question", Json("true"))]);
        foreach (var json in new[] { new JsonSerializerOptions(), CamelCaseJson() })
        {
            var wire = JsonSerializer.SerializeToElement(original, json);
            Assert.False(wire.TryGetProperty("AssistantMessageIsIndependent", out _));
            Assert.False(wire.TryGetProperty("assistantMessageIsIndependent", out _));
            Assert.False(wire.TryGetProperty("SourceHistoryOwned", out _));
            Assert.False(wire.TryGetProperty("sourceHistoryOwned", out _));
            var restored = Assert.IsType<InterruptState>(wire.Deserialize<InterruptState>(json));
            Assert.Equal(assistant.TrackingId, restored.AssistantMessage.TrackingId);
            Assert.Equal("call-0", Assert.Single(restored.CompletedToolResults).ToolUseId);
            Assert.Equal("committed", Assert.Single(restored.CompletedToolResults).Content);
            Assert.Equal("saved-question", Assert.Single(restored.Interrupts).Id);
            var answer = Assert.Single(restored.Responses);
            Assert.Equal("saved-question", answer.InterruptId);
            Assert.True(answer.Response.GetBoolean());
            Assert.True(JsonElement.DeepEquals(wire, JsonSerializer.SerializeToElement(restored, json)));
        }
    }

    private static SessionSnapshot IndependentSourceSnapshot(SessionSnapshot paired)
    {
        Assert.Null(paired.ModelHistoryCount);
        Assert.Null(paired.ModelInputReceipt);
        var json = CamelCaseJson();
        var fields = JsonSerializer.SerializeToElement(paired.InterruptState!, json).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        fields.Add("assistantMessageIsIndependent", Json("true"));
        var wire = JsonSerializer.SerializeToElement(fields, json);
        Assert.True(wire.GetProperty("assistantMessageIsIndependent").GetBoolean());
        return paired with
        {
            Messages = Array.Empty<Message>(),
            InterruptState = Assert.IsType<InterruptState>(wire.Deserialize<InterruptState>(json))
        };
    }

    private static async Task<Seed> CaptureNativePairAsync(PauseModel model, BodyCounts effects)
    {
        var store = new MemoryStore();
        Agent? producer = null;
        Exception? primary = null;
        try
        {
            producer = new Agent(new()
            {
                Model = model,
                ModelHistoryBehavior = ModelHistoryBehavior.Native,
                State = new Dictionary<string, JsonElement> { ["imported"] = Json("7") },
                SessionStore = store,
                SessionId = "source-independent-pending",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools =
                [
                    Tool("done", _ => { effects.Done++; return "committed"; }),
                    Tool("approve", context => { effects.Approve++; return context.Interrupt("approval").ToString(); })
                ]
            });
            var paused = await producer.InvokeAsync("task");
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            var question = Assert.Single(paused.Interrupts);
            Assert.Equal("call-1", question.ToolUseId);
            var saved = Assert.IsType<SessionSnapshot>(store.Saved);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            var pending = Assert.IsType<InterruptState>(saved.InterruptState);
            Assert.Equal(question.Id, Assert.Single(pending.Interrupts).Id);
            Assert.Equal("call-0", Assert.Single(pending.CompletedToolResults).ToolUseId);
            Assert.Equal("committed", Assert.Single(pending.CompletedToolResults).Content);
            Assert.Collection(saved.Messages,
                message => { Assert.Equal(Role.User, message.Role); Assert.Equal("task", message.Text); },
                message => { Assert.Equal(Role.Assistant, message.Role); Assert.Equal(pending.AssistantMessage.TrackingId, message.TrackingId); });
            AssertProviderState(saved.ModelState!);
            Assert.Equal(1, effects.Done);
            Assert.Equal(1, effects.Approve);
            Assert.Equal(1, model.StreamCalls);
            await producer.DisposeAsync();
            producer = null;
            Assert.Equal(0, model.DisposeCalls);
            return new(saved, question, pending.AssistantMessage.TrackingId);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwnedAsync(primary, producer); }
    }

    private static void AssertCompletedResults(Message message)
    {
        Assert.Equal(Role.User, message.Role);
        Assert.Equal(2, message.Content.Count);
        Assert.Collection(message.Content.OfType<ToolResultBlock>(),
            result => { Assert.Equal("call-0", result.ToolUseId); Assert.Equal("committed", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); },
            result => { Assert.Equal("call-1", result.ToolUseId); Assert.Equal("approved", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); });
    }

    private static void AssertProviderState(IReadOnlyDictionary<string, JsonElement> state)
    {
        Assert.Equal(2, state.Count);
        Assert.Equal("saved-provider", state["responseId"].GetString());
        Assert.Equal(23, state["nested"].GetProperty("sequence").GetInt32());
    }

    private static Dictionary<string, JsonElement> ProviderState() => new()
    {
        ["responseId"] = Json("\"saved-provider\""),
        ["nested"] = Json("{\"sequence\":23}")
    };

    private static async ValueTask DisposeOwnedAsync(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var secondary = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup)) failure.Data["IndependentPending.Cleanup." + secondary++] = cleanup;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static JsonSerializerOptions CamelCaseJson() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FunctionTool Tool(string name, Func<ToolContext, string> body)
        => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));

    private sealed record Seed(SessionSnapshot Snapshot, Interrupt Question, string AssistantTrackingId);
    private sealed class BodyCounts { internal int Done; internal int Approve; }
    private sealed record RequestObservation(IReadOnlyList<Message> Messages, IReadOnlyDictionary<string, JsonElement>? ModelState,
        int SourceDone, int SourceApprove, int ResumedDone, int ResumedApprove);

    private sealed class PauseModel : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => false;
        internal int StreamCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("The seed cannot make a second provider request.");
            return Produce(cancellationToken);
        }
        private static async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            var names = new[] { "done", "approve" };
            for (var index = 0; index < names.Length; index++)
            {
                yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, names[index], Json("{}")));
                yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = ProviderState() };
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new ValueTask<long>(9); }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class CompletionModel(BodyCounts sourceEffects, BodyCounts resumedEffects) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => true;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal List<RequestObservation> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("Only the completed-tool continuation is expected.");
            var messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray();
            var modelState = request.ModelState?.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
            Requests.Add(new(messages, modelState, sourceEffects.Done, sourceEffects.Approve, resumedEffects.Done, resumedEffects.Approve));
            return Complete(cancellationToken);
        }
        private static async IAsyncEnumerable<ModelStreamEvent> Complete([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = ProviderState() };
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new ValueTask<long>(9); }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Saved { get; set; }
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Loads++; return new ValueTask<SessionSnapshot?>(Saved); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saves++; Saved = snapshot; return default; }
    }
}
