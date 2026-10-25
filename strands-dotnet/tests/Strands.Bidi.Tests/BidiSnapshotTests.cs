using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiSnapshotTests
{
    [Fact]
    public async Task SessionPresetCapturesMessagesAndStateAndJsonRoundTripRestoresSelectedPrompt()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host" });
        await using var model = new ScriptedModel();
        await using var source = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = [Message.User("hello")], State = new Dictionary<string, JsonElement> { ["tenant"] = JsonSerializer.SerializeToElement("one") } });
        var preset = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal(new[] { "messages", "state" }, preset.Data.Keys.OrderBy(key => key));
        var snapshot = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, Include = SnapshotFields.SystemPrompt });
        var restored = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(snapshot))!;
        await using var target = new BidiAgent(new() { Model = model, ToolAgent = host }); await target.LoadSnapshotAsync(restored);
        Assert.Equal("hello", Assert.Single(target.Messages).Text); Assert.Equal("one", target.State.Get<string>("tenant")); Assert.Equal("host", target.SystemPrompt);
        source.State.Set("tenant", "changed"); Assert.Equal("one", target.State.Get<string>("tenant"));
        await target.StartAsync(); Assert.Equal("hello", Assert.Single(model.Session!.Messages).Text); await target.StopAsync();
    }

    [Fact]
    public async Task PartialSnapshotExplicitNullClearsPromptAndUnknownFieldsAndAppDataDoNotApply()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host" }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = [Message.User("keep")] }); agent.State.Set("keep", 3);
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement<string?>(null), ["modelState"] = JsonSerializer.SerializeToElement(new { opaque = true }) }, new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(new { overwrite = true }) }));
        Assert.Null(agent.SystemPrompt); Assert.Equal("keep", Assert.Single(agent.Messages).Text); Assert.Equal(3, agent.State.Get<int>("keep"));
        await agent.StartAsync(); Assert.Null(model.Session!.SystemPrompt); await agent.StopAsync();
    }

    [Theory]
    [InlineData(SnapshotFields.ModelState, false)]
    [InlineData(SnapshotFields.ModelState, true)]
    [InlineData(SnapshotFields.InterruptState, false)]
    [InlineData(SnapshotFields.InterruptState, true)]
    public async Task UnsupportedFieldSelectionFailsExplicitly(SnapshotFields field, bool exclude)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, Include = exclude ? 0 : field, Exclude = exclude ? field : 0 }));
    }

    [Fact]
    public async Task MalformedFieldsAreValidatedBeforeAnyHistoryStateOrPromptMutation()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "old" }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = [Message.User("old")] }); agent.State.Set("old", true);
        var data = new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("new") }), ["state"] = JsonSerializer.SerializeToElement(new { changed = true }), ["systemPrompt"] = JsonSerializer.SerializeToElement(123) };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(new(data)));
        Assert.Equal("old", Assert.Single(agent.Messages).Text); Assert.True(agent.State.Get<bool>("old")); Assert.Equal("old", agent.SystemPrompt);
    }

    [Fact]
    public async Task ActiveCaptureIsAllowedButRestoreRequiresAStoppedConversation()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = [Message.User("old")] });
        await agent.StartAsync();
        var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.LoadSnapshotAsync(snapshot)); await agent.StopAsync();
        await agent.LoadSnapshotAsync(snapshot); Assert.Equal("old", Assert.Single(agent.Messages).Text);
    }
}
