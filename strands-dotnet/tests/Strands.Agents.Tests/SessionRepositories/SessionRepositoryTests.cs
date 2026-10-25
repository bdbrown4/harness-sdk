using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class SessionRepositoryTests
{
    [Fact]
    public async Task SessionCreateReadAndDuplicateAreRealRepositoryOperations()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        Assert.Null(await repository.ReadSessionAsync("missing"));
        var session = new SessionRecord("session");
        Assert.Equal(session, await repository.CreateSessionAsync(session));
        Assert.Equal(session, await repository.ReadSessionAsync("session"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.CreateSessionAsync(session));
    }

    [Fact]
    public async Task AgentCreatesUpsertWhileUpdatesRequireExistingAndPreserveCreatedAt()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var first = new SessionAgentRecord("agent", Object("one"), Object("manager"), Object("internal"), DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        Assert.Null(await repository.ReadAgentAsync("session", "agent"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.UpdateAgentAsync("session", first));
        await repository.CreateAgentAsync("session", first);
        var update = new SessionAgentRecord("agent", Object("two"), Object("next"));
        await repository.UpdateAgentAsync("session", update);
        var saved = Assert.IsType<SessionAgentRecord>(await repository.ReadAgentAsync("session", "agent"));
        Assert.Equal(first.CreatedAt, saved.CreatedAt);
        Assert.Equal(update.UpdatedAt, saved.UpdatedAt);
        Assert.Equal("two", saved.State.GetProperty("value").GetString());
        await repository.CreateAgentAsync("session", update);
        Assert.Equal(update.CreatedAt, (await repository.ReadAgentAsync("session", "agent"))!.CreatedAt);
    }

    [Fact]
    public async Task MessageCreatesUpsertAndRedactedUpdatesKeepOriginalAndCreatedAt()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var first = new SessionMessageRecord(new(Role.User, [new TextBlock("secret")], "same-id"), 2, createdAt: DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        Assert.Null(await repository.ReadMessageAsync("session", "agent", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.UpdateMessageAsync("session", "agent", first));
        await repository.CreateMessageAsync("session", "agent", first);
        var update = new SessionMessageRecord(first.Message, 2, new(Role.User, [new TextBlock("REDACTED")], "same-id"));
        await repository.UpdateMessageAsync("session", "agent", update);
        var saved = Assert.IsType<SessionMessageRecord>(await repository.ReadMessageAsync("session", "agent", 2));
        Assert.Equal("secret", saved.Message.Text);
        Assert.Equal("REDACTED", saved.ToMessage().Text);
        Assert.Equal("same-id", saved.ToMessage().TrackingId);
        Assert.Equal(first.CreatedAt, saved.CreatedAt);
        Assert.Equal(update.UpdatedAt, saved.UpdatedAt);
        await repository.CreateMessageAsync("session", "agent", update);
        Assert.Equal(update.CreatedAt, (await repository.ReadMessageAsync("session", "agent", 2))!.CreatedAt);
    }

    [Fact]
    public async Task SparseNumericIdsAreOrderedBeforeRowPaginationExactlyOnce()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        await repository.CreateAgentAsync("session", new("agent", Object("state"), Object("manager")));
        foreach (var id in new long[] { 10, -1, 2, 0 }) await repository.CreateMessageAsync("session", "agent", new(Message.User(id.ToString(System.Globalization.CultureInfo.InvariantCulture)), id));
        Assert.Equal(new long[] { -1, 0, 2, 10 }, (await repository.ListMessagesAsync("session", "agent")).Select(item => item.MessageId));
        Assert.Equal(new long[] { 0, 2 }, (await repository.ListMessagesAsync("session", "agent", 2, 1)).Select(item => item.MessageId));
        Assert.Equal(new long[] { 2, 10 }, (await repository.ListMessagesAsync("session", "agent", offset: 2)).Select(item => item.MessageId));
        Assert.Empty(await repository.ListMessagesAsync("session", "agent", limit: 0));
        Assert.Empty(await repository.ListMessagesAsync("session", "agent", offset: 99));
    }

    [Fact]
    public async Task MissingAgentListsFailAndAnExistingEmptyAgentHasNoRows()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.ListMessagesAsync("session", "missing"));
        await repository.CreateAgentAsync("session", new("agent", Object("state"), Object("manager")));
        Assert.Empty(await repository.ListMessagesAsync("session", "agent"));
    }

    [Fact]
    public async Task MultiAgentCreateReadAndUpdateUseDetachedExplicitIdentities()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        Assert.Null(await repository.ReadMultiAgentAsync("session", "graph"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.UpdateMultiAgentAsync("session", "graph", Object("first")));
        using (var producer = JsonDocument.Parse("{\"value\":\"first\"}")) await repository.CreateMultiAgentAsync("session", "graph", producer.RootElement);
        Assert.Equal("first", (await repository.ReadMultiAgentAsync("session", "graph"))!.Value.GetProperty("value").GetString());
        await repository.UpdateMultiAgentAsync("session", "graph", Object("second"));
        Assert.Equal("second", (await repository.ReadMultiAgentAsync("session", "graph"))!.Value.GetProperty("value").GetString());
        Assert.Null(await repository.ReadMultiAgentAsync("different", "graph"));
    }

    [Fact]
    public async Task FileRoundtripRetainsRichContentMetadataAndCallerJsonLifetime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands indexed " + Guid.NewGuid().ToString("N"));
        var cleanupDirectory = directory;
        try
        {
            var repository = new FileSessionRepository(directory);
            cleanupDirectory = repository.RootPath;
            using (var document = JsonDocument.Parse("{\"value\":\"owned\"}"))
            {
                await repository.CreateAgentAsync("session", new("agent", document.RootElement, document.RootElement, document.RootElement));
                var result = new ToolResultBlock("call", [new TextBlock("text"), new ImageBlock("image/png", "AQID"), new JsonBlock(document.RootElement)], structuredContent: document.RootElement) { Metadata = document.RootElement };
                await repository.CreateMessageAsync("session", "agent", new(new(Role.User, [result], "tracking", document.RootElement), 10));
            }
            var restored = Assert.IsType<SessionMessageRecord>(await new FileSessionRepository(directory).ReadMessageAsync("session", "agent", 10));
            Assert.Equal("tracking", restored.Message.TrackingId);
            Assert.Equal("owned", restored.Message.Metadata!.Value.GetProperty("value").GetString());
            var tool = Assert.Single(restored.Message.Content.OfType<ToolResultBlock>());
            Assert.Equal("AQID", Assert.IsType<ImageBlock>(tool.ContentBlocks[1]).Data);
            Assert.Equal("owned", tool.Metadata!.Value.GetProperty("value").GetString());
            Assert.Equal("owned", tool.StructuredContent!.Value.GetProperty("value").GetString());
            Assert.Equal("owned", (await new FileSessionRepository(directory).ReadAgentAsync("session", "agent"))!.InternalState.GetProperty("value").GetString());
        }
        finally { if (Directory.Exists(cleanupDirectory)) Directory.Delete(cleanupDirectory, true); }
    }

    internal static JsonElement Object(string value) => JsonSerializer.SerializeToElement(new { value });
}
