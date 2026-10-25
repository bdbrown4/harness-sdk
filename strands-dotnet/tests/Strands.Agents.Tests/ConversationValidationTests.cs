using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ConversationValidationTests
{
    [Fact]
    public void InvalidToolResultStatusIsRejectedInInitialHistory()
    {
        Assert.Throws<InvalidDataException>(() => new Agent(new() { Model = new UnusedModel(), Messages = InvalidHistory() }));
    }

    [Fact]
    public async Task InvalidToolResultStatusIsRejectedBeforeRestoredHistoryCanReachTheModel()
    {
        var store = new InvalidSnapshotStore();
        await using var agent = new Agent(new() { Model = new UnusedModel(), SessionStore = store, SessionId = "invalid" });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());

        Assert.Empty(agent.Messages);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task InvalidToolResultStatusCannotBePersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-dotnet-tests", Guid.NewGuid().ToString());
        try
        {
            var store = new FileSessionStore(directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("invalid", new(1, InvalidHistory())).AsTask());
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Message[] InvalidHistory() =>
    [
        new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
        new(Role.User, [new ToolResultBlock("call", "untrusted status", (ToolResultStatus)42)])
    ];

    private sealed class InvalidSnapshotStore : ISessionStore
    {
        internal int Writes;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(new SessionSnapshot(1, InvalidHistory()));
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Writes++; return default; }
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Invalid history must not reach a model.");
    }
}
