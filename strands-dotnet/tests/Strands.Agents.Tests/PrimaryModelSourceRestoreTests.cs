using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelSourceRestoreTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, true)]
    [InlineData(ModelHistoryBehavior.TypeScript, false)]
    [InlineData(ModelHistoryBehavior.Python, true)]
    [InlineData(ModelHistoryBehavior.Python, false)]
    public async Task InitializedRestoreUsesCurrentCapabilityAndKeepsProviderState(ModelHistoryBehavior profile, bool currentStateful)
    {
        var original = new ProbeModel(!currentStateful);
        var selected = new ProbeModel(currentStateful);
        var store = new MemoryStore(SavedSession());
        var agent = new Agent(new()
        {
            Model = original,
            ModelHistoryBehavior = profile,
            SessionStore = store,
            SessionId = "source-restore",
            Messages = [Message.User("configured history")],
            ModelState = new Dictionary<string, JsonElement> { ["configuredOnly"] = JsonSerializer.SerializeToElement(true) }
        });
        Exception? primary = null;
        try
        {
            agent.SetModel(selected);
            await agent.InitializeAsync();

            Assert.Same(selected, agent.Model);
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, store.Saves);
            AssertProviderState(agent);
            AssertNeverInvoked(original, selected);
            if (currentStateful) Assert.Empty(agent.Messages);
            else AssertSavedMessages(agent);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeFixture(agent, original, selected, primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task PluginReplacementBeforeInitializedRestoreControlsDiscard(ModelHistoryBehavior profile)
    {
        var original = new ProbeModel(false);
        var selected = new ProbeModel(true);
        var replacement = new ReplacementPlugin(selected);
        var store = new MemoryStore(SavedSession());
        var agent = new Agent(new()
        {
            Model = original,
            ModelHistoryBehavior = profile,
            SessionStore = store,
            SessionId = "source-plugin-restore",
            Plugins = [replacement]
        });
        Exception? primary = null;
        try
        {
            await agent.InitializeAsync();

            Assert.Equal(1, replacement.Initializations);
            Assert.Same(selected, agent.Model);
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, store.Saves);
            AssertProviderState(agent);
            AssertNeverInvoked(original, selected);
            Assert.Empty(agent.Messages);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeFixture(agent, original, selected, primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task MissingInitialSessionAndExplicitSnapshotKeepCurrentStatefulHistory(ModelHistoryBehavior profile)
    {
        var original = new ProbeModel(false);
        var selected = new ProbeModel(true);
        var store = new MemoryStore(null);
        var agent = new Agent(new()
        {
            Model = original,
            ModelHistoryBehavior = profile,
            SessionStore = store,
            SessionId = "source-manual-restore",
            Messages = [Message.User("configured history")],
            ModelState = new Dictionary<string, JsonElement> { ["configuredOnly"] = JsonSerializer.SerializeToElement(true) }
        });
        var saved = SavedSession();
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(saved.Messages, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(saved.ModelState, Web)
        });
        Exception? primary = null;
        try
        {
            agent.SetModel(selected);
            await agent.InitializeAsync();
            Assert.Equal("configured history", Assert.Single(agent.Messages).Text);
            Assert.True(agent.ModelState.Get<bool>("configuredOnly"));

            await agent.LoadSnapshotAsync(snapshot);

            Assert.Same(selected, agent.Model);
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, store.Saves);
            AssertProviderState(agent);
            AssertNeverInvoked(original, selected);
            AssertSavedMessages(agent);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeFixture(agent, original, selected, primary); }
    }

    private static SessionSnapshot SavedSession()
        => new(1, [Message.User("saved user"), Message.Assistant("saved assistant")])
        {
            ModelState = new Dictionary<string, JsonElement>
            {
                ["responseId"] = JsonSerializer.SerializeToElement("saved-provider"),
                ["nested"] = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["sequence"] = 23 })
            }
        };

    private static void AssertProviderState(Agent agent)
    {
        Assert.Equal("saved-provider", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(23, agent.ModelState.Get("nested")!.Value.GetProperty("sequence").GetInt32());
        Assert.False(agent.ModelState.Get("configuredOnly").HasValue);
    }

    private static void AssertSavedMessages(Agent agent)
        => Assert.Collection(agent.Messages,
            message => { Assert.Equal(Role.User, message.Role); Assert.Equal("saved user", message.Text); },
            message => { Assert.Equal(Role.Assistant, message.Role); Assert.Equal("saved assistant", message.Text); });

    private static void AssertNeverInvoked(ProbeModel original, ProbeModel selected)
    {
        Assert.Equal(0, original.StreamCalls);
        Assert.Equal(0, selected.StreamCalls);
        Assert.Equal(0, original.CountCalls);
        Assert.Equal(0, selected.CountCalls);
    }

    private static async ValueTask DisposeFixture(Agent agent, ProbeModel original, ProbeModel selected, Exception? primary)
    {
        try
        {
            await agent.DisposeAsync();
            Assert.Equal(0, original.DisposeCalls);
            Assert.Equal(0, selected.DisposeCalls);
        }
        catch (Exception cleanup) when (primary is not null)
        { if (!ReferenceEquals(primary, cleanup)) primary.Data["PrimarySourceRestore.Cleanup"] = cleanup; }
        finally
        {
            await original.DisposeAsync();
            await selected.DisposeAsync();
        }
    }

    private sealed class ProbeModel(bool stateful) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => stateful;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { StreamCalls++; throw new InvalidOperationException("Restoring an inactive snapshot must not enter a provider."); }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { CountCalls++; throw new InvalidOperationException("Restoring an inactive snapshot must not count a provider request."); }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class ReplacementPlugin(IModel replacement) : IPlugin
    {
        public string Name => "primary-source-restore:replacement";
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); agent.SetModel(replacement); Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class MemoryStore(SessionSnapshot? saved) : ISessionStore
    {
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Loads++; return new ValueTask<SessionSnapshot?>(saved); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saves++; return default; }
    }
}
