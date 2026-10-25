namespace Strands.Agents.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task FailedRestorationNeverWritesSeedHistoryToTheUnreadSession()
    {
        var store = new UnreadableStore();
        var agent = new Agent(new()
        {
            Model = new AgentTests.ScriptedModel(),
            SessionStore = store,
            SessionId = "existing",
            Messages = [Message.User("seed"), new Message(Role.Assistant,
                [new ToolUseBlock("pending", "save", System.Text.Json.JsonSerializer.SerializeToElement(new { }))])]
        });

        await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("continue"));

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task RejectsOrphanedToolResultsBeforeWritingSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-dotnet-tests", Guid.NewGuid().ToString());
        var store = new FileSessionStore(directory);
        var invalid = new SessionSnapshot(1, [new Message(Role.User, [new ToolResultBlock("orphan", "untrusted")])]);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("invalid", invalid).AsTask());
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileStoreRoundTripsPolymorphicContentAndOpaqueIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-dotnet-tests", Guid.NewGuid().ToString());
        try
        {
            var store = new FileSessionStore(directory);
            var message = Message.Assistant("saved");
            var snapshot = new SessionSnapshot(1, [Message.User("hello"), message]);

            await store.SaveAsync("../../opaque\\session", snapshot);
            var restored = await store.LoadAsync("../../opaque\\session");

            Assert.NotNull(restored);
            Assert.Equal(message.TrackingId, restored.Messages[1].TrackingId);
            Assert.Equal("saved", restored.Messages[1].Text);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
            Assert.Contains("\"role\":\"user\"", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory, "*.json"))));
            Assert.Null(await store.LoadAsync("missing"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class UnreadableStore : ISessionStore
    {
        public int SaveCount { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            new(Task.FromException<SessionSnapshot?>(new IOException("Cannot read existing session.")));
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }
}
