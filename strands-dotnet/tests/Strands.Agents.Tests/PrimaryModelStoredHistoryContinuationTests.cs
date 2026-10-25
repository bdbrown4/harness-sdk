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

public sealed class PrimaryModelStoredHistoryContinuationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CompletedPythonPrefixCanReopenUnderExplicitCurrentCapabilityAndCallerCancellation(bool stateful, bool cancelBeforeProvider)
    {
        var producerModel = new FiniteModel(stateful: false, pause: true);
        var resumedModel = new FiniteModel(stateful: true);
        var freshModel = new FiniteModel(stateful);
        var effects = new Effects();
        var firstStore = new MemoryStore();
        var resumedStore = new MemoryStore();
        var files = new OwnedFiles();
        using var caller = new CancellationTokenSource();
        Agent? producer = null, resumed = null, fresh = null;
        Exception? primary = null;
        try
        {
            producer = new(new()
            {
                Model = producerModel,
                SessionStore = firstStore,
                SessionId = "paired-producer",
                Tools = Tools(effects),
                ToolExecution = ToolExecutionStrategy.Sequential
            });
            Assert.Equal(StopReasons.Interrupt, (await producer.InvokeAsync("original task")).StopReason);
            var paired = Assert.IsType<SessionSnapshot>(firstStore.Saved);
            Assert.Null(paired.ModelHistoryCount);
            Assert.Null(paired.ModelInputReceipt);
            var pending = Assert.IsType<InterruptState>(paired.InterruptState);
            Assert.Equal(pending.AssistantMessage.TrackingId, paired.Messages.Last().TrackingId);
            Assert.Equal(1, effects.Done);
            Assert.Equal(1, effects.Approve);
            await producer.DisposeAsync(); producer = null;
            Assert.Equal(0, producerModel.Disposals);

            // This public DTO mapping isolates independent pending ownership; it does not claim a new source-pause producer.
            resumedStore.Saved = Copy(paired with
            {
                Messages = Array.Empty<Message>(),
                InterruptState = pending with { AssistantMessageIsIndependent = true }
            });
            resumed = new(new()
            {
                Model = resumedModel,
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                SessionStore = resumedStore,
                SessionId = "source-resume",
                Tools = Tools(effects),
                ToolExecution = ToolExecutionStrategy.Sequential
            });
            var question = Assert.Single(pending.Interrupts);
            var result = await resumed.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] });
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal(1, effects.Done);
            Assert.Equal(2, effects.Approve);
            Assert.Equal(1, resumedModel.Calls);
            var completed = Assert.IsType<SessionSnapshot>(resumedStore.Writes.LastOrDefault(snapshot =>
                snapshot.InterruptState is null && snapshot.Messages.Count == 1 && snapshot.Messages[0].Content.OfType<ToolResultBlock>().Any()));
            Assert.Equal(ModelHistoryBehavior.Python, completed.HistoryProfile);
            AssertResults(Assert.Single(completed.Messages));
            Assert.Equal("S1", completed.ModelState!["responseId"].GetString());
            Assert.Null(completed.ModelHistoryCount);
            Assert.Null(completed.ModelInputReceipt);
            await resumed.DisposeAsync(); resumed = null;
            Assert.Equal(0, resumedModel.Disposals);

            await new FileSessionStore(files.Path).SaveAsync("completed-prefix", completed);
            var loaded = Assert.IsType<SessionSnapshot>(await new FileSessionStore(files.Path).LoadAsync("completed-prefix"));
            Assert.Equal(ModelHistoryBehavior.Python, loaded.HistoryProfile);
            AssertResults(Assert.Single(loaded.Messages));
            var hooks = new HookRegistry();
            if (cancelBeforeProvider) hooks.Register<BeforeInvocationEvent>((_, _) => { caller.Cancel(); return default; });
            fresh = new(new()
            {
                Model = freshModel,
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                SessionStore = new MemoryStore { Saved = loaded },
                SessionId = "fresh-source",
                Hooks = hooks
            });
            var next = await fresh.InvokeAsync("next input", cancellationToken: caller.Token);
            if (cancelBeforeProvider)
            {
                Assert.Equal(StopReasons.Cancelled, next.StopReason);
                Assert.True(caller.IsCancellationRequested);
                Assert.Equal(0, freshModel.Calls);
                Assert.Empty(freshModel.Requests);
            }
            else
            {
                Assert.Equal(StopReasons.EndTurn, next.StopReason);
                Assert.Equal(1, freshModel.Calls);
                var request = Assert.Single(freshModel.Requests);
                Assert.Equal("S1", request.ModelState!["responseId"].GetString());
                Assert.Equal(stateful ? 1 : 2, request.Messages.Count);
                if (!stateful) AssertResults(request.Messages[0]);
                Assert.Equal("next input", request.Messages.Last().Text);
            }
            Assert.Equal("S1", fresh.ModelState.Get<string>("responseId"));
            Assert.Equal(1, effects.Done);
            Assert.Equal(2, effects.Approve);
            await fresh.DisposeAsync(); fresh = null;
            Assert.Equal(0, freshModel.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, fresh, resumed, producer, freshModel, resumedModel, producerModel, files); }
    }

    private static void AssertResults(Message message)
    {
        Assert.Equal(Role.User, message.Role);
        Assert.Collection(message.Content.OfType<ToolResultBlock>(),
            done => { Assert.Equal("call-0", done.ToolUseId); Assert.Equal("committed", done.Content); Assert.Equal(ToolResultStatus.Success, done.Status); },
            approval => { Assert.Equal("call-1", approval.ToolUseId); Assert.Equal("approved", approval.Content); Assert.Equal(ToolResultStatus.Success, approval.Status); });
    }
    private static IReadOnlyList<ITool> Tools(Effects effects) =>
    [
        new FunctionTool(new("done", "completed sibling", Json("{\"type\":\"object\"}")), (_, _, _) =>
        { effects.Done++; return new ValueTask<string>("committed"); }),
        new FunctionTool(new("approve", "requires approval", Json("{\"type\":\"object\"}")), (_, context, _) =>
        { effects.Approve++; return new ValueTask<string>(context.Interrupt("approval").GetBoolean() ? "approved" : "declined"); })
    ];
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static SessionSnapshot Copy(SessionSnapshot value) => JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(value, Web), Web)!;
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
                else if (!ReferenceEquals(failure, error)) failure.Data["StoredContinuation.Cleanup." + index++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Effects { internal int Done; internal int Approve; }
    private sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Saved { get; set; }
        internal List<SessionSnapshot> Writes { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(Saved is null ? null : Copy(Saved)); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saved = Copy(snapshot); Writes.Add(Saved); return default; }
    }
    private sealed class FiniteModel(bool stateful, bool pause = false) : IStatefulModel, IAsyncDisposable
    {
        public bool Stateful => stateful;
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        internal List<ModelRequest> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls != 1) throw new InvalidOperationException("This fixture permits one finite provider request per model.");
            Requests.Add(request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            });
            return Produce(cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
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
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn)
            { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"S1\"") } };
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class OwnedFiles : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stored-history-continuation", Guid.NewGuid().ToString("N"));
        public ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned fixture directory escaped task TEMP.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            return default;
        }
    }
}
