using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Interventions;
using Strands.Agents.Storage;
using Xunit;

namespace Strands.Agents.Tests.Interventions;

public sealed class GuideFeedbackDeliveryTests
{
    private const string OwnerId = "guide-delivery-owner";
    private const string SessionId = "guide-delivery-session";
    private const string AccumulatedFeedback = "[one] first\n[two] second";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BeforeModelDirectFeedbackBypassesMessageAddedAndImmediateSaveButSurvivesSnapshotRestore()
    {
        await AssertDirectDelivery(afterModel: false);
    }

    [Fact]
    public async Task AfterModelDirectFeedbackRetriesWithoutCommittingRejectedOutputOrItsEffects()
    {
        await AssertDirectDelivery(afterModel: true);
    }

    [Fact]
    public async Task NativeFeedbackRemainsDurableWhenItsMessageAddedCallbackFails()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var state = CallerState();
            var added = new List<MessageAddedEvent>();
            var failure = new InvalidOperationException("native feedback callback failed");
            var hooks = Capture(added);
            hooks.Register<MessageAddedEvent>((value, _) =>
            {
                if (value.Message.Text == "[one] feedback")
                {
                    Assert.Same(state, value.InvocationState);
                    throw failure;
                }
                return default;
            });
            var model = owned.Model(Message.Assistant("must not execute"));
            var handler = owned.Handler(new GuidanceHandler("one", "feedback", afterModel: false, state));
            var directory = Path.Combine(owned.Directory, "native");
            var target = owned.Agent(new(new()
            {
                Id = OwnerId,
                Model = model,
                Hooks = hooks,
                Interventions = [handler],
                SessionStore = new FileSessionStore(directory),
                SessionId = SessionId
            }));

            var actual = await Record.ExceptionAsync(async () => { await target.InvokeAsync("question", new() { InvocationState = state }); });

            Assert.Same(failure, actual);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Equal(0, model.IteratorDisposals);
            Assert.Empty(model.Requests);
            Assert.Equal(1, handler.GuideActions);
            Assert.Equal(new[] { "question", "[one] feedback" }, added.Select(value => value.Message.Text));
            Assert.All(added, value => { Assert.Same(target, value.Agent); Assert.Same(state, value.InvocationState); });
            var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
            Assert.Equal(new[] { "question", "[one] feedback" }, saved.Messages.Select(message => message.Text));
            Assert.Equal(target.Messages.Select(message => message.TrackingId), saved.Messages.Select(message => message.TrackingId));
            Assert.All(saved.Messages, message => Assert.False(string.IsNullOrEmpty(message.TrackingId)));
            Assert.Null(saved.InterruptState);
            Assert.Single(state);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task UndefinedDeliveryIsRejectedDuringConstructionAndNativeDefaultIsOmittedFromJson()
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var model = owned.Model();
            var store = new CountingStore();
            var plugin = new InitializationProbe();
            var options = new AgentOptions { Model = model, SessionStore = store, SessionId = SessionId, Plugins = [plugin] };
            Assert.Equal(GuideFeedbackDelivery.Native, options.GuideFeedbackDelivery);
            Assert.False(Element(options).TryGetProperty("guideFeedbackDelivery", out _));
            Assert.Equal(1, Element(options with { GuideFeedbackDelivery = GuideFeedbackDelivery.DirectAppend })
                .GetProperty("guideFeedbackDelivery").GetInt32());
            Agent? unexpected = null;

            var actual = Record.Exception(() =>
            {
                unexpected = new Agent(options with { GuideFeedbackDelivery = (GuideFeedbackDelivery)int.MaxValue });
            });

            if (unexpected is not null) owned.Agent(unexpected);
            var rejected = Assert.IsType<ArgumentOutOfRangeException>(actual);
            Assert.Equal(nameof(AgentOptions.GuideFeedbackDelivery), rejected.ParamName);
            Assert.Null(unexpected);
            Assert.Equal(0, store.Loads);
            Assert.Equal(0, store.Saves);
            Assert.Equal(0, plugin.Initializations);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Empty(model.Requests);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task BeforeModelDirectFeedbackSurvivesProviderFailureUntilInvocationFinalization()
    {
        await AssertDirectDeliveryProviderFailure(afterModel: false);
    }

    [Fact]
    public async Task AfterModelDirectFeedbackSurvivesRetryFailureWithoutCommittingRejectedEffects()
    {
        await AssertDirectDeliveryProviderFailure(afterModel: true);
    }

    private static async Task AssertDirectDelivery(bool afterModel)
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var state = CallerState();
            var added = new List<MessageAddedEvent>();
            var hooks = Capture(added);
            var storage = owned.Storage();
            var manager = new SnapshotSessionManager(storage, SessionId, SnapshotSessionSaveStrategy.Message);
            var rejected = new Message(Role.Assistant, [new ToolUseBlock("rejected-call", "effect", Json("{}"))]);
            var model = afterModel ? owned.Model(rejected, Message.Assistant("accepted")) : owned.Model(Message.Assistant("accepted"));
            var snapshotsAtProviderEntry = new List<SessionSnapshot?>();
            model.OnRequest = () => snapshotsAtProviderEntry.Add(storage.ReadObservedSnapshot());
            var one = owned.Handler(new GuidanceHandler("one", "first", afterModel, state));
            var two = owned.Handler(new GuidanceHandler("two", "second", afterModel, state));
            var effects = 0;
            var effect = new FunctionTool(new("effect", "must not run a rejected request", Json("{\"type\":\"object\"}")),
                (_, _, _) => { effects++; return new ValueTask<string>("unexpected effect"); });
            var target = owned.Agent(new(new()
            {
                Id = OwnerId,
                Model = model,
                Hooks = hooks,
                GuideFeedbackDelivery = GuideFeedbackDelivery.DirectAppend,
                Interventions = [one, two],
                Plugins = [manager],
                Tools = [effect]
            }));

            var result = await target.InvokeAsync("question", new() { InvocationState = state });

            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            Assert.Equal("accepted", result.ToString());
            Assert.Equal(afterModel ? 2 : 1, model.StreamCalls);
            Assert.Equal(model.StreamCalls, model.IteratorDisposals);
            Assert.Equal(model.StreamCalls, model.Requests.Count);
            Assert.Equal(0, effects);
            Assert.Equal(1, one.GuideActions);
            Assert.Equal(1, two.GuideActions);
            Assert.Equal(1, result.Turns);
            Assert.Equal(afterModel ? 4 : 2, result.Usage.TotalTokens);
            if (afterModel)
                Assert.Equal("question", Assert.Single(model.Requests[0].Messages).Text);
            var guidedRequest = model.Requests[afterModel ? 1 : 0];
            Assert.Equal(new[] { "question", AccumulatedFeedback }, guidedRequest.Messages.Select(message => message.Text));
            var guided = guidedRequest.Messages[1];
            Assert.Equal(Role.User, guided.Role);
            Assert.False(string.IsNullOrEmpty(guided.TrackingId));
            Assert.Equal(new[] { "question", AccumulatedFeedback, "accepted" }, target.Messages.Select(message => message.Text));
            Assert.Equal(guided.TrackingId, target.Messages[1].TrackingId);
            Assert.DoesNotContain(target.Messages, message => message.Content.OfType<ToolUseBlock>().Any());
            Assert.Equal(new[] { "question", "accepted" }, added.Select(value => value.Message.Text));
            Assert.All(added, value => { Assert.Same(target, value.Agent); Assert.Same(state, value.InvocationState); });
            Assert.Single(state);
            Assert.Equal(model.StreamCalls, snapshotsAtProviderEntry.Count);
            foreach (var checkpoint in snapshotsAtProviderEntry)
            {
                var persistedBeforeResponse = Assert.IsType<SessionSnapshot>(checkpoint);
                Assert.Equal("question", Assert.Single(persistedBeforeResponse.Messages).Text);
                Assert.DoesNotContain(persistedBeforeResponse.Messages, message => message.Text == AccumulatedFeedback);
            }

            var writesAfterInvocation = storage.Writes;
            Assert.True(writesAfterInvocation > 0);
            var reopenedModel = owned.Model();
            var reopenedManager = new SnapshotSessionManager(storage, SessionId, SnapshotSessionSaveStrategy.Message);
            var reopened = owned.Agent(new(new() { Id = OwnerId, Model = reopenedModel, Plugins = [reopenedManager] }));

            Assert.True(await reopenedManager.RestoreSnapshotAsync(reopened));

            Assert.Equal(new[] { "question", AccumulatedFeedback, "accepted" }, reopened.Messages.Select(message => message.Text));
            Assert.Equal(target.Messages.Select(message => message.TrackingId), reopened.Messages.Select(message => message.TrackingId));
            Assert.Equal(0, reopenedModel.StreamCalls);
            Assert.Equal(0, reopenedModel.CountCalls);
            Assert.Empty(reopenedModel.Requests);
            Assert.Equal(writesAfterInvocation, storage.Writes);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    private static async Task AssertDirectDeliveryProviderFailure(bool afterModel)
    {
        var owned = new OwnedResources();
        Exception? primary = null;
        try
        {
            var state = CallerState();
            var added = new List<MessageAddedEvent>();
            var hooks = Capture(added);
            var directory = Path.Combine(owned.Directory, "provider-failure");
            var store = new RecordingFailureStore(directory);
            var failure = new IOException("guided provider failed");
            var rejected = new Message(Role.Assistant, [new ToolUseBlock("rejected-call", "effect", Json("{}"))]);
            var model = afterModel ? owned.Model(rejected, Message.Assistant("unreachable")) : owned.Model(Message.Assistant("unreachable"));
            await using var provider = new ProposedStateModel(model);
            var snapshotsAtProviderEntry = new List<SessionSnapshot?>();
            var writesAtProviderEntry = new List<int>();
            var expectedCalls = afterModel ? 2 : 1;
            model.OnRequest = () =>
            {
                snapshotsAtProviderEntry.Add(store.ReadObservedSnapshot());
                writesAtProviderEntry.Add(store.Saves);
                if (model.StreamCalls == expectedCalls) throw failure;
            };
            var afterInvocations = 0;
            hooks.Register<AfterInvocationEvent>((value, _) =>
            {
                afterInvocations++;
                Assert.Same(failure, value.Error);
                Assert.Same(state, value.InvocationState);
                Assert.Equal(1, store.Saves);
                Assert.Equal("question", Assert.Single(Assert.IsType<SessionSnapshot>(store.ReadObservedSnapshot()).Messages).Text);
                return default;
            });
            var one = owned.Handler(new GuidanceHandler("one", "first", afterModel, state));
            var two = owned.Handler(new GuidanceHandler("two", "second", afterModel, state));
            var effects = 0;
            var effect = new FunctionTool(new("effect", "must not run a rejected request", Json("{\"type\":\"object\"}")),
                (_, _, _) => { effects++; return new ValueTask<string>("unexpected effect"); });
            var target = owned.Agent(new(new()
            {
                Id = OwnerId,
                Model = provider,
                Hooks = hooks,
                GuideFeedbackDelivery = GuideFeedbackDelivery.DirectAppend,
                Interventions = [one, two],
                SessionStore = store,
                SessionId = SessionId,
                Tools = [effect],
                ModelState = new Dictionary<string, JsonElement> { ["cursor"] = Json("\"prior\"") }
            }));

            var actual = await Record.ExceptionAsync(async () => { await target.InvokeAsync("question", new() { InvocationState = state }); });

            Assert.Same(failure, actual);
            Assert.Equal(expectedCalls, model.StreamCalls);
            Assert.Equal(expectedCalls, model.CountCalls);
            Assert.Equal(expectedCalls, model.Requests.Count);
            Assert.Equal(expectedCalls, provider.IteratorDisposals);
            Assert.Equal(afterModel ? 1 : 0, model.IteratorDisposals);
            Assert.Equal(1, one.GuideActions);
            Assert.Equal(1, two.GuideActions);
            Assert.Equal(0, effects);
            Assert.Equal(1, afterInvocations);
            if (afterModel) Assert.Equal("question", Assert.Single(model.Requests[0].Messages).Text);
            Assert.Equal(new[] { "question", AccumulatedFeedback }, model.Requests[afterModel ? 1 : 0].Messages.Select(message => message.Text));
            Assert.All(model.Requests, request => Assert.Equal("prior", request.ModelState!["cursor"].GetString()));
            Assert.Equal(new[] { "question", AccumulatedFeedback }, target.Messages.Select(message => message.Text));
            Assert.DoesNotContain(target.Messages, message => message.Content.Any(block => block is ToolUseBlock or ToolResultBlock));
            Assert.Equal("prior", target.ModelState.Get<string>("cursor"));
            Assert.Single(target.ModelState.Keys());
            Assert.Equal("question", Assert.Single(added).Message.Text);
            Assert.All(added, value => { Assert.Same(target, value.Agent); Assert.Same(state, value.InvocationState); });
            Assert.Single(state);
            Assert.Equal(expectedCalls, snapshotsAtProviderEntry.Count);
            Assert.All(writesAtProviderEntry, writes => Assert.Equal(1, writes));
            Assert.All(snapshotsAtProviderEntry, snapshot =>
            {
                var savedBeforeFailure = Assert.IsType<SessionSnapshot>(snapshot);
                Assert.Equal("question", Assert.Single(savedBeforeFailure.Messages).Text);
                Assert.Equal("prior", savedBeforeFailure.ModelState!["cursor"].GetString());
            });
            Assert.Equal(2, store.Saves);
            var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory).LoadAsync(SessionId));
            Assert.Equal(new[] { "question", AccumulatedFeedback }, saved.Messages.Select(message => message.Text));
            Assert.Equal(target.Messages.Select(message => message.TrackingId), saved.Messages.Select(message => message.TrackingId));
            Assert.All(saved.Messages, message => Assert.False(string.IsNullOrEmpty(message.TrackingId)));
            Assert.Equal("prior", saved.ModelState!["cursor"].GetString());
            Assert.Single(saved.ModelState);
            Assert.Null(saved.InterruptState);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
            Assert.Equal(0, provider.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    private static HookRegistry Capture(List<MessageAddedEvent> events)
    {
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) => { events.Add(value); return default; });
        return hooks;
    }
    private static Dictionary<string, object?> CallerState() => new() { ["request"] = new object() };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Web);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class GuidanceHandler(string name, string feedback, bool afterModel, IReadOnlyDictionary<string, object?> state)
        : InterventionHandler, IAsyncDisposable
    {
        public override string Name => name;
        internal int GuideActions { get; private set; }
        internal int Disposals { get; private set; }
        public override ValueTask<IInterventionAction> BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Same(state, value.InvocationState);
            if (afterModel) return new(new Proceed());
            GuideActions++; return new(new Guide(feedback));
        }
        public override ValueTask<IInterventionAction> AfterModelCallAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Same(state, value.InvocationState);
            if (!afterModel || value.Message?.Content.OfType<ToolUseBlock>().Any() != true) return new(new Proceed());
            GuideActions++; return new(new Guide(feedback));
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class InitializationProbe : IPlugin
    {
        public string Name => "guide-constructor-probe";
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class CountingStore : ISessionStore
    {
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Loads++; return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Saves++; return default; }
    }

    private sealed class RecordingStorage : IStorage, IStorageCharacteristics, IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public bool IsEphemeral => true;
        public bool IsNamespaced => true;
        internal int Writes { get; private set; }
        internal int Disposals { get; private set; }
        internal SessionSnapshot? ReadObservedSnapshot()
        {
            if (_values.Count == 0) return null;
            using var document = JsonDocument.Parse(Assert.Single(_values).Value);
            return JsonSerializer.Deserialize<SessionSnapshot>(document.RootElement.GetProperty("snapshot").GetRawText(), Web);
        }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Writes++; _values[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return new(_values.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null); }
        public ValueTask DeleteAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); _values.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return new(_values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray()); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class FiniteModel(params Message[] turns) : IModel, IModelTokenCounter, IAsyncDisposable
    {
        internal Action? OnRequest { get; set; }
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        internal List<ModelRequest> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var turn = StreamCalls++;
            if (turn >= turns.Length) throw new InvalidOperationException("The finite model has no further scripted provider response.");
            Requests.Add(request with { Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray() });
            OnRequest?.Invoke();
            return Produce(turns[turn], cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce(Message message, [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.CompletedTask;
                token.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                for (var index = 0; index < message.Content.Count; index++)
                {
                    if (message.Content[index] is ToolUseBlock call)
                    {
                        yield return new ModelContentBlockStartEvent(index, call with { Input = Json("{}") });
                        yield return new ModelContentBlockDeltaEvent(index, ToolInput: call.Input.GetRawText());
                    }
                    else
                    {
                        yield return new ModelContentBlockStartEvent(index);
                        yield return new ModelContentBlockDeltaEvent(index, Text: Assert.IsType<TextBlock>(message.Content[index]).Text);
                    }
                    yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMetadataEvent(new(1, 1));
                yield return new ModelMessageStopEvent(message.Content.OfType<ToolUseBlock>().Any() ? StopReasons.ToolUse : StopReasons.EndTurn);
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new ValueTask<long>(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class RecordingFailureStore(string directory) : ISessionStore
    {
        private readonly FileSessionStore _inner = new(directory);
        private byte[]? _saved;
        internal int Saves { get; private set; }
        internal SessionSnapshot? ReadObservedSnapshot()
            => _saved is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(_saved, Web);
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default)
            => _inner.LoadAsync(id, token);
        public async ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        {
            await _inner.SaveAsync(id, snapshot, token);
            _saved = JsonSerializer.SerializeToUtf8Bytes(snapshot, Web);
            Saves++;
        }
    }

    private sealed class ProposedStateModel(FiniteModel inner) : IModel, IModelTokenCounter, IAsyncDisposable
    {
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var item in inner.StreamAsync(request, cancellationToken))
                    yield return item is ModelMessageStopEvent stop
                        ? stop with { ModelState = new Dictionary<string, JsonElement> { ["cursor"] = Json("\"rejected\"") } }
                        : item;
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => inner.CountTokensAsync(request, cancellationToken);
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class OwnedResources
    {
        private readonly string _temporaryRoot = Strands.Tests.TestPaths.GetPhysicalTempPath();
        private readonly List<Agent> _agents = [];
        private readonly List<IAsyncDisposable> _resources = [];
        private readonly List<FiniteModel> _models = [];
        private readonly List<GuidanceHandler> _handlers = [];
        private readonly List<RecordingStorage> _stores = [];
        internal string Directory { get; }
        internal OwnedResources()
            => Directory = Path.GetFullPath(Path.Combine(_temporaryRoot, "strands-guide-delivery-" + Guid.NewGuid().ToString("N")));
        internal Agent Agent(Agent agent) { _agents.Add(agent); return agent; }
        internal FiniteModel Model(params Message[] turns)
        { var model = new FiniteModel(turns); _models.Add(model); _resources.Add(model); return model; }
        internal GuidanceHandler Handler(GuidanceHandler handler) { _handlers.Add(handler); _resources.Add(handler); return handler; }
        internal RecordingStorage Storage()
        { var store = new RecordingStorage(); _stores.Add(store); _resources.Add(store); return store; }
        internal async ValueTask DisposeAgentsAndAssertBorrowedAsync()
        {
            for (var index = _agents.Count - 1; index >= 0; index--) await _agents[index].DisposeAsync();
            _agents.Clear();
            foreach (var model in _models) Assert.Equal(0, model.Disposals);
            foreach (var handler in _handlers) Assert.Equal(0, handler.Disposals);
            foreach (var store in _stores) Assert.Equal(0, store.Disposals);
        }
        internal async ValueTask DisposeAsync(Exception? primary)
        {
            Exception? failure = primary;
            for (var index = _agents.Count - 1; index >= 0; index--)
                try { await _agents[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "agent." + index); }
            _agents.Clear();
            for (var index = _resources.Count - 1; index >= 0; index--)
                try { await _resources[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "resource." + index); }
            try { DeleteOwnedDirectory(); }
            catch (Exception error) { failure = Attach(failure, error, "directory"); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private void DeleteOwnedDirectory()
        {
            var resolved = Path.GetFullPath(Directory);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(resolved), _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ||
                !Path.GetFileName(resolved).StartsWith("strands-guide-delivery-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup requires its original unique direct temporary child.");
            if (!System.IO.Directory.Exists(resolved)) return;
            for (var current = new DirectoryInfo(resolved); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses reparse ancestry.");
            var pending = new Stack<string>();
            pending.Push(resolved);
            while (pending.Count > 0)
                foreach (var entry in System.IO.Directory.GetFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses reparse entries.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            System.IO.Directory.Delete(resolved, recursive: true);
        }
        private static Exception Attach(Exception? primary, Exception cleanup, string key)
        {
            if (primary is null) return cleanup;
            if (!ReferenceEquals(primary, cleanup)) primary.Data["GuideDelivery.Cleanup." + key] = cleanup;
            return primary;
        }
    }
}
