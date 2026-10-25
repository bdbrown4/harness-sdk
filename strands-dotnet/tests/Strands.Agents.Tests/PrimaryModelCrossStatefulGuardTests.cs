using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.SessionRepositories;
using ClassicManager = Strands.Agents.Conversation.ConversationManager;

#if NETFRAMEWORK
using Strands.Agents.Tests.PrimaryModelPortability;
#endif

namespace Strands.Agents.Tests;

public sealed class PrimaryModelCrossStatefulGuardTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, "zero-cursor")]
    [InlineData(ModelHistoryBehavior.TypeScript, "acknowledged-cursor")]
    [InlineData(ModelHistoryBehavior.TypeScript, "input-receipt")]
    [InlineData(ModelHistoryBehavior.Python, "zero-cursor")]
    [InlineData(ModelHistoryBehavior.Python, "acknowledged-cursor")]
    [InlineData(ModelHistoryBehavior.Python, "input-receipt")]
    public async Task RetainedSnapshotRejectsValidNativeMetadataBeforeInitializationOrMutation(ModelHistoryBehavior behavior, string kind)
    {
        var snapshot = NativeCheckpoint(kind);
        var before = JsonSerializer.Serialize(snapshot, Web);
        await using (var native = new Agent(new() { Model = new RecordingModel("native", true) }))
            await native.LoadSnapshotAsync(snapshot);
        var model = new RecordingModel("source", true);
        var plugin = new InitializationProbe();
        var store = new LoadedStore(null);
        await using var agent = ConfiguredAgent(model, behavior, plugin, store);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(snapshot));

        AssertConfigured(agent);
        Assert.Equal(0, plugin.Initializations);
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, store.Saves);
        Assert.Empty(model.CountRequests);
        Assert.Empty(model.StreamRequests);
        Assert.Equal(before, JsonSerializer.Serialize(snapshot, Web));
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task RetainedSessionRestoreRejectsValidReceiptBeforePluginOrStatePublication(ModelHistoryBehavior behavior)
    {
        var snapshot = NativeSessionReceipt();
        var before = JsonSerializer.Serialize(snapshot, Web);
        var store = new LoadedStore(snapshot);
        var model = new RecordingModel("source", true);
        var plugin = new InitializationProbe();
        await using var agent = ConfiguredAgent(model, behavior, plugin, store);

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());

        AssertConfigured(agent);
        Assert.Equal(1, store.Loads);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, plugin.Initializations);
        Assert.Empty(model.CountRequests);
        Assert.Empty(model.StreamRequests);
        Assert.Equal(before, JsonSerializer.Serialize(snapshot, Web));
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task RetainedIndexedManagerRejectsBeforeBindingFilteringOrOwnedPublication(ModelHistoryBehavior behavior)
    {
        var repository = new NeverCalledRepository();
        var manager = new RepositorySessionManager("guard-session", repository);
        var model = new RecordingModel("borrowed", false);
        var filterCalls = 0;
        var tool = new FunctionTool(new("guard_tool", "Fixture only.", Json("{\"type\":\"object\"}")),
            (_, _, _) => new ValueTask<string>("unused"));
        var options = new AgentOptions
        {
            Id = "guard-owner",
            Model = model,
            Plugins = [manager],
            Tools = [tool],
            ModelHistoryBehavior = behavior,
            ToolFilter = _ => { filterCalls++; return true; }
        };
        Agent? attempted = null;
        Exception? primary = null;
        try
        {
            var error = Record.Exception(() => attempted = new Agent(options));
            Assert.IsType<ArgumentException>(error);
            Assert.Equal(0, filterCalls);
            Assert.Equal(0, model.CapabilityReads);
            Assert.Equal(0, repository.Calls);
            Assert.Equal(0, model.DisposeCalls);
            await using var native = new Agent(options with { ModelHistoryBehavior = ModelHistoryBehavior.Native });
            Assert.Equal("guard-owner", native.Id);
            Assert.Equal(1, filterCalls);
            Assert.Equal(0, repository.Calls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { if (attempted is not null) await DisposeJoined(attempted, primary); }
        Assert.Equal(0, model.DisposeCalls);
    }

    [Fact]
    public async Task TypeScriptClearingReadsTheConstructorReferenceCurrentFlagAfterReplacement()
    {
        var original = new RecordingModel("original", false);
        var replacement = new RecordingModel("replacement", false);
        var observedHistory = new List<int>();
        var hooks = new HookRegistry();
        hooks.Register<AfterInvocationEvent>((value, _) => { observedHistory.Add(value.Agent.Messages.Count); return default; });
        var agent = new Agent(new()
        {
            Model = original,
            OwnsModel = true,
            Hooks = hooks,
            ModelHistoryBehavior = ModelHistoryBehavior.TypeScript
        });
        Exception? primary = null;
        try
        {
            agent.SetModel(replacement);
            original.StatefulValue = true;
            Assert.Equal("replacement", (await agent.InvokeAsync("first")).Message!.Text);
            Assert.Empty(agent.Messages);
            Assert.Equal(new[] { 0 }, observedHistory);
            Assert.False(replacement.StatefulValue);
            original.StatefulValue = false;
            Assert.Equal("replacement", (await agent.InvokeAsync("second")).Message!.Text);
            Assert.Equal(new[] { 0, 2 }, observedHistory);
            Assert.Equal(new[] { "second", "replacement" }, agent.Messages.Select(message => message.Text));
            Assert.Equal(new[] { false, true, false }, original.FlagReads);
            Assert.Empty(original.CountRequests);
            Assert.Empty(original.StreamRequests);
            Assert.Equal(2, replacement.CountRequests.Count);
            Assert.Equal(2, replacement.StreamRequests.Count);
            Assert.Equal(2, replacement.IteratorDisposals);
            Assert.Same(replacement, agent.Model);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeJoined(agent, primary); }
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    [Fact]
    public async Task PythonClearingReadsTheCurrentModelAfterManagementAndBeforeEarlierUserHooks()
    {
        var original = new RecordingModel("original", false);
        var replacement = new RecordingModel("replacement", false);
        var late = new RecordingModel("late", false);
        var order = new List<string>();
        var manager = new OrderManager(order);
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) =>
        {
            if (ReferenceEquals(value.Agent.Model, replacement)) replacement.StatefulValue = true;
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            order.Add("user:" + value.Agent.Messages.Count);
            value.Agent.SetModel(late);
            return default;
        });
        var agent = new Agent(new()
        {
            Model = original,
            OwnsModel = true,
            Hooks = hooks,
            ConversationManager = manager,
            ModelHistoryBehavior = ModelHistoryBehavior.Python
        });
        Exception? primary = null;
        try
        {
            agent.SetModel(replacement);
            Assert.Equal("replacement", (await agent.InvokeAsync("first")).Message!.Text);
            Assert.Equal(new[] { "manager:2", "user:0" }, order);
            Assert.Empty(agent.Messages);
            Assert.True(replacement.StatefulValue);
            Assert.False(original.StatefulValue);
            Assert.Same(late, agent.Model);
            Assert.Equal("late", (await agent.InvokeAsync("second")).Message!.Text);
            Assert.Equal(new[] { "manager:2", "user:0", "manager:2", "user:2" }, order);
            Assert.Equal(new[] { "second", "late" }, agent.Messages.Select(message => message.Text));
            Assert.Equal(1, manager.Initializations);
            Assert.Equal(2, manager.BeforeCalls);
            Assert.Equal(2, manager.ManagementCalls);
            Assert.All(manager.Agents, value => Assert.Same(agent, value));
            Assert.Empty(original.StreamRequests);
            Assert.Single(replacement.CountRequests);
            Assert.Single(replacement.StreamRequests);
            Assert.Single(late.CountRequests);
            Assert.Single(late.StreamRequests);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeJoined(agent, primary); }
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
        Assert.Equal(0, late.DisposeCalls);
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public Task FailedProviderKeepsItsCapturedScopeAndStateUntilExplicitProfileCleanup(ModelHistoryBehavior behavior)
        => RunInterruptedProvider(behavior, cancel: false);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public Task CallerCancellationKeepsItsCapturedScopeAndNativeCancelledResult(ModelHistoryBehavior behavior)
        => RunInterruptedProvider(behavior, cancel: true);

    private static async Task RunInterruptedProvider(ModelHistoryBehavior behavior, bool cancel)
    {
        using var caller = new CancellationTokenSource();
        var failure = new IOException("controlled provider failure");
        var scope = new InterruptedScope(failure);
        var owner = new ScopedOwner(scope);
        var replacement = new RecordingModel("not-entered", true);
        var invocation = new Dictionary<string, object?> { ["caller-marker"] = new object() };
        var hooks = new HookRegistry();
        Exception? modelError = null;
        AfterInvocationEvent? after = null;
        hooks.Register<AfterModelCallEvent>((value, _) => { modelError = value.Error; return default; });
        hooks.Register<AfterInvocationEvent>((value, _) => { after = value; return default; });
        Dictionary<string, JsonElement> callerState;
        Agent agent;
        using (var document = JsonDocument.Parse("{\"responseId\":\"prior\",\"nested\":{\"value\":17}}"))
        {
            callerState = new() { ["responseId"] = document.RootElement.GetProperty("responseId"), ["nested"] = document.RootElement.GetProperty("nested") };
            agent = new(new()
            {
                Model = owner,
                OwnsModel = true,
                ModelState = callerState,
                Hooks = hooks,
                ModelHistoryBehavior = behavior
            });
        }
        callerState["responseId"] = Json("\"caller-late\"");
        callerState["nested"] = Json("{\"value\":99}");
        scope.BeforeBody = () => agent.ModelState.Set("responseId", "transient");
        var operation = agent.InvokeAsync("pending", new() { InvocationState = invocation }, caller.Token);
        Exception? primary = null;
        Exception? observedFailure = null;
        try
        {
            await Within(scope.Entered.Task);
            Assert.False(operation.IsCompleted);
            Assert.Equal(1, owner.Creations);
            Assert.Same(invocation, owner.InvocationState);
            var counted = Assert.Single(scope.CountRequests);
            var entered = Assert.Single(scope.StreamRequests);
            Assert.NotSame(callerState, counted.ModelState);
            Assert.NotSame(callerState, entered.ModelState);
            Assert.Equal("prior", counted.ModelState!["responseId"].GetString());
            Assert.Equal(17, counted.ModelState["nested"].GetProperty("value").GetInt32());
            Assert.Equal("prior", entered.ModelState!["responseId"].GetString());
            Assert.Equal(17, entered.ModelState["nested"].GetProperty("value").GetInt32());
            Assert.Equal("transient", agent.ModelState.Get<string>("responseId"));
            Assert.Equal("pending", Assert.Single(entered.Messages).Text);
            agent.SetModel(replacement);
            replacement.StatefulValue = false;
            Assert.Same(replacement, agent.Model);
            if (cancel)
            {
                caller.Cancel();
                var result = await Within(operation);
                Assert.Equal(StopReasons.Cancelled, result.StopReason);
                Assert.NotNull(after);
                Assert.Null(after.Error);
                Assert.Equal(StopReasons.Cancelled, after.Result!.StopReason);
                var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(scope.CancellationError);
                Assert.True(cancelled.CancellationToken.IsCancellationRequested);
                Assert.Same(cancelled, modelError);
            }
            else
            {
                scope.Release.TrySetResult(true);
                var error = await Assert.ThrowsAsync<IOException>(async () => { await Within(operation); });
                Assert.Same(failure, error);
                observedFailure = error;
                Assert.Same(failure, modelError);
                Assert.NotNull(after);
                Assert.Same(failure, after.Error);
                Assert.Null(after.Result);
            }
            Assert.Equal("prior", agent.ModelState.Get<string>("responseId"));
            Assert.Equal(17, agent.ModelState.GetAll()["nested"].GetProperty("value").GetInt32());
            Assert.Equal("prior", entered.ModelState["responseId"].GetString());
            Assert.Equal(1, scope.IteratorDisposals);
            Assert.Empty(replacement.CountRequests);
            Assert.Empty(replacement.StreamRequests);
            Assert.Equal(0, owner.DirectStreamCalls);
            if (behavior == ModelHistoryBehavior.TypeScript) Assert.Empty(agent.Messages);
            else Assert.Equal("pending", Assert.Single(agent.Messages).Text);
            var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
            Assert.False(snapshot.Data.ContainsKey("modelHistoryCount"));
            Assert.False(snapshot.Data.ContainsKey("modelInputReceipt"));
            Assert.Equal("1.0", snapshot.SchemaVersion);
            Assert.Equal("caller-late", callerState["responseId"].GetString());
            Assert.Equal(99, callerState["nested"].GetProperty("value").GetInt32());
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            scope.Release.TrySetResult(true);
            caller.Cancel();
            try { await JoinOwned(operation, primary, observedFailure); }
            finally { await DisposeJoined(agent, primary); }
        }
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(0, scope.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
    }

    private static Agent ConfiguredAgent(RecordingModel model, ModelHistoryBehavior behavior, InitializationProbe plugin, LoadedStore store)
        => new(new()
        {
            Model = model,
            ModelHistoryBehavior = behavior,
            Plugins = [plugin],
            SessionStore = store,
            SessionId = "guard-session",
            Messages = [Message.User("configured")],
            SystemPrompt = "configured prompt",
            State = new Dictionary<string, JsonElement> { ["label"] = Json("\"configured\"") },
            ModelState = State("configured provider")
        });
    private static void AssertConfigured(Agent agent)
    {
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Equal("configured prompt", agent.SystemPrompt);
        Assert.Equal("configured", agent.State.Get<string>("label"));
        Assert.Equal("configured provider", agent.ModelState.Get<string>("responseId"));
    }
    private static AgentSnapshot NativeCheckpoint(string kind)
    {
        var receipt = kind == "input-receipt";
        var messages = kind == "acknowledged-cursor"
            ? new[] { new Message(Role.User, [new TextBlock("native input")], "native-u"), new Message(Role.Assistant, [new TextBlock("native output")], "native-a") }
            : new[] { new Message(Role.User, [new TextBlock("native input")], "native-u") };
        var data = new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(messages, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(State("native provider"), Web),
            ["modelHistoryCount"] = JsonSerializer.SerializeToElement(kind == "acknowledged-cursor" ? 2 : 0)
        };
        if (receipt) data["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, "native-u", "discarded"), Web);
        return new(data, schemaVersion: receipt ? "2.0" : "1.0");
    }
    private static SessionSnapshot NativeSessionReceipt()
        => new(2, [new Message(Role.User, [new TextBlock("native input")], "native-u")])
        { ModelHistoryCount = 0, ModelInputReceipt = new(1, "native-u", "discarded"), ModelState = State("native provider") };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static Dictionary<string, JsonElement> State(string value) => new() { ["responseId"] = JsonSerializer.SerializeToElement(value) };
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) => await task.WaitAsync(Watchdog);
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(Watchdog);
    private static async Task JoinOwned(Task task, Exception? primary, Exception? observedFailure)
    {
        try { await task; }
        catch (Exception error) when (ReferenceEquals(error, observedFailure)) { }
        catch (Exception cleanup) when (primary is not null)
        { if (!ReferenceEquals(primary, cleanup)) primary.Data["PrimaryCrossStatefulGuard.JoinFailure"] = cleanup; }
    }
    private static async Task DisposeJoined(Agent agent, Exception? primary)
    {
        try { await agent.DisposeAsync(); }
        catch (Exception cleanup) when (primary is not null)
        { if (!ReferenceEquals(primary, cleanup)) primary.Data["PrimaryCrossStatefulGuard.DisposeFailure"] = cleanup; }
    }

    private sealed class RecordingModel(string text, bool stateful) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        // Mutable values exercise the explicit retained-history profiles, not Native's fixed capability contract.
        internal bool StatefulValue { get; set; } = stateful;
        internal int CapabilityReads { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal List<bool> FlagReads { get; } = [];
        internal List<ModelRequest> CountRequests { get; } = [];
        internal List<ModelRequest> StreamRequests { get; } = [];
        public bool Stateful { get { CapabilityReads++; FlagReads.Add(StatefulValue); return StatefulValue; } }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountRequests.Add(request); return new ValueTask<long>(17); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: text);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State(text) };
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
    private sealed class ScopedOwner(IModel scope) : IStatefulModel, IInvocationModel, IAsyncDisposable
    {
        public bool Stateful => true;
        internal int Creations { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int DirectStreamCalls { get; private set; }
        internal IReadOnlyDictionary<string, object?>? InvocationState { get; private set; }
        public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState)
        { Creations++; InvocationState = invocationState; return scope; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { DirectStreamCalls++; throw new InvalidOperationException("Only the captured invocation scope may enter the provider."); }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
    private sealed class InterruptedScope(IOException failure) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => true;
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal Action? BeforeBody { get; set; }
        internal OperationCanceledException? CancellationError { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal List<ModelRequest> CountRequests { get; } = [];
        internal List<ModelRequest> StreamRequests { get; } = [];
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountRequests.Add(request); return new ValueTask<long>(17); }
        private async Task FinishBody(CancellationToken cancellationToken)
        {
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException error) { CancellationError = error; throw; }
            throw failure;
        }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeforeBody?.Invoke();
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "unaccepted");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State("unaccepted") };
                Entered.TrySetResult(true);
                await FinishBody(cancellationToken);
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
    private sealed class OrderManager(List<string> order) : ClassicManager
    {
        internal int Initializations { get; private set; }
        internal int BeforeCalls { get; private set; }
        internal int ManagementCalls { get; private set; }
        internal List<Agent> Agents { get; } = [];
        public override async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { Initializations++; await base.InitializeAsync(agent, cancellationToken); }
        protected override ValueTask BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); BeforeCalls++; Agents.Add(value.Agent); return default; }
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ManagementCalls++; Agents.Add(agent); order.Add("manager:" + agent.Messages.Count); return default; }
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This fixture never requests context reduction.");
    }
    private sealed class InitializationProbe : IPlugin
    {
        public string Name => "primary-cross-guard:init";
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class LoadedStore(SessionSnapshot? snapshot) : ISessionStore
    {
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Loads++; return new ValueTask<SessionSnapshot?>(snapshot); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saves++; return default; }
    }
    private sealed class NeverCalledRepository : ISessionRepository
    {
        internal int Calls { get; private set; }
        private InvalidOperationException Unexpected() { Calls++; return new("A construction guard must not invoke repository I/O."); }
        public ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<SessionRecord?> ReadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask CreateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<SessionAgentRecord?> ReadAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask UpdateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask CreateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<SessionMessageRecord?> ReadMessageAsync(string sessionId, string agentId, long messageId, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask UpdateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default) => throw Unexpected();
        public ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string sessionId, string agentId, int? limit = null, int offset = 0, CancellationToken cancellationToken = default) => throw Unexpected();
    }
}
