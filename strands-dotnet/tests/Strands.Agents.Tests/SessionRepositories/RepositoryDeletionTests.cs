using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryDeletionTests
{
    [Fact]
    public async Task ReopenedFileRepositoryDeletesOnlyTheSelectedOpaqueSessionAcrossAllRowKinds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-repository-delete-" + Guid.NewGuid().ToString("N"));
        var physicalDirectory = directory;
        Exception? primary = null;
        try
        {
            var repository = new FileSessionRepository(directory);
            physicalDirectory = repository.RootPath;
            const string selected = "../../opaque\\session";
            await Populate(repository, selected, "selected");
            await Populate(repository, "sibling", "retained");
            var storage = new LocalFileStorage(repository.RootPath);
            await storage.WriteAsync("application/unrelated", [7, 8, 9]);
            var before = new Dictionary<string, byte[]>();
            foreach (var key in await storage.ListAsync()) before[key] = (await storage.ReadAsync(key))!;
            Assert.Equal(9, before.Count);

            await Assert.IsAssignableFrom<IDeletableSessionRepository>(new FileSessionRepository(directory)).DeleteSessionAsync(selected);

            var reopened = new FileSessionRepository(directory);
            Assert.Null(await reopened.ReadSessionAsync(selected));
            Assert.Null(await reopened.ReadAgentAsync(selected, "agent"));
            Assert.Null(await reopened.ReadMessageAsync(selected, "agent", -4));
            Assert.Null(await reopened.ReadMultiAgentAsync(selected, "swarm"));
            Assert.NotNull(await reopened.ReadSessionAsync("sibling"));
            Assert.Equal("retained", (await reopened.ReadAgentAsync("sibling", "agent"))!.State.GetProperty("value").GetString());
            Assert.Equal("retained", (await reopened.ReadMessageAsync("sibling", "agent", -4))!.Message.Text);
            Assert.Equal("retained", (await reopened.ReadMultiAgentAsync("sibling", "swarm"))!.Value.GetProperty("value").GetString());
            var after = await storage.ListAsync();
            Assert.Equal(5, after.Count);
            foreach (var key in after) Assert.Equal(before[key], await storage.ReadAsync(key));
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { if (Directory.Exists(physicalDirectory)) Directory.Delete(physicalDirectory, recursive: true); }
            catch (Exception cleanup)
            {
                if (primary is null) primary = cleanup;
                else primary.Data["RepositoryDeletionTest.Cleanup"] = cleanup;
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    [Fact]
    public async Task ExistingNineMethodRepositoriesRemainUsableWithoutOptionalDeletion()
    {
        ISessionRepository repository = new ExistingRepository();
        Assert.False(repository is IDeletableSessionRepository);
        await repository.CreateSessionAsync(new("legacy"));
        await repository.CreateAgentAsync("legacy", new("agent", Object("original"), Object("manager")));
        await repository.CreateMessageAsync("legacy", "agent", new(Message.User("legacy message"), 3));
        Assert.Equal("legacy", (await repository.ReadSessionAsync("legacy"))!.SessionId);
        Assert.Equal("original", (await repository.ReadAgentAsync("legacy", "agent"))!.State.GetProperty("value").GetString());
        Assert.Equal("legacy message", Assert.Single(await repository.ListMessagesAsync("legacy", "agent")).Message.Text);
    }

    private static JsonElement Object(string value) => JsonSerializer.SerializeToElement(new { value });
    private static async Task Populate(StorageSessionRepository repository, string sessionId, string value)
    {
        await repository.CreateSessionAsync(new(sessionId));
        await repository.CreateAgentAsync(sessionId, new("agent", Object(value), Object("manager")));
        await repository.CreateMessageAsync(sessionId, "agent", new(Message.User(value), -4));
        await repository.CreateMultiAgentAsync(sessionId, "swarm", Object(value));
    }
    private sealed class ExistingRepository : ISessionRepository
    {
        private readonly StorageSessionRepository _inner = new(new InMemoryStorage());
        public ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken token = default) => _inner.CreateSessionAsync(session, token);
        public ValueTask<SessionRecord?> ReadSessionAsync(string sessionId, CancellationToken token = default) => _inner.ReadSessionAsync(sessionId, token);
        public ValueTask CreateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken token = default) => _inner.CreateAgentAsync(sessionId, agent, token);
        public ValueTask<SessionAgentRecord?> ReadAgentAsync(string sessionId, string agentId, CancellationToken token = default) => _inner.ReadAgentAsync(sessionId, agentId, token);
        public ValueTask UpdateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken token = default) => _inner.UpdateAgentAsync(sessionId, agent, token);
        public ValueTask CreateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken token = default) => _inner.CreateMessageAsync(sessionId, agentId, message, token);
        public ValueTask<SessionMessageRecord?> ReadMessageAsync(string sessionId, string agentId, long messageId, CancellationToken token = default) => _inner.ReadMessageAsync(sessionId, agentId, messageId, token);
        public ValueTask UpdateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken token = default) => _inner.UpdateMessageAsync(sessionId, agentId, message, token);
        public ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string sessionId, string agentId, int? limit = null, int offset = 0, CancellationToken token = default) => _inner.ListMessagesAsync(sessionId, agentId, limit, offset, token);
    }
}
