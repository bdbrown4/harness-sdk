using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Strands.Agents.Tests;

public sealed class SessionPromptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("original")]
    public async Task OrdinarySessionWritesAndRestoresThePromptIncludingAnExplicitNull(string? prompt)
    {
        var store = new Store();
        await using (var source = new Agent(new() { Model = new Model(), SystemPrompt = prompt, SessionStore = store, SessionId = "session" }))
            await source.InvokeAsync("question");
        Assert.True(store.Snapshot!.HasSystemPrompt); Assert.Equal(prompt, store.Snapshot.SystemPrompt);
        await using var target = new Agent(new() { Model = new Model(), SystemPrompt = "replacement configuration", SessionStore = store, SessionId = "session" });
        await target.InitializeAsync();
        Assert.Equal(prompt, target.SystemPrompt);
    }

    [Fact]
    public async Task AnExplicitNullSessionPromptClearsTheConfiguredPrompt()
    {
        var store = new Store { Snapshot = new(1, []) { HasSystemPrompt = true, SystemPrompt = null } };
        await using var agent = new Agent(new() { Model = new Model(), SystemPrompt = "configured", SessionStore = store, SessionId = "session" });
        await agent.InitializeAsync();
        Assert.Null(agent.SystemPrompt);
    }

    [Fact]
    public async Task MissingPromptFieldsInALegacyFilePreserveTheConfiguredPrompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-session-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory); await store.SaveAsync("session", new(1, []));
            var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); root.Remove("hasSystemPrompt"); root.Remove("systemPrompt"); File.WriteAllText(path, root.ToJsonString());
            await using var agent = new Agent(new() { Model = new Model(), SystemPrompt = "preserved", SessionStore = store, SessionId = "session" });
            await agent.InitializeAsync();
            Assert.Equal("preserved", agent.SystemPrompt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FileSessionRoundTripRetainsTheExplicitNullMarker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-session-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory); await store.SaveAsync("session", new(1, []) { HasSystemPrompt = true, SystemPrompt = null });
            var snapshot = await store.LoadAsync("session");
            Assert.True(snapshot!.HasSystemPrompt); Assert.Null(snapshot.SystemPrompt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class Store : ISessionStore
    {
        public SessionSnapshot? Snapshot { get; set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshot = snapshot; return default; }
    }
    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
