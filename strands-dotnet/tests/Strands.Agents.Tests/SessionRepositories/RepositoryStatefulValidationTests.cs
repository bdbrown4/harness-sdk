using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryStatefulValidationTests
{
    [Theory]
    [InlineData("missing-projection")]
    [InlineData("projection-kind")]
    [InlineData("projection-version")]
    [InlineData("ids-kind")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("fraction")]
    [InlineData("missing-row")]
    [InlineData("non-suffix")]
    [InlineData("excluded-row")]
    [InlineData("unconfirmed-row")]
    [InlineData("missing-frontier")]
    [InlineData("missing-cursor")]
    [InlineData("negative-cursor")]
    [InlineData("cursor-past-end")]
    [InlineData("cursor-on-user")]
    [InlineData("missing-provider-state")]
    [InlineData("wrong-version")]
    [InlineData("owner")]
    [InlineData("broken-history")]
    public async Task InvalidActiveProjectionRejectsBeforePublishingAnyRestoredFields(string mutation)
    {
        var repository = await SeedAsync();
        var stored = (await repository.Inner.ReadAgentAsync("session", "agent"))!;
        var metadata = JsonNode.Parse(stored.InternalState.GetRawText())!.AsObject();
        var projection = metadata["statefulProjection"]!.AsObject();
        var snapshot = metadata["snapshot"]!.AsObject();
        switch (mutation)
        {
            case "missing-projection": metadata.Remove("statefulProjection"); break;
            case "projection-kind": metadata["statefulProjection"] = true; break;
            case "projection-version": projection["version"] = 99; break;
            case "ids-kind": projection["activeMessageIds"] = "0"; break;
            case "duplicate": projection["activeMessageIds"] = JsonNode.Parse("[0,1,1,3]"); break;
            case "unordered": projection["activeMessageIds"] = JsonNode.Parse("[1,0,2,3]"); break;
            case "fraction": projection["activeMessageIds"] = JsonNode.Parse("[0,1,2,3.5]"); break;
            case "missing-row": projection["activeMessageIds"] = JsonNode.Parse("[0,1,2,9]"); break;
            case "non-suffix": projection["activeMessageIds"] = JsonNode.Parse("[0,3]"); snapshot["modelHistoryCount"] = 2; break;
            case "excluded-row": metadata["journalFrontier"]!["excludedMessageIds"] = JsonNode.Parse("[1]"); break;
            case "unconfirmed-row": metadata["journalFrontier"]!["lastMessageId"] = 2; break;
            case "missing-frontier": metadata.Remove("journalFrontier"); break;
            case "missing-cursor": snapshot.Remove("modelHistoryCount"); break;
            case "negative-cursor": snapshot["modelHistoryCount"] = -1; break;
            case "cursor-past-end": snapshot["modelHistoryCount"] = 5; break;
            case "cursor-on-user": snapshot["modelHistoryCount"] = 3; break;
            case "missing-provider-state": snapshot["modelState"] = new JsonObject(); break;
            case "wrong-version": metadata["nativeSessionVersion"] = 1; break;
            case "owner": snapshot["agentId"] = "someone-else"; break;
            case "broken-history":
                await repository.Inner.UpdateMessageAsync("session", "agent", new(new Message(Role.User, [new ToolResultBlock("orphan", "invalid")]), 2));
                break;
        }
        await repository.Inner.UpdateAgentAsync("session", new("agent", stored.State, stored.ConversationManagerState, JsonSerializer.SerializeToElement(metadata)));
        repository.Operations.Clear();
        var initialized = false;
        var plugin = new StateReadingPlugin();
        var hooks = new HookRegistry(); hooks.Register<InitializedEvent>((_, _) => { initialized = true; return default; });
        await using var agent = new Agent(new()
        {
            Id = "agent",
            Model = new JournalStatefulModel(),
            Messages = [Message.User("configured")],
            State = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) },
            ModelState = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) },
            SystemPrompt = "configured",
            Hooks = hooks,
            Plugins = [new RepositorySessionManager("session", repository), plugin]
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Equal("configured", agent.SystemPrompt);
        Assert.Null(agent.State.Get("stored")); Assert.True(agent.State.Get<bool>("configured"));
        Assert.Null(agent.ModelState.Get("responseId")); Assert.True(agent.ModelState.Get<bool>("configured"));
        Assert.Null(plugin.Observed); Assert.False(initialized);
        Assert.DoesNotContain(repository.Operations, operation => operation.StartsWith("create-", StringComparison.Ordinal) || operation.StartsWith("update-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatefulAndStatelessMetadataCannotSilentlyChangeModes(bool statefulTarget)
    {
        var repository = await SeedAsync();
        if (statefulTarget)
        {
            var stored = (await repository.Inner.ReadAgentAsync("session", "agent"))!;
            var legacy = JsonSerializer.SerializeToElement(new { nativeSessionVersion = 1, snapshot = new SessionSnapshot(1, []) }, SessionRecordCodec.Options);
            await repository.Inner.UpdateAgentAsync("session", new("agent", stored.State, stored.ConversationManagerState, legacy));
        }
        await using var agent = new Agent(new() { Id = "agent", Model = statefulTarget ? new JournalStatefulModel() : new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Empty(agent.Messages); Assert.Empty(agent.State.GetAll());
    }

    [Theory]
    [InlineData("read-agent")]
    [InlineData("list-messages")]
    public async Task RequestedCancellationAfterBackendReadPreservesTheCallerAndConfiguredState(string boundary)
    {
        var repository = await SeedAsync(ignoreReadCancellation: true);
        using var caller = new CancellationTokenSource();
        repository.Before = (operation, _) => { if (operation == boundary) caller.Cancel(); return default; };
        var manager = new RepositorySessionManager("session", repository);
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalStatefulModel(), Messages = [Message.User("configured")], Plugins = [manager] });
        var initialization = agent.InitializeAsync(caller.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Empty(agent.ModelState.GetAll());
        var reads = repository.Operations.Count;
        repository.Before = null;
        var repeated = agent.InitializeAsync();
        Assert.Same(initialization, repeated);
        Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated)).CancellationToken);
        Assert.Equal(reads, repository.Operations.Count);
        await using var fresh = new Agent(new() { Id = "agent", Model = new JournalStatefulModel(), Plugins = [manager] });
        await fresh.InitializeAsync();
        Assert.Equal(4, fresh.Messages.Count);
        Assert.Equal("accepted", fresh.ModelState.Get<string>("responseId"));
    }

    internal static async Task<JournalProbeRepository> SeedAsync(bool ignoreReadCancellation = false)
    {
        var repository = new JournalProbeRepository { IgnoreReadCancellation = ignoreReadCancellation };
        await repository.Inner.CreateSessionAsync(new("session"));
        var snapshot = new SessionSnapshot(1, [])
        {
            AgentId = "agent",
            ModelHistoryCount = 4,
            ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("accepted") },
            HasSystemPrompt = true,
            SystemPrompt = "stored"
        };
        var metadata = JsonSerializer.SerializeToElement(new
        {
            nativeSessionVersion = 2,
            snapshot,
            journalFrontier = new { lastMessageId = 3, excludedMessageIds = Array.Empty<long>() },
            statefulProjection = new { version = 1, activeMessageIds = new long[] { 0, 1, 2, 3 } }
        }, SessionRecordCodec.Options);
        await repository.Inner.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { stored = true }), JsonSerializer.SerializeToElement(new { }), metadata));
        await repository.Inner.CreateMessageAsync("session", "agent", new(Message.User("first"), 0));
        await repository.Inner.CreateMessageAsync("session", "agent", new(Message.Assistant("accepted first"), 1));
        await repository.Inner.CreateMessageAsync("session", "agent", new(Message.User("second"), 2));
        await repository.Inner.CreateMessageAsync("session", "agent", new(Message.Assistant("accepted second"), 3));
        return repository;
    }
}
