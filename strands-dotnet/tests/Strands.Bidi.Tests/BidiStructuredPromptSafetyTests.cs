using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Storage;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiStructuredPromptSafetyTests
{
    [Theory]
    [InlineData("[]", null)]
    [InlineData("[{\"type\":\"text\",\"text\":\"\"}]", "")]
    [InlineData("[{\"type\":\"cachePoint\",\"ttl\":\"custom\"}]", null)]
    [InlineData("[{\"type\":\"text\",\"text\":\"\"},{\"type\":\"text\",\"text\":\"second\"}]", "\nsecond")]
    public async Task TextProjectionDistinguishesNoTextFromEmptyText(string json, string? projection)
    {
        var content = JsonSerializer.Deserialize<ContentBlock[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "fallback" }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SystemPromptContent = content });
        Assert.Equal(projection, agent.SystemPrompt); Assert.Equal(content, agent.SystemPromptContent);
        await agent.StartAsync(); Assert.Equal(projection, model.Session!.SystemPrompt); await agent.StopAsync();
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"type\":\"text\",\"text\":null}]")]
    [InlineData("[{\"type\":\"reasoning\",\"text\":\"private\"}]")]
    [InlineData("[{\"type\":\"json\",\"value\":{}}]")]
    public async Task InvalidContentFailsBeforeInitializationAndCannotPartiallyReplaceFields(string json)
    {
        var store = new BidiPersistenceTests.MemoryStore();
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SystemPrompt = "old", Messages = [Message.User("old")] });
        agent.State.Set("old", true);
        var content = JsonSerializer.Deserialize<ContentBlock[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<ArgumentException>(() => agent.SetSystemPromptContent(content));
        Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, SystemPromptContent = content }));
        var data = new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.Deserialize<JsonElement>(json), ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("new") }), ["state"] = JsonSerializer.SerializeToElement(new { changed = true }) };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(new(data)));
        Assert.Equal("old", agent.SystemPrompt); Assert.Equal("old", Assert.Single(agent.Messages).Text); Assert.True(agent.State.Get<bool>("old")); Assert.Equal(0, store.Loads); Assert.Equal(0, model.Starts);
    }

    [Fact]
    public async Task MutuallyExclusiveConstructorFormsAreRejectedWithoutLoadingAStore()
    {
        var store = new BidiPersistenceTests.MemoryStore();
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SystemPrompt = "text", SystemPromptContent = [] }));
        Assert.Equal(0, store.Loads); Assert.Equal(0, model.Starts);
    }

    [Theory]
    [InlineData(false, "ignored legacy", "configured")]
    [InlineData(true, null, null)]
    [InlineData(true, "", "")]
    public async Task AbsentStructuredFieldsRetainTheExistingPromptPresenceSemantics(bool present, string? saved, string? expected)
    {
        var store = new BidiPersistenceTests.MemoryStore { Snapshot = new(1, []) { HasSystemPrompt = present, SystemPrompt = saved } };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SystemPrompt = "configured" });
        await agent.InitializeAsync(); Assert.Equal(expected, agent.SystemPrompt);
        if (expected is null) Assert.Null(agent.SystemPromptContent);
        else Assert.Equal(expected, Assert.IsType<TextBlock>(Assert.Single(agent.SystemPromptContent!)).Text);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"messages\":[]}", "configured")]
    [InlineData("{\"schemaVersion\":1,\"messages\":[],\"hasSystemPrompt\":true,\"systemPrompt\":null}", null)]
    [InlineData("{\"schemaVersion\":1,\"messages\":[],\"hasSystemPrompt\":true,\"systemPrompt\":\"old string\"}", "old string")]
    public async Task OldFileJsonWithoutTheNewFieldRemainsReadable(string json, string? expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-legacy-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory); await store.SaveAsync("legacy", new(1, []));
            File.WriteAllText(Assert.Single(Directory.GetFiles(directory, "*.json")), json);
            Assert.Null((await store.LoadAsync("legacy"))!.SystemPromptContent);
            await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
            await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SessionId = "legacy", SystemPrompt = "configured" });
            await agent.InitializeAsync(); Assert.Equal(expected, agent.SystemPrompt); Assert.Equal(0, model.Starts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "different")]
    public async Task BorrowedStoreStructuredTamperingFailsBeforeAnyAcceptedMutation(bool present, string? projection)
    {
        var store = new BidiPersistenceTests.MemoryStore { Snapshot = new(1, [Message.User("replacement")]) { HasSystemPrompt = present, SystemPrompt = projection, SystemPromptContent = [new TextBlock("actual")] } };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SystemPrompt = "old", Messages = [Message.User("old")] });
        var initialized = 0; agent.Hooks.Register<BidiInitializedEvent>((_, _) => { initialized++; return default; });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal("old", agent.SystemPrompt); Assert.Equal("old", Assert.Single(agent.Messages).Text); Assert.Equal(0, initialized); Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task FileStoreCopiesStructuredContentBeforeItsFirstAsyncWriteAndRejectsTamperedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-prompt-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = new List<ContentBlock> { new TextBlock("saved"), new CachePointBlock("custom") };
            var store = new FileSessionStore(directory);
            var saving = store.SaveAsync("id", new(1, []) { HasSystemPrompt = true, SystemPrompt = "saved", SystemPromptContent = content });
            content.Clear(); await saving;
            var loaded = await store.LoadAsync("id"); Assert.Equal(2, loaded!.SystemPromptContent!.Count); Assert.Equal("custom", Assert.IsType<CachePointBlock>(loaded.SystemPromptContent[1]).Ttl);
            Assert.Throws<NotSupportedException>(() => ((IList<ContentBlock>)loaded.SystemPromptContent).Clear());
            var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
            File.WriteAllText(path, JsonSerializer.Serialize(loaded with { SystemPrompt = "tampered" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync("id"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AChangedActivePromptIsAppliedOnTheNextConnectionAndRetainedByTheCatalog()
    {
        var storage = new InMemoryStorage(); var manager = new BidiSnapshotSessionManager(storage, "prompts");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new BidiRestartTests.RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager, SystemPrompt = "first" });
        await agent.StartAsync(); var mutable = new List<ContentBlock> { new TextBlock("next"), new CachePointBlock("1h") }; agent.SetSystemPromptContent(mutable); mutable.Clear();
        Assert.Equal("first", model.Sessions[0].SystemPrompt); Assert.Throws<NotSupportedException>(() => ((IList<ContentBlock>)agent.SystemPromptContent!).Clear());
        model.Current.Emit(new BidiConnectionTimeoutException("restart"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current); await Within(model.Reconnected.Task);
        Assert.Equal("next", model.Sessions[1].SystemPrompt); await agent.StopAsync();
        await using var fresh = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = new(storage, "prompts") }); await fresh.InitializeAsync();
        Assert.Equal("next", fresh.SystemPrompt); Assert.Equal("1h", Assert.IsType<CachePointBlock>(fresh.SystemPromptContent![1]).Ttl);
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }
}
