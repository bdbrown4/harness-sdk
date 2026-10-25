using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.Skills;

namespace Strands.Agents.Tests;

public sealed class AgentSystemPromptTests
{
    [Fact]
    public async Task OrdinaryAgentForwardsTypedOrderAndCachepoint()
    {
        var model = new RecordingModel();
        var supplied = Prompt();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = supplied });
        supplied.Clear();
        await agent.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal("first 雪\nsecond", model.Requests[0].SystemPrompt);
        AssertPrompt(agent.SystemPromptContent);
        Assert.Equal("first 雪\nsecond", agent.SystemPrompt);
    }

    [Fact]
    public async Task DefaultChildInheritsCurrentTypedRepresentation()
    {
        var model = new RecordingModel();
        await using var parent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await using var child = parent.CreateChild();
        await child.InvokeAsync("child question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        AssertPrompt(child.SystemPromptContent);
        Assert.Equal("first 雪\nsecond", child.SystemPrompt);
    }

    [Fact]
    public async Task MiddlewarePassthroughKeepsTypedRepresentation()
    {
        var model = new RecordingModel();
        InvokeModelContext? observed = null;
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) =>
        {
            observed = context;
            return new ValueTask<InvokeModelContext>(context);
        });
        await agent.InvokeAsync("question");
        Assert.NotNull(observed);
        AssertPrompt(observed.SystemPromptContent);
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal("first 雪\nsecond", observed.SystemPrompt);
    }

    [Fact]
    public async Task SaveAndFreshSessionRestoreKeepsTypedPrompt()
    {
        var store = new MemorySessionStore();
        await using (var original = new Agent(new() { Model = new RecordingModel(), SystemPromptContent = Prompt(), SessionStore = store, SessionId = "typed-session" }))
            await original.InvokeAsync("first question");
        var saved = Assert.IsType<SessionSnapshot>(store.Snapshot);
        var freshModel = new RecordingModel();
        await using var fresh = new Agent(new() { Model = freshModel, SystemPrompt = "fallback", SessionStore = store, SessionId = "typed-session" });
        await fresh.InvokeAsync("second question");
        Assert.True(saved.HasSystemPrompt);
        AssertPrompt(saved.SystemPromptContent);
        Assert.Equal("first 雪\nsecond", saved.SystemPrompt);
        AssertPrompt(Assert.Single(freshModel.Requests).SystemPromptContent);
        AssertPrompt(fresh.SystemPromptContent);
    }

    [Fact]
    public async Task InMemorySnapshotCarriesTypedUnionAndRestores()
    {
        await using var original = new Agent(new() { Model = new RecordingModel(), SystemPromptContent = Prompt() });
        var snapshot = await original.TakeSnapshotAsync(new() { Include = SnapshotFields.SystemPrompt });
        var freshModel = new RecordingModel();
        await using var fresh = new Agent(new() { Model = freshModel, SystemPrompt = "fallback" });
        await fresh.LoadSnapshotAsync(snapshot);
        await fresh.InvokeAsync("question");
        Assert.Equal(JsonValueKind.Array, snapshot.Data["systemPrompt"].ValueKind);
        AssertPrompt(Assert.Single(freshModel.Requests).SystemPromptContent);
        AssertPrompt(fresh.SystemPromptContent);
    }

    [Fact]
    public async Task SkillsAppendsTypedXmlWithoutDroppingCallerPoint()
    {
        var model = new RecordingModel();
        var supplied = Prompt();
        var skills = new AgentSkills(new() { Skills = [] });
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = supplied, Plugins = [skills] });
        await agent.InvokeAsync("first question");
        await agent.InvokeAsync("second question");
        Assert.Equal(2, model.Requests.Count);
        foreach (var request in model.Requests)
        {
            Assert.NotNull(request.SystemPromptContent);
            Assert.Equal(4, request.SystemPromptContent.Count);
            AssertPrompt(request.SystemPromptContent.Take(3).ToArray());
            var xml = Assert.IsType<TextBlock>(request.SystemPromptContent[3]).Text;
            Assert.Contains("<available_skills>", xml);
            Assert.Equal("first 雪\nsecond\n" + xml, request.SystemPrompt);
        }
        AssertPrompt(agent.SystemPromptContent);
        AssertPrompt(supplied);
    }

    [Fact]
    public async Task LegacyStringAndDefaultNullOrdinaryAgentRemainExact()
    {
        var plainModel = new RecordingModel();
        var options = new AgentOptions { Model = plainModel, SystemPrompt = "legacy" };
        Assert.False(JsonSerializer.SerializeToElement(options).TryGetProperty("SystemPromptContent", out _));
        await using var plain = new Agent(options);
        await plain.InvokeAsync("question");
        Assert.Equal("legacy", Assert.Single(plainModel.Requests).SystemPrompt);
        Assert.Null(plainModel.Requests[0].SystemPromptContent);
        Assert.Null(plain.SystemPromptContent);
        var defaultModel = new RecordingModel();
        await using var defaults = new Agent(new() { Model = defaultModel });
        await defaults.InvokeAsync("question");
        Assert.Null(Assert.Single(defaultModel.Requests).SystemPrompt);
        Assert.Null(defaultModel.Requests[0].SystemPromptContent);
    }

    [Fact]
    public async Task MalformedTypedSnapshotRejectsBeforeInitialization()
    {
        var plugin = new InitializationProbe();
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin] });
        var malformed = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["systemPrompt"] = JsonSerializer.SerializeToElement(new { unsupported = "shape" })
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(malformed).AsTask());
        Assert.Equal(0, plugin.Initializations);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("cache-only")]
    [InlineData("empty-text")]
    public async Task PresentTypedEmptyAndCacheOnlyPromptsRetainTheirMembership(string shape)
    {
        ContentBlock[] content = shape switch
        {
            "cache-only" => [new CachePointBlock("caller-owned")],
            "empty-text" => [new TextBlock("")],
            _ => []
        };
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = content });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        Assert.NotNull(request.SystemPromptContent);
        Assert.Equal(content, request.SystemPromptContent);
        Assert.Equal(shape == "empty-text" ? "" : null, request.SystemPrompt);
        Assert.NotNull(agent.SystemPromptContent);
    }

    [Theory]
    [InlineData("null-block")]
    [InlineData("null-text")]
    [InlineData("json")]
    public void InvalidTypedOptionsRejectBeforeAgentConstruction(string shape)
    {
        ContentBlock invalid = shape switch
        {
            "null-block" => null!,
            "null-text" => new TextBlock(null!),
            _ => new JsonBlock(JsonSerializer.SerializeToElement(new { text = "not system text" }))
        };
        Assert.Throws<InvalidDataException>(() => new AgentOptions { Model = new RecordingModel(), SystemPromptContent = [invalid] });
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    public void DualNonNullOptionsRejectBeforeBorrowedToolFilter(string plain, bool nonempty)
    {
        var calls = 0;
        var model = new RecordingModel();
        var tool = new FunctionTool(new("probe", "probe", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("unused"));
        var options = new AgentOptions
        {
            Model = model,
            SystemPrompt = plain,
            SystemPromptContent = nonempty ? Prompt() : [],
            Tools = [tool],
            ToolFilter = _ => { calls++; return true; }
        };
        Assert.Throws<ArgumentException>(() => new Agent(options));
        Assert.Equal(0, calls);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task OptionsCopyBeforeConstructionAndRecordCloneExposeNoMutableMembership()
    {
        var supplied = Prompt();
        var model = new RecordingModel();
        var options = new AgentOptions { Model = model, SystemPromptContent = supplied };
        supplied.Clear();
        var clone = options with { Name = "cloned" };
        var list = Assert.IsAssignableFrom<IList<ContentBlock>>(clone.SystemPromptContent);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        await using var agent = new Agent(clone);
        await agent.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        AssertPrompt(options.SystemPromptContent);
    }

    [Fact]
    public async Task DefaultChildUsesCurrentRestoredTypedPromptInsteadOfConstructionString()
    {
        var model = new RecordingModel();
        await using var parent = new Agent(new() { Model = model, SystemPrompt = "construction" });
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["systemPrompt"] = JsonSerializer.SerializeToElement<ContentBlock[]>(Prompt().ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        await parent.LoadSnapshotAsync(snapshot);
        await using var child = parent.CreateChild();
        await child.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        AssertPrompt(parent.SystemPromptContent);
        AssertPrompt(child.SystemPromptContent);
    }

    [Fact]
    public async Task ChildStringOverrideExplicitlyClearsInheritedTypedRepresentation()
    {
        var model = new RecordingModel();
        await using var parent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        Assert.Throws<ArgumentException>(() => parent.CreateChild(options => options with { SystemPrompt = "child" }));
        await using var child = parent.CreateChild(options => options with { SystemPromptContent = null, SystemPrompt = "child" });
        await child.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        Assert.Equal("child", request.SystemPrompt);
        Assert.Null(request.SystemPromptContent);
        AssertPrompt(parent.SystemPromptContent);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("first 雪\nsecond")]
    [InlineData(null)]
    public async Task MiddlewareStringAssignmentReplacesTypedPromptEvenForEqualTextOrNull(string? replacement)
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel,
            (context, _) => new ValueTask<InvokeModelContext>(context with { SystemPrompt = replacement }));
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        Assert.Null(request.SystemPromptContent);
        Assert.Equal(replacement, request.SystemPrompt);
        AssertPrompt(agent.SystemPromptContent);
    }

    [Fact]
    public async Task MiddlewareTypedAssignmentCopiesBeforeHandlerReturns()
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPrompt = "legacy" });
        var supplied = Prompt();
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) =>
        {
            var accepted = context with { SystemPromptContent = supplied };
            supplied.Clear();
            return new ValueTask<InvokeModelContext>(accepted);
        });
        await agent.InvokeAsync("question");
        AssertPrompt(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal("legacy", agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ContextLastExplicitInitializerSelectsTheWholePromptUnion(int shape)
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => new ValueTask<InvokeModelContext>(shape switch
        {
            0 => context with { SystemPrompt = "plain", SystemPromptContent = Prompt() },
            1 => context with { SystemPromptContent = Prompt(), SystemPrompt = "plain" },
            2 => context with { SystemPromptContent = null },
            _ => context with { SystemPrompt = null }
        }));
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        if (shape == 0) AssertPrompt(request.SystemPromptContent);
        else Assert.Null(request.SystemPromptContent);
        Assert.Equal(shape == 0 ? "first 雪\nsecond" : shape == 1 ? "plain" : null, request.SystemPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeModelRequestStringRewriteStillRequiresExplicitTypedClear(bool clear)
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        using var registration = agent.Hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            value.Request = clear ? value.Request with { SystemPromptContent = null, SystemPrompt = "hook replacement" }
                : value.Request with { SystemPrompt = "hook replacement" };
            return default;
        });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        if (clear) { Assert.Null(request.SystemPromptContent); Assert.Equal("hook replacement", request.SystemPrompt); }
        else { AssertPrompt(request.SystemPromptContent); Assert.Equal("first 雪\nsecond", request.SystemPrompt); }
    }

    [Fact]
    public async Task LegacySkillsKeepExactDoubleNewlineAndDoNotMutateAgentPrompt()
    {
        var model = new RecordingModel();
        var skills = new AgentSkills(new() { Skills = [] });
        await using var agent = new Agent(new() { Model = model, SystemPrompt = "legacy", Plugins = [skills] });
        await agent.InvokeAsync("question");
        var request = Assert.Single(model.Requests);
        Assert.Null(request.SystemPromptContent);
        Assert.StartsWith("legacy\n\n<available_skills>", request.SystemPrompt);
        Assert.Equal("legacy", agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
    }

    private static List<ContentBlock> Prompt() => [new TextBlock("first 雪"), new CachePointBlock("caller-owned"), new TextBlock("second")];

    private static void AssertPrompt(IReadOnlyList<ContentBlock>? blocks)
    {
        Assert.NotNull(blocks);
        Assert.Collection(blocks,
            block => Assert.Equal("first 雪", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal("caller-owned", Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("second", Assert.IsType<TextBlock>(block).Text));
    }

    private sealed class RecordingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class MemorySessionStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
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

    private sealed class InitializationProbe : IPlugin
    {
        internal int Initializations { get; private set; }
        public string Name => "stage2-initialization-probe";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
}
