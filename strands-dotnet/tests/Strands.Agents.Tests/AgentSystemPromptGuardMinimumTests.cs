using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentSystemPromptGuardMinimumTests
{
    [Fact]
    public async Task OrdinarySessionRestoresFullGuardPromptBeforeFakeModelCall()
    {
        var prompt = new ContentBlock[]
        {
            new TextBlock("instructions"),
            new GuardContentBlock("selected nine", qualifiers: [GuardQualifier.Query, GuardQualifier.GroundingSource]),
            new CachePointBlock("caller")
        };
        var request = await RoundTripAsync(prompt);
        Assert.Equal("instructions", request.SystemPrompt);
        Assert.Collection(request.SystemPromptContent!,
            block => Assert.Equal("instructions", Assert.IsType<TextBlock>(block).Text),
            block =>
            {
                var guard = Assert.IsType<GuardContentBlock>(block);
                Assert.Equal("selected nine", guard.Text);
                Assert.Equal(new[] { GuardQualifier.Query, GuardQualifier.GroundingSource }, guard.Qualifiers);
                Assert.Null(guard.Image);
            },
            block => Assert.Equal("caller", Assert.IsType<CachePointBlock>(block).Ttl));
    }

    [Fact]
    public async Task FallbackCountsGuardOnlySystemTextWithoutFlatteningIt()
    {
        var model = new RecordingModel();
        var request = new ModelRequest([]) { SystemPromptContent = [new GuardContentBlock("abcdefghi", qualifiers: [GuardQualifier.Query])] };
        Assert.Null(request.SystemPrompt);
        Assert.Equal(3, await model.CountTokensAsync(request));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task FallbackCountsExistingMessageGuardText()
    {
        var model = new RecordingModel();
        var request = new ModelRequest([new Message(Role.User, [new GuardContentBlock("abcdefghi", qualifiers: [GuardQualifier.Query])])]);
        Assert.Equal(3, await model.CountTokensAsync(request));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task TypedTextCacheAgentRequestAndSessionRemainControls()
    {
        var request = await RoundTripAsync([new TextBlock("first"), new CachePointBlock("caller"), new TextBlock("second")]);
        Assert.Equal("first\nsecond", request.SystemPrompt);
        Assert.Collection(request.SystemPromptContent!,
            block => Assert.Equal("first", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal("caller", Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("second", Assert.IsType<TextBlock>(block).Text));
    }

    private static async Task<ModelRequest> RoundTripAsync(IReadOnlyList<ContentBlock> prompt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "b07-system-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var source = new Agent(new()
            {
                Model = new RecordingModel(),
                SystemPromptContent = prompt,
                SessionStore = new FileSessionStore(directory),
                SessionId = "guard-session"
            }))
                await source.InvokeAsync("first");

            var store = new FileSessionStore(directory);
            var stored = Assert.IsType<SessionSnapshot>(await store.LoadAsync("guard-session"));
            Assert.True(stored.HasSystemPrompt);
            Assert.Equal(prompt.Count, stored.SystemPromptContent!.Count);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var detached = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(stored, options), options)!;
            await store.SaveAsync("guard-session", detached);

            var model = new RecordingModel();
            await using var fresh = new Agent(new()
            {
                Model = model,
                SystemPrompt = "fallback",
                SessionStore = new FileSessionStore(directory),
                SessionId = "guard-session"
            });
            await fresh.InvokeAsync("second");
            Assert.Equal(prompt.Count, fresh.SystemPromptContent!.Count);
            return Assert.Single(model.Requests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class RecordingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
