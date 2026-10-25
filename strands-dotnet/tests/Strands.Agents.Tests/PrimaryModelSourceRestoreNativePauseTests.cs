using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelSourceRestoreNativePauseTests
{
    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task RestoredNativePendingBatchCanBeAbortedWithoutProviderOrToolReplay(ModelHistoryBehavior profile)
    {
        var producerModel = new StatefulProbe(producePause: true);
        var restoredModel = new StatefulProbe(producePause: false);
        var store = new MemoryStore();
        var committedEffects = 0;
        var approvalEntries = 0;
        var replayedBodies = 0;
        Agent? producer = null;
        Agent? restored = null;
        Exception? primary = null;
        try
        {
            producer = new Agent(new()
            {
                Model = producerModel,
                ModelHistoryBehavior = ModelHistoryBehavior.Native,
                SessionStore = store,
                SessionId = "native-source-pause",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools =
                [
                    Tool("done", _ => { committedEffects++; return "committed"; }),
                    Tool("approve", context => { approvalEntries++; return context.Interrupt("approval").ToString(); })
                ]
            });
            var paused = await producer.InvokeAsync("task");
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            var question = Assert.Single(paused.Interrupts);
            Assert.Equal("call-1", question.ToolUseId);
            Assert.Equal(1, committedEffects);
            Assert.Equal(1, approvalEntries);
            Assert.Equal(1, producerModel.StreamCalls);
            var saved = Assert.IsType<SessionSnapshot>(store.Saved);
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            var savedPending = Assert.IsType<InterruptState>(saved.InterruptState);
            Assert.Equal(question.Id, Assert.Single(savedPending.Interrupts).Id);
            var completed = Assert.Single(savedPending.CompletedToolResults);
            Assert.Equal("call-0", completed.ToolUseId);
            Assert.Equal("committed", completed.Content);
            Assert.Equal(ToolResultStatus.Success, completed.Status);
            Assert.Collection(saved.Messages,
                message => { Assert.Equal(Role.User, message.Role); Assert.Equal("task", message.Text); },
                message => { Assert.Equal(Role.Assistant, message.Role); Assert.Equal(savedPending.AssistantMessage.TrackingId, message.TrackingId); });
            AssertProviderState(saved.ModelState!);
            var loadsBeforeRestore = store.Loads;
            await producer.DisposeAsync();
            producer = null;
            Assert.Equal(0, producerModel.DisposeCalls);

            restored = new Agent(new()
            {
                Model = restoredModel,
                ModelHistoryBehavior = profile,
                SessionStore = store,
                SessionId = "native-source-pause",
                ToolExecution = ToolExecutionStrategy.Sequential,
                Tools =
                [
                    Tool("done", _ => { replayedBodies++; throw new InvalidOperationException("Aborting cannot replay a completed tool."); }),
                    Tool("approve", _ => { replayedBodies++; throw new InvalidOperationException("Aborting cannot execute an unfinished tool."); })
                ]
            });
            await restored.InitializeAsync();
            Assert.Same(restoredModel, restored.Model);
            Assert.Equal(loadsBeforeRestore + 1, store.Loads);
            var pending = Assert.IsType<InterruptState>(restored.InterruptState);
            Assert.Equal(question.Id, Assert.Single(pending.Interrupts).Id);
            Assert.Equal(savedPending.AssistantMessage.TrackingId, pending.AssistantMessage.TrackingId);
            AssertProviderState(restored.ModelState.GetAll());
            AssertNeverEntered(restoredModel);

            Assert.True(await restored.AbortInterruptsAsync("declined"));

            Assert.Null(restored.InterruptState);
            Assert.Equal(1, committedEffects);
            Assert.Equal(1, approvalEntries);
            Assert.Equal(0, replayedBodies);
            Assert.Equal(1, producerModel.StreamCalls);
            AssertNeverEntered(restoredModel);
            AssertProviderState(restored.ModelState.GetAll());
            AssertSettledHistory(restored.Messages, savedPending.AssistantMessage.TrackingId);
            var settled = Assert.IsType<SessionSnapshot>(store.Saved);
            Assert.Null(settled.InterruptState);
            Assert.Null(settled.ModelHistoryCount);
            Assert.Null(settled.ModelInputReceipt);
            AssertProviderState(settled.ModelState!);
            AssertSettledHistory(settled.Messages, savedPending.AssistantMessage.TrackingId);
            Assert.False(await restored.AbortInterruptsAsync("already settled"));
            AssertSettledHistory(restored.Messages, savedPending.AssistantMessage.TrackingId);
            AssertNeverEntered(restoredModel);
            await restored.DisposeAsync();
            restored = null;
            Assert.Equal(0, producerModel.DisposeCalls);
            Assert.Equal(0, restoredModel.DisposeCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { await DisposeAgents(primary, restored, producer); }
            finally
            {
                await producerModel.DisposeAsync();
                await restoredModel.DisposeAsync();
            }
        }
    }

    private static void AssertSettledHistory(IReadOnlyList<Message> messages, string assistantTrackingId)
    {
        Assert.Collection(messages,
            message => { Assert.Equal(Role.User, message.Role); Assert.Equal("task", message.Text); },
            message => { Assert.Equal(Role.Assistant, message.Role); Assert.Equal(assistantTrackingId, message.TrackingId); },
            message =>
            {
                Assert.Equal(Role.User, message.Role);
                Assert.Collection(message.Content.OfType<ToolResultBlock>(),
                    result => { Assert.Equal("call-0", result.ToolUseId); Assert.Equal("committed", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); },
                    result => { Assert.Equal("call-1", result.ToolUseId); Assert.Equal("declined", result.Content); Assert.Equal(ToolResultStatus.Error, result.Status); });
            });
    }

    private static void AssertProviderState(IReadOnlyDictionary<string, JsonElement> state)
    {
        Assert.Equal("saved-provider", state["responseId"].GetString());
        Assert.Equal(23, state["nested"].GetProperty("sequence").GetInt32());
    }

    private static void AssertNeverEntered(StatefulProbe model)
    {
        Assert.Equal(0, model.StreamCalls);
        Assert.Equal(0, model.CountCalls);
        Assert.Equal(0, model.DisposeCalls);
    }

    private static async ValueTask DisposeAgents(Exception? primary, params Agent?[] agents)
    {
        Exception? failure = primary;
        var secondary = 0;
        foreach (var agent in agents)
        {
            if (agent is null) continue;
            try { await agent.DisposeAsync(); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup)) failure.Data["PrimarySourceNativePause.Cleanup." + secondary++] = cleanup;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FunctionTool Tool(string name, Func<ToolContext, string> body)
        => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));

    private sealed class StatefulProbe(bool producePause) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => !producePause;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (!producePause || StreamCalls != 1) throw new InvalidOperationException("No additional provider request is allowed.");
            return Produce(cancellationToken);
        }
        private static async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            var tools = new[] { "done", "approve" };
            for (var index = 0; index < tools.Length; index++)
            {
                yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, tools[index], Json("{}")));
                yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse)
            {
                ModelState = new Dictionary<string, JsonElement>
                {
                    ["responseId"] = Json("\"saved-provider\""),
                    ["nested"] = Json("{\"sequence\":23}")
                }
            };
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            CountCalls++;
            if (!producePause) throw new InvalidOperationException("Restoring or aborting must not count a provider request.");
            return new ValueTask<long>(9);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Saved { get; private set; }
        internal int Loads { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Loads++; return new ValueTask<SessionSnapshot?>(Saved); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saved = snapshot; return default; }
    }
}
