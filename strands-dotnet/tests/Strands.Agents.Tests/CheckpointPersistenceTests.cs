using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointPersistenceTests
{
    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, false)]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, true)]
    [InlineData(CheckpointBehavior.PythonReplay, false)]
    [InlineData(CheckpointBehavior.PythonReplay, true)]
    public async Task ASeparateMarkerAndRestoredConversationResumeTheSelectedWorkflow(CheckpointBehavior behavior, bool stored)
    {
        var effects = 0;
        var store = stored ? new JsonMemoryStore() : null;
        var firstModel = new BoundaryModel(() => effects);
        await using var source = Create(firstModel, behavior, () => effects++, store);
        var paused = await source.InvokeAsync("question");
        var snapshot = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.False(snapshot.Data.ContainsKey("checkpoint"));
        var marker = JsonSerializer.Deserialize<AgentCheckpoint>(JsonSerializer.Serialize(paused.Checkpoint));
        await source.DisposeAsync();
        var nextModel = new BoundaryModel(() => effects);
        await using var restored = Create(nextModel, behavior, () => effects++, store);
        if (!stored) await restored.LoadSnapshotAsync(snapshot);

        var result = await restored.InvokeAsync(null, new() { CheckpointResume = marker });

        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), result.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(behavior == CheckpointBehavior.TypeScriptRegenerate ? 1 : 0, nextModel.Requests);
        Assert.Single(restored.Messages, message => message.Text == "question");
        Assert.Equal(ToolResultStatus.Success, Assert.Single(restored.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task AfterToolsSessionResumptionDoesNotRepeatTheAcceptedEffect(CheckpointBehavior behavior)
    {
        var effects = 0;
        var store = new JsonMemoryStore();
        await using var source = Create(new BoundaryModel(() => effects), behavior, () => effects++, store);
        var modelBoundary = await source.InvokeAsync("question");
        var toolBoundary = await source.InvokeAsync(null, new() { CheckpointResume = modelBoundary.Checkpoint });
        Assert.Equal(1, effects);
        await source.DisposeAsync();
        var nextModel = new BoundaryModel(() => effects);
        await using var restored = Create(nextModel, behavior, () => effects++, store);

        var result = await restored.InvokeAsync(null, new() { CheckpointResume = toolBoundary.Checkpoint });

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal("done", result.Message!.Text);
        Assert.Equal(1, effects);
        Assert.Equal(1, nextModel.Requests);
        Assert.Single(restored.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Single(restored.Messages, message => message.Text == "question");
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task AFailingFinishHookCannotLeaveAResumablePendingToolRequest(CheckpointBehavior behavior)
    {
        var effects = 0;
        var store = new JsonMemoryStore();
        var failure = new IOException("finish hook failed");
        await using var agent = Create(new BoundaryModel(() => effects), behavior, () => effects++, store);
        using var hook = agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
            value.Result?.Checkpoint is not null ? new ValueTask(Task.FromException(failure)) : default);

        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(failure, actual);
        Assert.Equal(0, effects);
        var persisted = Assert.IsType<SessionSnapshot>(await store.LoadAsync("session"));
        Assert.Equal(Role.User, persisted.Messages.Last().Role);
        if (behavior == CheckpointBehavior.PythonReplay)
        {
            var repair = Assert.Single(persisted.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Equal(ToolResultStatus.Error, repair.Status);
            Assert.Equal("effect-1", repair.ToolUseId);
        }
        else Assert.Empty(persisted.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task TypedInvocationExposesItsPauseWithoutFabricatingAValidatedValue(CheckpointBehavior behavior)
    {
        var conversions = 0;
        var model = new BoundaryModel(() => conversions, structured: true);
        await using var agent = new Agent(new() { Model = model, CheckpointBehavior = behavior });
        var state = new Dictionary<string, object?> { ["caller"] = new object() };
        var options = new StructuredOutputOptions<JsonElement>
        {
            Schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { answer = new { type = "integer" } },
                required = new[] { "answer" }
            }),
            Deserializer = json => { conversions++; return json.Clone(); }
        };

        var paused = await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("question", options,
            new() { InvocationState = state }));

        Assert.NotNull(paused.Result);
        Assert.Same(state, paused.Result.InvocationState);
        Assert.Equal(StopReasons.Checkpoint, paused.Result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), paused.Result.Checkpoint);
        Assert.Equal(0, conversions);
        var completed = await agent.InvokeStructuredAsync(null, options,
            new() { CheckpointResume = paused.Result.Checkpoint, InvocationState = state });
        Assert.Equal(7, completed.Output.GetProperty("answer").GetInt32());
        Assert.Equal(StopReasons.ToolUse, completed.Result.StopReason);
        Assert.Null(completed.Result.Checkpoint);
        Assert.Same(state, completed.Result.InvocationState);
        Assert.Equal(1, conversions);
    }

    private static Agent Create(IModel model, CheckpointBehavior behavior, Action effect, ISessionStore? store)
    {
        var tool = new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                effect();
                return new ValueTask<string>("accepted");
            });
        return new(new() { Model = model, Tools = [tool], CheckpointBehavior = behavior, SessionStore = store, SessionId = store is null ? null : "session" });
    }

    private sealed class JsonMemoryStore : ISessionStore
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private byte[]? _bytes;

        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("session", sessionId);
            cancellationToken.ThrowIfCancellationRequested();
            return new(_bytes is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(_bytes, Json));
        }

        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Assert.Equal("session", sessionId);
            cancellationToken.ThrowIfCancellationRequested();
            _bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            return default;
        }
    }

    private sealed class BoundaryModel(Func<int> effects, bool structured = false) : IModel
    {
        internal int Requests { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var needsTools = effects() == 0;
            yield return new ModelMessageStartEvent();
            if (needsTools)
            {
                var name = structured ? StructuredOutputTool<JsonElement>.ToolName : "effect";
                var input = structured ? JsonSerializer.SerializeToElement(new { answer = 7 }) : JsonSerializer.SerializeToElement(new { });
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("effect-1", name, input));
                yield return new ModelContentBlockStopEvent(0);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(needsTools ? StopReasons.ToolUse : StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
