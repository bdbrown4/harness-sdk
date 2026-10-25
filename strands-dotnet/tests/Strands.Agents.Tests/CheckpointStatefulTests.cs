using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointStatefulTests
{
    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, false)]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, true)]
    [InlineData(CheckpointBehavior.PythonReplay, false)]
    [InlineData(CheckpointBehavior.PythonReplay, true)]
    public async Task RestoredBoundariesRetainProviderStateWithoutReplayingConsumedInputOrAcceptedEffects(CheckpointBehavior behavior, bool stored)
    {
        var effects = 0;
        var requests = new List<ModelRequest>();
        var store = stored ? new JsonMemoryStore() : null;
        await using var first = Create(new StatefulModel(requests), behavior, () => effects++, store);
        var modelPause = await first.InvokeAsync("question");
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), modelPause.Checkpoint);
        Assert.Equal("state-1", first.ModelState.GetAll().TryGetValue("responseId", out var firstState) ? firstState.GetString() : null);
        Assert.Equal(0, effects);
        var firstSnapshot = await first.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        if (behavior == CheckpointBehavior.TypeScriptRegenerate)
        {
            Assert.Single(first.Messages);
            var receipt = firstSnapshot.Data["modelInputReceipt"];
            Assert.Equal(1, receipt.GetProperty("consumedMessageCount").GetInt32());
            Assert.Equal(first.Messages[0].TrackingId, receipt.GetProperty("lastConsumedTrackingId").GetString());
            Assert.Equal(modelPause.Message!.TrackingId, receipt.GetProperty("discardedResponseTrackingId").GetString());
        }
        else Assert.Equal(2, first.Messages.Count);
        await first.DisposeAsync();
        await using var second = Create(new StatefulModel(requests), behavior, () => effects++, store);
        if (!stored) await second.LoadSnapshotAsync(firstSnapshot);

        var toolPause = await second.InvokeAsync(null, new() { CheckpointResume = modelPause.Checkpoint });

        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), toolPause.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(behavior == CheckpointBehavior.TypeScriptRegenerate ? 2 : 1, requests.Count);
        if (behavior == CheckpointBehavior.TypeScriptRegenerate)
        {
            Assert.Empty(requests[1].Messages);
            Assert.Equal("state-1", requests[1].ModelState!["responseId"].GetString());
        }
        var accepted = Assert.Single(second.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(behavior == CheckpointBehavior.TypeScriptRegenerate ? "effect-2" : "effect-1", accepted.ToolUseId);
        var secondSnapshot = await second.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        await second.DisposeAsync();
        await using var third = Create(new StatefulModel(requests), behavior, () => effects++, store);
        if (!stored) await third.LoadSnapshotAsync(secondSnapshot);

        var completed = await third.InvokeAsync(null, new() { CheckpointResume = toolPause.Checkpoint });

        Assert.Equal(StopReasons.EndTurn, completed.StopReason);
        Assert.Equal("done", completed.Message!.Text);
        Assert.Null(completed.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(behavior == CheckpointBehavior.TypeScriptRegenerate ? 3 : 2, requests.Count);
        var pending = Assert.Single(requests.Last().Messages);
        Assert.Equal(Role.User, pending.Role);
        Assert.Equal(accepted.ToolUseId, Assert.IsType<ToolResultBlock>(Assert.Single(pending.Content)).ToolUseId);
        Assert.Equal("state-" + (requests.Count - 1), requests.Last().ModelState!["responseId"].GetString());
        Assert.Single(requests.SelectMany(request => request.Messages), message => message.Text == "question");
        Assert.Empty(third.Messages);
    }

    [Fact]
    public async Task CanceledCheckpointDoesNotAcknowledgeTheDiscardedProviderResponse()
    {
        using var caller = new CancellationTokenSource();
        var requests = new List<ModelRequest>();
        var effects = 0;
        await using var agent = Create(new StatefulModel(requests), CheckpointBehavior.TypeScriptRegenerate, () => effects++, null);
        using var hook = agent.Hooks.Register<AfterModelCallEvent>((_, _) => { caller.Cancel(); return default; });

        var result = await agent.InvokeAsync("question", cancellationToken: caller.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Empty(agent.ModelState.GetAll());
        var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.False(snapshot.Data.ContainsKey("modelInputReceipt"));
        Assert.Equal(0, effects);
        Assert.Single(requests);
    }

    [Fact]
    public async Task FailedProviderCleanupCannotCommitTheDiscardedResponseReceipt()
    {
        var failure = new IOException("stateful provider cleanup failed");
        var requests = new List<ModelRequest>();
        var effects = 0;
        await using var agent = Create(new StatefulModel(requests, failure), CheckpointBehavior.TypeScriptRegenerate, () => effects++, null);

        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(failure, actual);
        Assert.Empty(agent.ModelState.GetAll());
        var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.False(snapshot.Data.ContainsKey("modelInputReceipt"));
        Assert.Equal(0, effects);
        Assert.Single(requests);
    }

    private static Agent Create(IModel model, CheckpointBehavior behavior, Action effect, ISessionStore? store) => new(new()
    {
        Model = model,
        CheckpointBehavior = behavior,
        SessionStore = store,
        SessionId = store is null ? null : "session",
        Tools = [new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, token) => { token.ThrowIfCancellationRequested(); effect(); return new ValueTask<string>("accepted"); })]
    });

    private sealed class JsonMemoryStore : ISessionStore
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private byte[]? _bytes;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(_bytes is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(_bytes, Web)); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Web); return default; }
    }

    private sealed class StatefulModel(List<ModelRequest> requests, Exception? cleanupFailure = null) : IStatefulModel
    {
        public bool Stateful => true;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request with { Messages = request.Messages.ToArray(), ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()) });
            var previous = request.ModelState?.TryGetValue("responseId", out var state) == true ? int.Parse(state.GetString()!.Substring(6), System.Globalization.CultureInfo.InvariantCulture) : 0;
            var completed = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Any();
            try
            {
                yield return new ModelMessageStartEvent();
                if (completed)
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                }
                else yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("effect-" + (previous + 1), "effect", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(completed ? StopReasons.EndTurn : StopReasons.ToolUse)
                { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("state-" + (previous + 1)) } };
                await Task.CompletedTask;
            }
            finally { if (cleanupFailure is not null) throw cleanupFailure; }
        }
    }
}
