using System.Reflection;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiSystemPromptGuardBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BidiRejectsOrdinarySystemGuardsBeforeConnectingOrReplacingItsPrompt(bool image, bool setter)
    {
        ContentBlock guard = image ? new GuardContentBlock(new ImageBlock("image/png", "AAEC")) : new GuardContentBlock("selected", qualifiers: [GuardQualifier.Query]);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SystemPrompt = "accepted", Messages = [Message.User("old")] });
        if (setter) Assert.Throws<ArgumentException>(() => agent.SetSystemPromptContent([guard]));
        else Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, SystemPromptContent = [guard] }));
        Assert.Equal("accepted", agent.SystemPrompt);
        Assert.Equal("old", Assert.Single(agent.Messages).Text);
        Assert.Equal(0, model.Starts);
    }

    [Fact]
    public async Task NeutralFileAcceptsOrdinaryGuardButBidiRejectsBeforeInitializationCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bidi-guard-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            await store.SaveAsync("ordinary", new(1, [Message.User("replacement")])
            {
                HasSystemPrompt = true,
                SystemPrompt = "plain",
                SystemPromptContent = [new TextBlock("plain"), new GuardContentBlock("selected")]
            });
            Assert.IsType<GuardContentBlock>((await store.LoadAsync("ordinary"))!.SystemPromptContent![1]);
            await using var host = new Agent(new() { Model = new NoCallsModel() });
            await using var model = new ScriptedModel();
            await using var agent = new BidiAgent(new()
            {
                Model = model,
                ToolAgent = host,
                SessionStore = store,
                SessionId = "ordinary",
                SystemPrompt = "accepted",
                Messages = [Message.User("old")]
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
            Assert.Equal("accepted", agent.SystemPrompt);
            Assert.Equal("old", Assert.Single(agent.Messages).Text);
            Assert.Equal(0, model.Starts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BidiSnapshotGuardRejectsBeforeReplacingStateOrHistory()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SystemPrompt = "accepted", Messages = [Message.User("old")] });
        agent.State.Set("accepted", true);
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["systemPrompt"] = JsonSerializer.SerializeToElement<ContentBlock[]>([new GuardContentBlock("selected")], new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") }),
            ["state"] = JsonSerializer.SerializeToElement(new { accepted = false })
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(snapshot));
        Assert.Equal("accepted", agent.SystemPrompt);
        Assert.Equal("old", Assert.Single(agent.Messages).Text);
        Assert.True(agent.State.Get<bool>("accepted"));
        Assert.Equal(0, model.Starts);
    }

    [Fact]
    public void OriginalInternalHelperSignaturesStillExistAndRemainNarrow()
    {
        var copy = typeof(SessionSnapshot).GetMethod("CopySystemPromptContent", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(IReadOnlyList<ContentBlock>)], null);
        var validate = typeof(SessionSnapshot).GetMethod("CopyValidatedSystemPromptContent", BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        Assert.NotNull(copy);
        Assert.NotNull(validate);
        ContentBlock[] guarded = [new GuardContentBlock("selected")];
        var copyError = Assert.Throws<TargetInvocationException>(() => copy.Invoke(null, [guarded]));
        Assert.IsType<InvalidDataException>(copyError.InnerException);
        var snapshot = new SessionSnapshot(1, []) { HasSystemPrompt = true, SystemPromptContent = guarded };
        var validateError = Assert.Throws<TargetInvocationException>(() => validate.Invoke(snapshot, null));
        Assert.IsType<InvalidDataException>(validateError.InnerException);
    }

    [Fact]
    public async Task TextCacheOnlyBidiSessionStillConnectsWithMatchingPlainProjection()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new()
        {
            Model = model,
            ToolAgent = host,
            SystemPromptContent = [new TextBlock("first"), new CachePointBlock("caller"), new TextBlock("last")]
        });
        await agent.StartAsync();
        Assert.Equal("first\nlast", model.Session!.SystemPrompt);
        Assert.Equal(1, model.Starts);
        await agent.StopAsync();
    }
}
