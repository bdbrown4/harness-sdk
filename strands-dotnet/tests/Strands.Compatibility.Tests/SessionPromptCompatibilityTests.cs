using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class SessionPromptCompatibilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("persisted instructions")]
    public async Task FileBackedAgentRestoresExplicitPromptInsteadOfNewConfiguration(string? prompt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            await using (var source = new Agent(new() { Model = new Model(), SystemPrompt = prompt, SessionStore = store, SessionId = "conversation" }))
                await source.InvokeAsync("question");
            var snapshot = await store.LoadAsync("conversation");
            Assert.True(snapshot!.HasSystemPrompt);
            Assert.Equal(prompt, snapshot.SystemPrompt);
            await using var restored = new Agent(new() { Model = new Model(), SystemPrompt = "new configuration", SessionStore = store, SessionId = "conversation" });
            await restored.InitializeAsync();
            Assert.Equal(prompt, restored.SystemPrompt);
            Assert.Equal(2, restored.Messages.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task LegacyFileWithoutPromptMarkerPreservesConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            await store.SaveAsync("conversation", new(1, []));
            var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json.Remove("hasSystemPrompt");
            json.Remove("systemPrompt");
            File.WriteAllText(path, json.ToJsonString());
            await using var restored = new Agent(new() { Model = new Model(), SystemPrompt = "configured", SessionStore = store, SessionId = "conversation" });
            await restored.InitializeAsync();
            Assert.Equal("configured", restored.SystemPrompt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
