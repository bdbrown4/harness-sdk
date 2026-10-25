using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Goals;
using Strands.Agents.Middleware;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Skills;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class AgentSystemPromptGuardConformanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("")]
    [InlineData("雪😀e\u0301\nline")]
    public async Task GuardOnlyContentRetainsTextAndHasNoPlainProjection(string text)
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = [new GuardContentBlock(text)] });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        Assert.Null(request.SystemPrompt);
        Assert.Equal(text, Assert.IsType<GuardContentBlock>(Assert.Single(request.SystemPromptContent!)).Text);
        Assert.Null(agent.SystemPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionsAndRequestDetachMembershipAndQualifierOrderBeforeAnyModelAwait(bool clone)
    {
        var qualifiers = new List<GuardQualifier> { GuardQualifier.Query, GuardQualifier.Query, GuardQualifier.GroundingSource };
        var supplied = new List<ContentBlock> { new GuardContentBlock("private", qualifiers: qualifiers), new TextBlock("plain") };
        var model = new Responses();
        var original = new AgentOptions { Model = model, SystemPromptContent = supplied };
        var selected = clone ? original with { Name = "copy" } : original;
        var request = new ModelRequest([]) { SystemPromptContent = supplied };
        supplied.Clear();
        qualifiers.Clear();
        Assert.Throws<NotSupportedException>(() => ((IList<ContentBlock>)selected.SystemPromptContent!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GuardQualifier>)Assert.IsType<GuardContentBlock>(request.SystemPromptContent![0]).Qualifiers).Clear());
        await using var agent = new Agent(selected);
        await agent.InvokeAsync("question");
        foreach (var content in new[] { request.SystemPromptContent, Assert.Single(model.Requests).SystemPromptContent })
        {
            Assert.Equal(2, content!.Count);
            Assert.Equal(new[] { GuardQualifier.Query, GuardQualifier.Query, GuardQualifier.GroundingSource }, Assert.IsType<GuardContentBlock>(content[0]).Qualifiers);
        }
        Assert.Equal("plain", request.SystemPrompt);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("first\nlast")]
    [InlineData(null)]
    public async Task MiddlewareStringAssignmentReplacesTheWholeGuardUnion(string? replacement)
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel,
            (context, _) => new ValueTask<InvokeModelContext>(context with { SystemPrompt = replacement }));
        await agent.InvokeAsync("question");
        Assert.Null(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal(replacement, model.Requests[0].SystemPrompt);
        AssertPrompt(agent.SystemPromptContent);
    }

    [Fact]
    public async Task MiddlewareTypedReplacementCopiesBeforeReturningAndDoesNotMutateAgent()
    {
        var model = new Responses();
        var replacement = Prompt();
        await using var agent = new Agent(new() { Model = model, SystemPrompt = "configured" });
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) =>
        {
            var accepted = context with { SystemPromptContent = replacement };
            replacement.Clear();
            return new ValueTask<InvokeModelContext>(accepted);
        });
        await agent.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal("configured", agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeModelHookRetainsGuardAuthorityUnlessItExplicitlyClearsTypedContent(bool clear)
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            value.Request = clear ? value.Request with { SystemPromptContent = null, SystemPrompt = "hook" }
                : value.Request with { SystemPrompt = "hook" };
            return default;
        });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        if (clear) { Assert.Null(request.SystemPromptContent); Assert.Equal("hook", request.SystemPrompt); }
        else { AssertPrompt(request.SystemPromptContent); Assert.Equal("first\nlast", request.SystemPrompt); }
    }

    [Fact]
    public async Task CurrentRestoredGuardPromptIsInheritedByTheDefaultChild()
    {
        var model = new Responses();
        await using var parent = new Agent(new() { Model = model, SystemPrompt = "constructed" });
        await parent.LoadSnapshotAsync(PromptSnapshot());
        await using var child = parent.CreateChild();
        await child.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        AssertPrompt(parent.SystemPromptContent);
        AssertPrompt(child.SystemPromptContent);
    }

    [Fact]
    public async Task SharedSkillsAppendXmlWithoutFlatteningOrMutatingGuardContent()
    {
        var skills = new AgentSkills(new() { Skills = [] });
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt(), Plugins = [skills] });
        await agent.InvokeAsync("first");
        await agent.InvokeAsync("second");
        foreach (var request in model.Requests)
        {
            AssertPrompt(request.SystemPromptContent!.Take(4).ToArray());
            Assert.Equal(5, request.SystemPromptContent!.Count);
            Assert.Contains("<available_skills>", Assert.IsType<TextBlock>(request.SystemPromptContent[4]).Text);
            Assert.StartsWith("first\nlast\n<available_skills>", request.SystemPrompt);
        }
        AssertPrompt(agent.SystemPromptContent);
    }

    [Fact]
    public async Task GoalResetRestoresGuardMembershipAcrossAttempts()
    {
        var attempts = 0;
        var goal = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(++attempts == 2)),
            new() { PreserveContext = false, MaxAttempts = 2 });
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt(), Plugins = [goal] });
        await agent.InvokeAsync("question");
        Assert.Equal(2, attempts);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, value => AssertPrompt(value.SystemPromptContent));
        AssertPrompt(agent.SystemPromptContent);
    }

    [Fact]
    public async Task AgentToolResetRestoresCapturedGuardInsteadOfLaterStringPrompt()
    {
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await using var parent = new Agent(new() { Model = new Responses() });
        var tool = agent.AsTool(new AgentToolOptions());
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement("later") }));
        var input = JsonSerializer.SerializeToElement(new { input = "question" });
        Assert.Equal("answer", await tool.InvokeAsync(input, new(parent, new("nested", tool.Spec.Name, input), new Dictionary<string, object?>())));
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        AssertPrompt(agent.SystemPromptContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedResumeOrAbortPersistsTheGuardUnionWithoutReplayingTheBody(bool abort)
    {
        var model = new ToolResponses();
        var store = new Store();
        var effects = 0;
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            context.Interrupt("approval");
            effects++;
            return new ValueTask<string>("approved");
        });
        await using var agent = new Agent(new()
        {
            Model = model,
            SystemPromptContent = Prompt(),
            Tools = [tool],
            SessionStore = store,
            SessionId = "paused-guard"
        });
        var paused = await agent.InvokeAsync("question");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        Assert.Equal(0, effects);
        AssertPrompt(store.Snapshot!.SystemPromptContent);
        if (abort)
        {
            Assert.True(await agent.AbortInterruptsAsync("canceled"));
            Assert.Equal(0, effects);
            Assert.Single(model.Requests);
            Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages.Last().Content)).Status);
        }
        else
        {
            var result = await agent.InvokeAsync(null, new()
            {
                InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]
            });
            Assert.Equal("answer", result.ToString());
            Assert.Equal(1, effects);
            Assert.Equal(2, model.Requests.Count);
        }
        Assert.Null(store.Snapshot!.InterruptState);
        AssertPrompt(store.Snapshot.SystemPromptContent);
        Assert.All(model.Requests, value => AssertPrompt(value.SystemPromptContent));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("repository")]
    public async Task FreshStorageHostRestoresGuardMetadataBeforeItsFirstModelCall(string kind)
    {
        var storage = new InMemoryStorage();
        IPlugin writer = kind == "catalog" ? new SnapshotSessionManager(storage, "guard-catalog")
            : new RepositorySessionManager("guard-repository", new StorageSessionRepository(storage));
        await using (var source = new Agent(new() { Id = "stable", Model = new Responses(), SystemPromptContent = Prompt(), Plugins = [writer] }))
        {
            if (writer is SnapshotSessionManager catalog) Assert.Null(await catalog.SaveSnapshotAsync(source, true));
            else await source.InvokeAsync("saved");
        }
        IPlugin reader = kind == "catalog" ? new SnapshotSessionManager(storage, "guard-catalog")
            : new RepositorySessionManager("guard-repository", new StorageSessionRepository(storage));
        var model = new Responses();
        await using var fresh = new Agent(new() { Id = "stable", Model = model, SystemPrompt = "fallback", Plugins = [reader] });
        if (reader is SnapshotSessionManager manager) Assert.True(await manager.RestoreSnapshotAsync(fresh));
        else await fresh.InitializeAsync();
        Assert.Empty(model.Requests);
        AssertPrompt(fresh.SystemPromptContent);
        await fresh.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
    }

    [Theory]
    [InlineData("[{\"type\":\"guardContent\",\"text\":\"x\",\"qualifiers\":[\"invalid\"]}]")]
    [InlineData("[{\"type\":\"unknown-guard\",\"text\":\"x\"}]")]
    public async Task InvalidGuardSnapshotRejectsBeforeInitializationAndPreservesAcceptedState(string json)
    {
        var probe = new Probe();
        var model = new Responses();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt(), Plugins = [probe], Messages = [Message.User("old")] });
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["systemPrompt"] = JsonSerializer.Deserialize<JsonElement>(json),
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") })
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(snapshot).AsTask());
        Assert.Equal(0, probe.Initializations);
        Assert.Empty(model.Requests);
        Assert.Equal("old", Assert.Single(agent.Messages).Text);
        AssertPrompt(agent.SystemPromptContent);
    }

    [Fact]
    public async Task FileStoreRejectsConflictingGuardProjectionBeforeCreatingStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "guard-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new SessionSnapshot(1, []) { HasSystemPrompt = true, SystemPrompt = "forged", SystemPromptContent = Prompt() };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await new FileSessionStore(directory).SaveAsync("id", snapshot));
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("", 0L)]
    [InlineData("a", 1L)]
    [InlineData("abcd", 1L)]
    [InlineData("abcde", 2L)]
    [InlineData("😀雪e\u0301", 2L)]
    public async Task FallbackCountsGuardTextPerExistingBlockRuleAndImagesAsZero(string text, long expected)
    {
        var model = new Responses();
        var system = new ModelRequest([]) { SystemPromptContent = [new GuardContentBlock(text), new GuardContentBlock(new ImageBlock("image/jpeg", "AQID")), new CachePointBlock()] };
        var message = new ModelRequest([new(Role.User, [new GuardContentBlock(text), new GuardContentBlock(new ImageBlock("image/png", "AQID"))])]);
        Assert.Equal(expected, await model.CountTokensAsync(system));
        Assert.Equal(expected, await model.CountTokensAsync(message));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task FallbackCountsMixedGuardAndPlainTextOnceWithoutChangingThePlainProjection()
    {
        var model = new Responses();
        var request = new ModelRequest([]) { SystemPromptContent = Prompt() };
        Assert.Equal("first\nlast", request.SystemPrompt);
        Assert.Equal(6, await model.CountTokensAsync(request));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task PreCanceledFallbackPreservesCallerTokenAndDoesNoModelWork()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var model = new Responses();
        var request = new ModelRequest([]) { SystemPromptContent = Prompt() };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(request, cancel.Token).AsTask());
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public void LegacyModelRequestConstructorDeconstructionAndDefaultJsonShapeRemainUnchanged()
    {
        var request = new ModelRequest([], "plain", null);
        var (messages, plain, tools) = request;
        Assert.Empty(messages);
        Assert.Equal("plain", plain);
        Assert.Null(tools);
        Assert.False(JsonSerializer.SerializeToElement(request, Json).TryGetProperty("systemPromptContent", out _));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new Responses(), SystemPrompt = "plain", SystemPromptContent = Prompt() }));
    }

    private static List<ContentBlock> Prompt() => [new TextBlock("first"), new GuardContentBlock("abcdefghi", qualifiers: [GuardQualifier.Query, GuardQualifier.GroundingSource]), new CachePointBlock("caller"), new TextBlock("last")];
    private static AgentSnapshot PromptSnapshot() => new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement(Prompt(), Json) });
    private static void AssertPrompt(IReadOnlyList<ContentBlock>? content)
    {
        Assert.NotNull(content);
        Assert.Collection(content,
            block => Assert.Equal("first", Assert.IsType<TextBlock>(block).Text),
            block => { var guard = Assert.IsType<GuardContentBlock>(block); Assert.Equal("abcdefghi", guard.Text); Assert.Equal(new[] { GuardQualifier.Query, GuardQualifier.GroundingSource }, guard.Qualifiers); },
            block => Assert.Equal("caller", Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("last", Assert.IsType<TextBlock>(block).Text));
    }
    private sealed class Responses : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
    private sealed class Probe : IPlugin
    {
        internal int Initializations;
        public string Name => "guard-initialization";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Snapshot);
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return default;
        }
    }
    private sealed class ToolResponses : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = Requests.Count == 0;
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return first ? new ModelContentBlockStartEvent(0, new("approval-call", "approve", JsonSerializer.SerializeToElement(new { })))
                : new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("answer") };
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
