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

public sealed class PrimaryModelSourceFirstPauseProducerTests
{
    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task GenuineSourcePauseSurvivesFileReopenWithoutRepeatingCompletedTools(ModelHistoryBehavior profile)
    {
        var directory = new OwnedDirectory();
        var effects = new BodyCounts();
        var producerModel = new FiniteModel(pause: true, effects);
        var resumedModel = new FiniteModel(pause: false, effects);
        var producerAfter = new AfterObserver();
        var resumedAfter = new AfterObserver();
        const string sessionId = "genuine-source-first-pause";
        Agent? producer = null;
        Agent? resumed = null;
        Exception? primary = null;
        try
        {
            producer = new(new()
            {
                Model = producerModel,
                ModelHistoryBehavior = profile,
                ModelState = ProviderState("S0"),
                SessionStore = new FileSessionStore(directory.Path),
                SessionId = sessionId,
                ToolExecution = ToolExecutionStrategy.Sequential,
                Plugins = [producerAfter],
                Tools =
                [
                    Tool("done", _ => { effects.FirstDone++; return "committed-once"; }),
                    Tool("approve", context => { effects.FirstApprove++; return context.Interrupt("approval").ToString(); })
                ]
            });

            var paused = await producer.InvokeAsync("start");

            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            var question = Assert.Single(paused.Interrupts);
            Assert.Equal("call-1", question.ToolUseId);
            Assert.Equal("approval", question.Name);
            var pending = Assert.IsType<InterruptState>(producer.InterruptState);
            Assert.Equal(question.Id, Assert.Single(pending.Interrupts).Id);
            Assert.Empty(pending.Responses);
            Assert.Equal(new[] { "call-0", "call-1" }, pending.AssistantMessage.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
            Assert.Equal(new[] { "done", "approve" }, pending.AssistantMessage.Content.OfType<ToolUseBlock>().Select(call => call.Name));
            var committed = Assert.Single(pending.CompletedToolResults);
            Assert.Equal("call-0", committed.ToolUseId);
            Assert.Equal("committed-once", committed.Content);
            Assert.Equal(ToolResultStatus.Success, committed.Status);
            Assert.Equal(1, effects.FirstDone);
            Assert.Equal(1, effects.FirstApprove);
            Assert.Equal(0, effects.ResumedDone);
            Assert.Equal(0, effects.ResumedApprove);
            Assert.Equal(1, producerModel.StreamCalls);
            Assert.Equal(1, producerModel.IteratorDisposals);
            var firstRequest = Assert.Single(producerModel.Requests);
            AssertState(firstRequest.ModelState!, "S0");
            var input = Assert.Single(firstRequest.Messages);
            Assert.Equal(Role.User, input.Role);
            Assert.Equal("start", input.Text);
            AssertState(producer.ModelState.GetAll(), "S1");

            Assert.Empty(producer.Messages);
            Assert.True(pending.AssistantMessageIsIndependent);
            Assert.Equal(profile == ModelHistoryBehavior.TypeScript ? 0 : 2, Assert.Single(producerAfter.MessageCounts));
            var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory.Path).LoadAsync(sessionId));
            AssertPausedSnapshot(saved, profile, question.Id, pending.AssistantMessage.TrackingId);
            AssertState(saved.ModelState!, "S1");
            await producer.DisposeAsync(); producer = null;
            Assert.Equal(0, producerModel.Disposals);
            Assert.Equal(0, resumedModel.Disposals);

            resumed = new(new()
            {
                Model = resumedModel,
                ModelHistoryBehavior = profile,
                SessionStore = new FileSessionStore(directory.Path),
                SessionId = sessionId,
                ToolExecution = ToolExecutionStrategy.Sequential,
                Plugins = [resumedAfter],
                Tools =
                [
                    Tool("done", _ => { effects.ResumedDone++; throw new InvalidOperationException("A completed sibling cannot execute again."); }),
                    Tool("approve", context =>
                    {
                        effects.ResumedApprove++;
                        Assert.Equal(0, resumedModel.StreamCalls);
                        AssertState(context.Agent.ModelState.GetAll(), "S1");
                        return context.Interrupt("approval").GetBoolean() ? "approved" : "declined";
                    })
                ]
            });
            await resumed.InitializeAsync();

            Assert.Empty(resumed.Messages);
            var reopened = Assert.IsType<InterruptState>(resumed.InterruptState);
            Assert.True(reopened.AssistantMessageIsIndependent);
            Assert.Equal(question.Id, Assert.Single(reopened.Interrupts).Id);
            Assert.Equal(pending.AssistantMessage.TrackingId, reopened.AssistantMessage.TrackingId);
            Assert.Equal("committed-once", Assert.Single(reopened.CompletedToolResults).Content);
            AssertState(resumed.ModelState.GetAll(), "S1");
            Assert.Equal(0, resumedModel.StreamCalls);
            Assert.Equal(0, effects.ResumedDone);
            Assert.Equal(0, effects.ResumedApprove);

            var result = await resumed.InvokeAsync(null, new InvocationOptions
            {
                InterruptResponses = [new(question.Id, Json("true"))]
            });

            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("finished", result.ToString());
            Assert.Equal(1, effects.FirstDone);
            Assert.Equal(1, effects.FirstApprove);
            Assert.Equal(0, effects.ResumedDone);
            Assert.Equal(1, effects.ResumedApprove);
            Assert.Equal(1, producerModel.StreamCalls);
            Assert.Equal(1, producerModel.IteratorDisposals);
            Assert.Equal(1, resumedModel.StreamCalls);
            Assert.Equal(1, resumedModel.IteratorDisposals);
            var continuation = Assert.Single(resumedModel.Requests);
            Assert.Equal(1, continuation.FirstDone);
            Assert.Equal(1, continuation.FirstApprove);
            Assert.Equal(0, continuation.ResumedDone);
            Assert.Equal(1, continuation.ResumedApprove);
            AssertState(continuation.ModelState!, "S1");
            if (profile == ModelHistoryBehavior.TypeScript)
            {
                Assert.Collection(continuation.Messages,
                    message =>
                    {
                        Assert.Equal(Role.Assistant, message.Role);
                        Assert.Equal(pending.AssistantMessage.TrackingId, message.TrackingId);
                        Assert.Equal(new[] { "call-0", "call-1" }, message.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
                        Assert.Equal(new[] { "done", "approve" }, message.Content.OfType<ToolUseBlock>().Select(call => call.Name));
                    },
                    AssertResults);
            }
            else AssertResults(Assert.Single(continuation.Messages));
            Assert.Null(resumed.InterruptState);
            Assert.Empty(resumed.Messages);
            AssertState(resumed.ModelState.GetAll(), "S2");
            Assert.Equal(profile == ModelHistoryBehavior.TypeScript ? 0 : 2, Assert.Single(resumedAfter.MessageCounts));
            var completed = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory.Path).LoadAsync(sessionId));
            Assert.Equal(profile, completed.HistoryProfile);
            Assert.Equal(1, completed.SchemaVersion);
            Assert.Empty(completed.Messages);
            Assert.Null(completed.InterruptState);
            Assert.Null(completed.ModelHistoryCount);
            Assert.Null(completed.ModelInputReceipt);
            AssertState(completed.ModelState!, "S2");
            await resumed.DisposeAsync(); resumed = null;
            Assert.Equal(0, producerModel.Disposals);
            Assert.Equal(0, resumedModel.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, resumed, producer, resumedModel, producerModel, directory); }
    }

    private static void AssertPausedSnapshot(SessionSnapshot saved, ModelHistoryBehavior profile, string questionId, string assistantId)
    {
        Assert.Equal(profile, saved.HistoryProfile);
        Assert.Equal(1, saved.SchemaVersion);
        Assert.Empty(saved.Messages);
        Assert.Null(saved.ModelHistoryCount);
        Assert.Null(saved.ModelInputReceipt);
        var pending = Assert.IsType<InterruptState>(saved.InterruptState);
        Assert.True(pending.AssistantMessageIsIndependent);
        Assert.Equal(assistantId, pending.AssistantMessage.TrackingId);
        Assert.Equal(new[] { "call-0", "call-1" }, pending.AssistantMessage.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
        Assert.Equal(questionId, Assert.Single(pending.Interrupts).Id);
        Assert.Empty(pending.Responses);
        var committed = Assert.Single(pending.CompletedToolResults);
        Assert.Equal("call-0", committed.ToolUseId);
        Assert.Equal("committed-once", committed.Content);
    }

    private static void AssertResults(Message message)
    {
        Assert.Equal(Role.User, message.Role);
        Assert.Equal(2, message.Content.Count);
        Assert.Collection(message.Content.OfType<ToolResultBlock>(),
            result => { Assert.Equal("call-0", result.ToolUseId); Assert.Equal("committed-once", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); },
            result => { Assert.Equal("call-1", result.ToolUseId); Assert.Equal("approved", result.Content); Assert.Equal(ToolResultStatus.Success, result.Status); });
    }

    private static Dictionary<string, JsonElement> ProviderState(string phase)
        => new() { ["responseId"] = Json("\"" + phase + "\""), ["nested"] = Json("{\"sequence\":23}") };
    private static void AssertState(IReadOnlyDictionary<string, JsonElement> state, string phase)
    {
        Assert.Equal(2, state.Count);
        Assert.Equal(phase, state["responseId"].GetString());
        Assert.Equal(23, state["nested"].GetProperty("sequence").GetInt32());
    }
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static FunctionTool Tool(string name, Func<ToolContext, string> body)
        => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));

    private static async ValueTask DisposeOwned(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var index = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else if (!ReferenceEquals(failure, cleanup)) failure.Data["SourceProducer.Cleanup." + index++] = cleanup;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class BodyCounts { internal int FirstDone; internal int FirstApprove; internal int ResumedDone; internal int ResumedApprove; }
    private sealed record RequestObservation(IReadOnlyList<Message> Messages, IReadOnlyDictionary<string, JsonElement>? ModelState,
        int FirstDone, int FirstApprove, int ResumedDone, int ResumedApprove);

    private sealed class AfterObserver : IPlugin
    {
        public string Name => "source-first-pause-observer";
        internal List<int> MessageCounts { get; } = [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            agent.Hooks.Register<AfterInvocationEvent>((value, _) => { MessageCounts.Add(value.Agent.Messages.Count); return default; });
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class FiniteModel(bool pause, BodyCounts effects) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => true;
        internal int StreamCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        internal List<RequestObservation> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("Each finite model permits exactly one provider request.");
            var messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray();
            var state = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            Requests.Add(new(messages, state, effects.FirstDone, effects.FirstApprove, effects.ResumedDone, effects.ResumedApprove));
            return Produce(cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.CompletedTask;
                token.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                if (pause)
                {
                    var names = new[] { "done", "approve" };
                    for (var index = 0; index < names.Length; index++)
                    {
                        yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, names[index], Json("{}")));
                        yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                        yield return new ModelContentBlockStopEvent(index);
                    }
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                    yield return new ModelContentBlockStopEvent(0);
                }
                yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn)
                { ModelState = ProviderState(pause ? "S1" : "S2") };
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new ValueTask<long>(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class OwnedDirectory : IAsyncDisposable
    {
        private readonly string _parent = Strands.Tests.TestPaths.GetPhysicalTempPath();
        public string Path { get; }
        internal OwnedDirectory() => Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_parent, "strands-source-first-pause-" + Guid.NewGuid().ToString("N")));
        public ValueTask DisposeAsync()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(resolved), _parent.TrimEnd(System.IO.Path.DirectorySeparatorChar)))
                throw new InvalidOperationException("The owned fixture directory must be a direct child of its original temporary root.");
            if (!Directory.Exists(resolved)) return default;
            for (var current = new DirectoryInfo(resolved); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses reparse ancestry.");
            var entries = Directory.GetFileSystemEntries(resolved);
            foreach (var entry in entries)
            {
                var absolute = System.IO.Path.GetFullPath(entry);
                if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(absolute), resolved) ||
                    (File.GetAttributes(absolute) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Fixture cleanup requires literal owned files without subdirectories or reparse points.");
            }
            foreach (var entry in entries) File.Delete(entry);
            Directory.Delete(resolved, recursive: false);
            return default;
        }
    }
}
