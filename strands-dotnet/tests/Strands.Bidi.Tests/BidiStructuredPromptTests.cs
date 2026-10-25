using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiStructuredPromptTests
{
    [Fact]
    public async Task StructuredConstructionDetachesBlocksAndSendsOnlyTheSourceTextProjection()
    {
        var content = new List<ContentBlock> { new TextBlock("first"), new CachePointBlock("1h"), new TextBlock("second") };
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host" }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SystemPromptContent = content }); content.Clear();
        Assert.Equal("first\nsecond", agent.SystemPrompt); Assert.Equal(3, agent.SystemPromptContent!.Count); Assert.Equal("1h", Assert.IsType<CachePointBlock>(agent.SystemPromptContent[1]).Ttl);
        await agent.StartAsync(); Assert.Equal("first\nsecond", model.Session!.SystemPrompt); await agent.StopAsync();
    }

    [Fact]
    public async Task SnapshotsRetainBlocksAndMutablePromptChangesRespectLegacyStringAndNullRestores()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host" }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.SetSystemPromptContent([new TextBlock("structured"), new CachePointBlock()]);
        var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.SystemPrompt });
        Assert.Equal(JsonValueKind.Array, snapshot.Data["systemPrompt"].ValueKind);
        var roundtrip = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(snapshot))!;
        agent.SetSystemPrompt("temporary"); await agent.LoadSnapshotAsync(roundtrip);
        Assert.Equal("structured", agent.SystemPrompt); Assert.IsType<CachePointBlock>(agent.SystemPromptContent![1]);
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement("legacy") }));
        Assert.Equal("legacy", Assert.IsType<TextBlock>(Assert.Single(agent.SystemPromptContent!)).Text);
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement<string?>(null) }));
        Assert.Null(agent.SystemPrompt); Assert.Null(agent.SystemPromptContent);
    }

    [Fact]
    public async Task ARealFileSessionPreservesCacheOnlyPromptWithoutFallingBackToTheHost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-structured-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "must not appear" }); await using var model = new ScriptedModel();
            await using (var source = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = new FileSessionStore(directory), SessionId = "structured", SystemPromptContent = [new CachePointBlock("5m")] })) { await source.SaveSessionAsync(); }
            var saved = await new FileSessionStore(directory).LoadAsync("structured"); Assert.Null(saved!.SystemPrompt); Assert.IsType<CachePointBlock>(Assert.Single(saved.SystemPromptContent!));
            await using var fresh = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = new FileSessionStore(directory), SessionId = "structured" }); await fresh.InitializeAsync();
            Assert.Null(fresh.SystemPrompt); Assert.Equal("5m", Assert.IsType<CachePointBlock>(Assert.Single(fresh.SystemPromptContent!)).Ttl);
            await fresh.StartAsync(); Assert.Null(model.Session!.SystemPrompt); await fresh.StopAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FileSessionRejectsAConflictingStructuredProjectionBeforeCreatingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-structured-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new SessionSnapshot(1, []) { HasSystemPrompt = true, SystemPrompt = "forged", SystemPromptContent = [new TextBlock("actual")] };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await new FileSessionStore(directory).SaveAsync("invalid", snapshot));
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
