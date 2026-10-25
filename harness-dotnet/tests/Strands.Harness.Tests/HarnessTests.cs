using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Harness.Tests;

public sealed class HarnessTests
{
    [Fact]
    public async Task DeniedDuplicateToolsAreNotInspectedForCollisions()
    {
        var filterCalls = 0;
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = new EmptyModel(), Tools = [Tool("duplicate"), Tool("duplicate")], ToolFilter = _ => { filterCalls++; return false; } },
        });

        Assert.Empty(agent.Tools);
        Assert.Equal(2, filterCalls);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task DeniedToolSpecificationsAreNeverReadAndAcceptedToolsAreFilteredOnce()
    {
        var allowed = Tool("allowed");
        var denied = new ThrowingSpecificationTool();
        var filterCalls = new List<ITool>();
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new()
            {
                Model = new EmptyModel(),
                Tools = [denied, allowed],
                ToolFilter = tool => { filterCalls.Add(tool); return ReferenceEquals(tool, allowed); },
            },
        });

        Assert.Same(allowed, Assert.Single(agent.Tools));
        Assert.Equal(new ITool[] { denied, allowed }, filterCalls);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task CreatesAnOrdinaryAgentWithSupportedToolsAndContract()
    {
        var model = new EmptyModel();
        var agent = await Harness.CreateAsync(new HarnessOptions { Model = model, Memory = new() { Enabled = false }, Context = new() { Enabled = false }, Session = new() { Enabled = false } });

        Assert.IsType<Agent>(agent);
        Assert.Same(model, agent.Model);
        Assert.Equal(HarnessPrompt.Contract, agent.SystemPrompt);
        Assert.Equal(["shell", "read", "write", "edit", "web_fetch", "programmatic_tool_caller", "todo_write", "subagent", "strands_manage_background_task"], agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task DefaultHarnessCanBeExposedAsATool()
    {
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            Model = new EmptyModel(),
            BuiltinTools = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal("strands_harness", agent.AsTool().Spec.Name);
    }

    [Fact]
    public async Task MissingModelUsesOwnedLazyBedrockDefault()
    {
        var agent = await Harness.CreateAsync(new HarnessOptions { BackgroundTasks = null, BuiltinTools = [], BuiltinPlugins = [], Memory = new() { Enabled = false }, Context = new() { Enabled = false }, Session = new() { Enabled = false } });
        var model = Assert.IsType<BedrockModel>(agent.Model);
        Assert.Equal(Harness.DefaultModel, "bedrock/" + model.ModelId);
        Assert.Equal("global.anthropic.claude-opus-5", model.ModelId);
        Assert.Equal("us-west-2", model.Region);
        await agent.DisposeAsync();
        await using var stream = model.StreamAsync(new([])).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public async Task SuppliedModelsStayCallerOwnedUnlessCoreOptionsExplicitlyTransferOwnership()
    {
        var callerOwned = new DisposableModel();
        var agent = await Harness.CreateAsync(new HarnessOptions { BackgroundTasks = null, Model = callerOwned, BuiltinTools = [], BuiltinPlugins = [], Memory = new() { Enabled = false }, Context = new() { Enabled = false }, Session = new() { Enabled = false } });
        await agent.DisposeAsync();
        Assert.False(callerOwned.Disposed);
        var transferred = new DisposableModel();
        agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = callerOwned,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = transferred, OwnsModel = true },
        });
        await agent.DisposeAsync();
        Assert.True(transferred.Disposed);
    }

    [Fact]
    public async Task InitializationFailureReleasesAnExplicitlyOwnedModel()
    {
        var model = new DisposableModel();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = model, OwnsModel = true, Plugins = [new FailingPlugin()] },
        }));
        Assert.Equal("initialization failed", error.Message);
        Assert.True(model.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryPreservesPrimaryFailureWhenOwnedModelCleanupThrows(bool constructionFailure)
    {
        var cleanup = new IOException("model cleanup failed");
        var model = new ThrowingDisposableModel(cleanup);
        var primary = new InvalidOperationException("primary initialization failed");
        var failure = await Record.ExceptionAsync(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = model, OwnsModel = true, MaxTurns = constructionFailure ? 0 : 20, Plugins = [new FailingPlugin(primary)] },
        }));

        Assert.NotNull(failure);
        if (constructionFailure) Assert.IsType<ArgumentOutOfRangeException>(failure);
        else Assert.Same(primary, failure);
        var errors = Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]);
        Assert.Same(cleanup, Assert.Single(errors));
        Assert.Equal(1, model.DisposeCalls);
    }

    [Fact]
    public async Task CoreConfigurationWinsOverConvenienceDefaults()
    {
        var explicitModel = new EmptyModel();
        var hooks = new HookRegistry();
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            Instructions = "ignored domain text",
            BuiltinTools = [],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = explicitModel, Name = "custom", SystemPrompt = "custom prompt", Hooks = hooks },
        });

        Assert.Same(explicitModel, agent.Model);
        Assert.NotSame(hooks, agent.Hooks);
        Assert.Equal("custom", agent.Name);
        Assert.Equal("custom prompt", agent.SystemPrompt);
        Assert.Empty(agent.Tools);
        var observed = 0;
        hooks.Register<BeforeModelCallEvent>((_, _) => { observed++; return default; });
        await agent.InvokeAsync("test live application hooks");
        Assert.Equal(1, observed);
    }

    [Theory]
    [InlineData("unknown")]
    public async Task RejectsUnavailableBuiltinsInsteadOfPretendingTheyWork(string name)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            BuiltinTools = [name],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));

        Assert.Contains(name, error.Message);
    }

    [Fact]
    public async Task RejectsNormalizedToolNameCollisions()
    {
        var first = Tool("my-tool");
        var second = Tool("my_tool");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            BuiltinTools = [],
            Tools = [first, second],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));

        Assert.Contains("my_tool", error.Message);
    }

    [Fact]
    public async Task AllowsReplacingADisabledBuiltin()
    {
        var read = Tool("read");
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            BuiltinTools = ["write"],
            BuiltinPlugins = [],
            Tools = [read],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal(["write", "read"], agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Same(read, agent.Tools[1]);
    }

    [Fact]
    public async Task DuplicateBuiltinSelectionsRegisterOnlyOnce()
    {
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            BuiltinTools = ["edit", "read", "read"],
            BuiltinPlugins = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal(["read", "edit"], agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public void PromptAppendsDomainAndRequestContextInOrder()
    {
        Assert.Equal(HarnessPrompt.Contract + "\n\ndomain\n\ncontext", HarnessPrompt.Build("domain", ["context"]));
        Assert.Equal(HarnessPrompt.Contract, HarnessPrompt.Build());
    }

    [Fact]
    public void PromptMatchesThePythonHarnessContractBytes()
    {
        // SHA-256 of HARNESS_CONTRACT in harness-py/src/strands_harness/prompt.py.
        Assert.Equal("57C1190C695EE4E1286AB1E6C82680134234EE6B733650EDCC08C2BD638F8E1B",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HarnessPrompt.Contract))));
    }

    [Fact]
    public async Task ConstructionHonorsCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Harness.CreateAsync(new HarnessOptions { BackgroundTasks = null, Model = new EmptyModel() }, source.Token));
    }

    [Fact]
    public async Task NativeFileToolExecutesThroughACompleteAgentLoop()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "note.txt");
            var input = JsonSerializer.SerializeToElement(new { path, content = "verified file" });
            var model = new ScriptedModel([
                new ModelMessageStartEvent(),
                new ModelContentBlockStartEvent(0, new("write-1", "write", input)),
                new ModelContentBlockStopEvent(0),
                new ModelMessageStopEvent(StopReasons.ToolUse),
            ], Text("saved"));
            var agent = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = model,
                BuiltinTools = ["write"],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Enabled = false },
            });

            Assert.Equal("saved", (await agent.InvokeAsync("save a note")).ToString());
            Assert.Equal("verified file", await File.ReadAllTextAsync(path));
            var result = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]);
            Assert.Equal("write-1", result.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal($"Wrote 1 lines to {path}.", result.Content);
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task SessionIdIsSanitizedAndConversationResumes()
    {
        var directory = TemporaryDirectory();
        try
        {
            var first = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = new ScriptedModel(Text("first answer")),
                BuiltinTools = [],
                BuiltinPlugins = [],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Id = " My Session! ", Directory = directory },
            });
            await first.InvokeAsync("first question");
            var model = new ScriptedModel(Text("second answer"));
            var resumed = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = model,
                BuiltinTools = [],
                BuiltinPlugins = [],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Id = "my-session-", Directory = directory },
            });
            await resumed.InvokeAsync("second question");

            Assert.Equal(["first question", "first answer", "second question"], model.Requests[0].Messages.Select(message => message.Text));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ExplicitCoreSessionIdWinsOverHarnessSessionId()
    {
        var directory = TemporaryDirectory();
        try
        {
            var model = new ScriptedModel(Text("answer"));
            var agent = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = model,
                BuiltinTools = [],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Id = "sugar", Directory = directory },
                AgentOptions = new() { Model = model, SessionId = "explicit" },
            });
            await agent.InvokeAsync("question");
            var store = new FileSessionStore(directory);
            Assert.NotNull(await store.LoadAsync("explicit"));
            Assert.Null(await store.LoadAsync("sugar"));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ExplicitCoreStoreWinsEvenWhenHarnessSessionsAreDisabled()
    {
        var store = new RecordingSessionStore();
        var model = new ScriptedModel(Text("answer"));
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = model, SessionStore = store, SessionId = "core" },
        });
        await agent.InvokeAsync("question");

        Assert.Equal("core", store.SavedId);
        Assert.Equal(["question", "answer"], store.Snapshot!.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankSessionIdsFailBeforeConstruction(string sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new EmptyModel(),
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Id = sessionId },
        }));
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "strands-harness-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static ModelStreamEvent[] Text(string value) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    ];

    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            foreach (var item in turns[Requests.Count - 1])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class RecordingSessionStore : ISessionStore
    {
        public string? SavedId { get; private set; }
        public SessionSnapshot? Snapshot { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<SessionSnapshot?>(null);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SavedId = sessionId;
            Snapshot = snapshot;
            return ValueTask.CompletedTask;
        }
    }

    private static FunctionTool Tool(string name) => new(new(name, "A test tool", JsonSerializer.SerializeToElement(new { type = "object" })),
        (_, _, _) => ValueTask.FromResult("ok"));

    private sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class DisposableModel : IModel, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => new EmptyModel().StreamAsync(request, cancellationToken);
    }

    private sealed class FailingPlugin(Exception? failure = null) : IPlugin
    {
        public string Name => "failure";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw failure ?? new InvalidOperationException("initialization failed");
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class ThrowingDisposableModel(Exception failure) : IModel, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public void Dispose() { DisposeCalls++; throw failure; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => new EmptyModel().StreamAsync(request, cancellationToken);
    }

    private sealed class ThrowingSpecificationTool : ITool
    {
        public ToolSpec Spec => throw new InvalidOperationException("A denied tool specification must not be read.");
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
