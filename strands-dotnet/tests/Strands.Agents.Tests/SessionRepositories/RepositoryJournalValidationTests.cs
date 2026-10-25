using System.Text.Json;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalValidationTests
{
    [Theory]
    [InlineData("excluded-kind")]
    [InlineData("null-messages")]
    [InlineData("agent-mismatch")]
    [InlineData("invalid-system-content")]
    [InlineData("native-version")]
    [InlineData("frontier-kind")]
    [InlineData("frontier-string")]
    [InlineData("frontier-missing")]
    [InlineData("excluded-frontier")]
    [InlineData("null-snapshot")]
    [InlineData("hidden-history")]
    [InlineData("frontier-absent-native")]
    public async Task MalformedNativeMetadataFailsBeforeAnyLiveFieldChanges(string kind)
    {
        var repository = new JournalProbeRepository();
        await repository.Inner.CreateSessionAsync(new("session"));
        var snapshot = new SessionSnapshot(1, []);
        object? marker = new { lastMessageId = (long?)null, excludedMessageIds = Array.Empty<long>() };
        if (kind == "null-messages") snapshot = snapshot with { Messages = null! };
        if (kind == "agent-mismatch") snapshot = snapshot with { AgentId = "another-agent" };
        if (kind == "invalid-system-content") snapshot = snapshot with { HasSystemPrompt = true, SystemPrompt = "changed", SystemPromptContent = [new TextBlock(null!)] };
        if (kind == "hidden-history") snapshot = snapshot with { Messages = [Message.User("hidden")] };
        if (kind == "frontier-kind") marker = true;
        if (kind == "frontier-string") marker = new { lastMessageId = "0", excludedMessageIds = Array.Empty<long>() };
        if (kind == "frontier-missing") marker = new { lastMessageId = 2, excludedMessageIds = Array.Empty<long>() };
        if (kind == "excluded-kind") marker = new { lastMessageId = 0, excludedMessageIds = new[] { "bad" } };
        if (kind == "excluded-frontier") marker = new { lastMessageId = 0, excludedMessageIds = new[] { 0 } };
        var metadata = kind == "frontier-absent-native"
            ? JsonSerializer.SerializeToElement(new { nativeSessionVersion = 1, snapshot }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : JsonSerializer.SerializeToElement(new { nativeSessionVersion = kind == "native-version" ? 2 : 1, snapshot = kind == "null-snapshot" ? null : snapshot, journalFrontier = marker }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await repository.Inner.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { stored = "retained" }), JsonSerializer.SerializeToElement(new { }), metadata));
        await repository.Inner.CreateMessageAsync("session", "agent", new(Message.User("stored"), 0));
        var plugin = new StateReadingPlugin();
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), SystemPrompt = "configured", State = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) }, Messages = [Message.User("configured")], Plugins = [new RepositorySessionManager("session", repository), plugin] });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.True(agent.State.Get("configured")?.GetBoolean());
        Assert.Null(agent.State.Get("stored"));
        Assert.Null(plugin.Observed);
        Assert.Equal("configured", agent.SystemPrompt);
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.DoesNotContain("update-agent", repository.Operations);
    }

    [Fact]
    public async Task SharedSessionInitializationCallbacksCannotAwaitTheirOwnManagerGate()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        var entered = false;
        repository.Before = async (operation, _) =>
        {
            if (operation != "read-session" || entered) return;
            entered = true;
            await using var nested = new Agent(new() { Id = "nested", Model = new JournalTestModel(), Plugins = [manager] });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await nested.InitializeAsync(deadline.Token);
        };
        await using var outer = new Agent(new() { Id = "outer", Model = new JournalTestModel(), Plugins = [manager] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => outer.InitializeAsync());
    }
}
