using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositorySessionManagerTests
{
    [Fact]
    public async Task InvokingARealAgentCreatesSessionMetadataAndOneRowPerAcceptedMessage()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var journal = new RepositorySessionManager("session", repository);
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [journal] });
        var result = await agent.InvokeAsync("question");
        Assert.Equal("answer", result.ToString());
        Assert.NotNull(await repository.ReadSessionAsync("session"));
        Assert.NotNull(await repository.ReadAgentAsync("session", "agent"));
        var rows = await repository.ListMessagesAsync("session", "agent");
        Assert.Equal(new long[] { 0, 1 }, rows.Select(row => row.MessageId));
        Assert.Equal(new[] { "question", "answer" }, rows.Select(row => row.ToMessage().Text));
    }

    [Fact]
    public async Task ConfiguredInitialMessagesAreStoredOnceAcrossRepeatedInitialization()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Messages = [Message.User("existing")], Plugins = [new RepositorySessionManager("session", repository)] });
        await agent.InitializeAsync();
        await agent.InitializeAsync();
        var row = Assert.Single(await repository.ListMessagesAsync("session", "agent"));
        Assert.Equal(0, row.MessageId);
        Assert.Equal("existing", row.Message.Text);
    }

    [Fact]
    public async Task RestorePrecedesPluginStateReadsAndDoesNotEraseTheirInitializationWrites()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        await repository.CreateSessionAsync(new("session"));
        await repository.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { stored = "retained" }), JsonSerializer.SerializeToElement(new { })));
        await repository.CreateMessageAsync("session", "agent", new(Message.User("stored history"), 0));
        var plugin = new StateReadingPlugin();
        var observed = false;
        var hooks = new HookRegistry();
        using var registration = hooks.Register<InitializedEvent>((value, _) =>
        {
            observed = value.Agent.State.Get("stored")?.GetString() == "retained" && value.Agent.State.Get("plugin")?.GetString() == "initialized";
            return default;
        });
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Hooks = hooks, Plugins = [new RepositorySessionManager("session", repository), plugin] });
        await agent.InitializeAsync();
        Assert.Equal("retained", plugin.Observed);
        Assert.True(observed);
        Assert.Equal("initialized", agent.State.Get("plugin")?.GetString());
        Assert.Equal("stored history", Assert.Single(agent.Messages).Text);
    }
}

internal sealed class JournalTestModel : IModel
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelMessageStartEvent();
        yield return new ModelContentBlockStartEvent(0);
        yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
        yield return new ModelContentBlockStopEvent(0);
        yield return new ModelMessageStopEvent(StopReasons.EndTurn);
    }
}

internal sealed class StateReadingPlugin : IPlugin
{
    public string Name => "test:journal-state-reader";
    public string? Observed { get; private set; }
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Observed = agent.State.Get("stored")?.GetString();
        agent.State.Set("plugin", "initialized");
        return default;
    }
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
}
