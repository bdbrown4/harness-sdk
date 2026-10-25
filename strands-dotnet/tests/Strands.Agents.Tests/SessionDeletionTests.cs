using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed partial class SessionDeletionTests
{
    [Fact]
    public async Task DeletingAnOpaqueSessionPreservesSiblingRecordsAndFreshAgentRestoration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-session-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new FileSessionStore(directory);
            var deleted = new SessionSnapshot(1, [Message.User("deleted question"), Message.Assistant("deleted answer")])
            {
                State = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("deleted") }
            };
            var sibling = new SessionSnapshot(1, [Message.User("sibling question"), Message.Assistant("sibling answer")])
            {
                State = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("retained") }
            };
            const string key = "../../opaque\\session";
            await first.SaveAsync(key, deleted);
            await first.SaveAsync("sibling", sibling);
            var unrelated = Path.Combine(directory, "application-owned.txt");
            File.WriteAllText(unrelated, "keep this file");
            var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
            var reopened = new FileSessionStore(directory);

            await Assert.IsAssignableFrom<IDeletableSessionStore>(reopened).DeleteAsync(key);

            Assert.Null(await new FileSessionStore(directory).LoadAsync(key));
            var savedSibling = await new FileSessionStore(directory).LoadAsync("sibling");
            Assert.Equal(sibling.Messages[1].TrackingId, savedSibling!.Messages[1].TrackingId);
            Assert.Equal("retained", savedSibling.State!["value"].GetString());
            foreach (var path in Directory.GetFiles(directory)) Assert.Equal(before[path], File.ReadAllBytes(path));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            await using var emptyAgent = new Agent(new() { Model = new NeverCalledModel(), SessionStore = new FileSessionStore(directory), SessionId = key });
            await using var siblingAgent = new Agent(new() { Model = new NeverCalledModel(), SessionStore = new FileSessionStore(directory), SessionId = "sibling" });
            await emptyAgent.InitializeAsync();
            await siblingAgent.InitializeAsync();
            Assert.Empty(emptyAgent.Messages);
            Assert.Null(emptyAgent.State.Get("value"));
            Assert.Equal(sibling.Messages[1].TrackingId, siblingAgent.Messages[1].TrackingId);
            Assert.Equal("retained", siblingAgent.State.Get<string>("value"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExistingTwoMethodSessionStoresRemainUsableWithoutDeletion()
    {
        var store = new ExistingStore();
        Assert.False((object)store is IDeletableSessionStore);
        await using var agent = new Agent(new() { Model = new NeverCalledModel(), SessionStore = store, SessionId = "existing" });
        await agent.InitializeAsync();
        Assert.Equal("existing answer", agent.Messages[1].Text);
        Assert.Equal(1, store.Loads);
    }

    private sealed class ExistingStore : ISessionStore
    {
        internal int Loads;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Loads++;
            return new(new SessionSnapshot(1, [Message.User("existing question"), Message.Assistant("existing answer")]));
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }
    private sealed class NeverCalledModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Session restoration must not call a model.");
    }
}
