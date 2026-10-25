using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Interventions;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelSourceRestoreFailureTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, true)]
    [InlineData(ModelHistoryBehavior.Python, false)]
    public async Task FailureBeforeRestoredHistoryDiscardRemainsMemoized(ModelHistoryBehavior profile, bool failInPlugin)
    {
        var failure = new IOException("initialization stopped before restored-history discard");
        var original = new ProbeModel(false);
        var selected = new ProbeModel(true);
        var later = new ProbeModel(true);
        var store = new MemoryStore();
        var plugin = new ReplacingPlugin(selected, failInPlugin ? failure : null);
        var observer = new FailingObserver(failure);
        var hooks = new HookRegistry();
        var initialized = 0;
        using var initializedRegistration = hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var agent = new Agent(new()
        {
            Model = original,
            ModelHistoryBehavior = profile,
            SessionStore = store,
            SessionId = "failed-source-initialization",
            Plugins = [plugin],
            Interventions = [observer],
            Hooks = hooks,
            Messages = [Message.User("configured history")],
            ModelState = new Dictionary<string, JsonElement> { ["configuredOnly"] = JsonSerializer.SerializeToElement(true) }
        });
        var constructionReads = original.StatefulReads;
        Exception? primary = null;
        try
        {
            var first = agent.InitializeAsync();
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first));
            Assert.True(first.IsFaulted);
            Assert.Same(selected, agent.Model);
            AssertStateAndNoOperations(agent, store, original, selected, later, constructionReads);
            Assert.Equal(1, plugin.Initializations);
            Assert.Equal(failInPlugin ? 0 : 1, plugin.ToolRequests);
            Assert.Equal(failInPlugin ? 0 : 1, observer.Observations);
            Assert.Equal(0, initialized);

            agent.SetModel(later);
            var second = agent.InitializeAsync();
            Assert.Same(first, second);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => second));
            Assert.Same(later, agent.Model);
            AssertStateAndNoOperations(agent, store, original, selected, later, constructionReads);
            Assert.Equal(1, plugin.Initializations);
            Assert.Equal(failInPlugin ? 0 : 1, plugin.ToolRequests);
            Assert.Equal(failInPlugin ? 0 : 1, observer.Observations);
            Assert.Equal(0, initialized);

            var manual = new AgentSnapshot(new Dictionary<string, JsonElement>
            {
                ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("manual history") }, Web),
                ["modelState"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["responseId"] = "manual-provider" }, Web)
            });
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.LoadSnapshotAsync(manual).AsTask()));
            Assert.Same(first, agent.InitializeAsync());
            Assert.Same(later, agent.Model);
            AssertStateAndNoOperations(agent, store, original, selected, later, constructionReads);
            Assert.Equal(1, plugin.Initializations);
            Assert.Equal(failInPlugin ? 0 : 1, plugin.ToolRequests);
            Assert.Equal(failInPlugin ? 0 : 1, observer.Observations);
            Assert.Equal(0, initialized);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                await agent.DisposeAsync();
                Assert.Equal(0, original.DisposeCalls);
                Assert.Equal(0, selected.DisposeCalls);
                Assert.Equal(0, later.DisposeCalls);
            }
            catch (Exception cleanup) when (primary is not null)
            { if (!ReferenceEquals(primary, cleanup)) primary.Data["PrimarySourceRestoreFailure.Cleanup"] = cleanup; }
            finally
            {
                await original.DisposeAsync();
                await selected.DisposeAsync();
                await later.DisposeAsync();
            }
        }
    }

    private static void AssertStateAndNoOperations(Agent agent, MemoryStore store, ProbeModel original, ProbeModel selected, ProbeModel later, int constructionReads)
    {
        Assert.Equal(1, store.Loads);
        Assert.Equal(0, store.Saves);
        Assert.Collection(agent.Messages,
            message => { Assert.Equal(Role.User, message.Role); Assert.Equal("saved user", message.Text); },
            message => { Assert.Equal(Role.Assistant, message.Role); Assert.Equal("saved assistant", message.Text); });
        Assert.Equal("saved-provider", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(23, agent.ModelState.Get("nested")!.Value.GetProperty("sequence").GetInt32());
        Assert.False(agent.ModelState.Get("configuredOnly").HasValue);
        Assert.Equal(constructionReads, original.StatefulReads);
        Assert.Equal(0, selected.StatefulReads);
        Assert.Equal(0, later.StatefulReads);
        foreach (var model in new[] { original, selected, later })
        {
            Assert.Equal(0, model.FactoryCalls);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Equal(0, model.DisposeCalls);
        }
    }

    private sealed class ProbeModel(bool stateful) : IStatefulModel, IInvocationModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful { get { StatefulReads++; return stateful; } }
        internal int StatefulReads { get; private set; }
        internal int FactoryCalls { get; private set; }
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState)
        { FactoryCalls++; throw new InvalidOperationException("Initialization must not create a provider scope."); }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { StreamCalls++; throw new InvalidOperationException("Initialization must not enter a provider stream."); }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { CountCalls++; throw new InvalidOperationException("Initialization must not count a provider request."); }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class ReplacingPlugin(IModel replacement, Exception? failure) : IPlugin
    {
        public string Name => "source-restore:failure-guard";
        internal int Initializations { get; private set; }
        internal int ToolRequests { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Initializations++;
            agent.SetModel(replacement);
            if (failure is not null) throw failure;
            return default;
        }
        public IReadOnlyList<ITool> GetTools() { ToolRequests++; return []; }
    }

    private sealed class FailingObserver(Exception failure) : InterventionHandler, IAgentLifecycleObserver
    {
        public override string Name => "source-restore:failing-observer";
        internal int Observations { get; private set; }
        public ValueTask ObserveAgentAsync(Agent agent, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Observations++; throw failure; }
    }

    private sealed class MemoryStore : ISessionStore
    {
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loads++;
            return new ValueTask<SessionSnapshot?>(new SessionSnapshot(1, [Message.User("saved user"), Message.Assistant("saved assistant")])
            {
                ModelState = new Dictionary<string, JsonElement>
                {
                    ["responseId"] = JsonSerializer.SerializeToElement("saved-provider"),
                    ["nested"] = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["sequence"] = 23 })
                }
            });
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saves++; return default; }
    }
}
