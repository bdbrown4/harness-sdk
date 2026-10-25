using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;
using static Strands.Harness.Tests.NotebookTests;

namespace Strands.Harness.Tests;

public sealed class NotebookStateTests
{
    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task ReadsAndListsNeverPersistTheVirtualDefault(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        Assert.Equal("Available notebooks:\n- default: Empty", await Invoke(tool, agent, new { mode = "list" }));
        Assert.Equal("Notebook 'default' is empty", await Invoke(tool, agent, new { mode = "read" }));
        Assert.Null(agent.State.Get("notebooks"));
        agent.State.Set("notebooks", new Dictionary<string, string>());
        await Invoke(tool, agent, new { mode = "list" }); await Invoke(tool, agent, new { mode = "read" });
        Assert.Empty(agent.State.Get("notebooks")!.Value.EnumerateObject());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task FreshDefaultWritesPersistButMissingNamedOperationsLeaveStateAlone(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        var write = new Dictionary<string, object?> { ["mode"] = "write", [Key(behavior, "newStr", "new_str")] = "first" };
        Assert.Equal("Appended text to notebook 'default'", await Invoke(tool, agent, write));
        Assert.Equal("first", agent.State.Get("notebooks")!.Value.GetProperty("default").GetString());
        Assert.Equal("Cleared notebook 'default'", await Invoke(tool, agent, new { mode = "clear" }));
        var before = agent.State.Get("notebooks")!.Value.GetRawText();
        foreach (var mode in new[] { "read", "write", "clear" })
            await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, Input(behavior, mode, "missing", "value")));
        Assert.Equal(before, agent.State.Get("notebooks")!.Value.GetRawText());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("\"not a map\"")]
    [InlineData("{\"good\":\"text\",\"unselected\":null}")]
    [InlineData("{\"duplicate\":\"a\",\"duplicate\":\"b\"}")]
    public async Task MalformedWholeStateOrDuplicateKeysCannotPartiallyCommit(string json)
    {
        await using var agent = NewAgent();
        using (var document = JsonDocument.Parse(json)) agent.State.Set("notebooks", document.RootElement);
        agent.State.Set("other", new { untouched = 7 }); var before = JsonSerializer.Serialize(agent.State.GetAll());
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => Invoke(Notebook.Create(), agent, Input(NotebookBehavior.TypeScript, "create", "new", "value")));
        Assert.Contains("Malformed notebooks state", failure.Message); Assert.Equal(before, JsonSerializer.Serialize(agent.State.GetAll()));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript, "create")]
    [InlineData(NotebookBehavior.Python, "create")]
    [InlineData(NotebookBehavior.TypeScript, "append")]
    [InlineData(NotebookBehavior.Python, "append")]
    [InlineData(NotebookBehavior.TypeScript, "replace")]
    [InlineData(NotebookBehavior.Python, "replace")]
    [InlineData(NotebookBehavior.TypeScript, "insert")]
    [InlineData(NotebookBehavior.Python, "insert")]
    public async Task EveryGrowingOperationChecksItsDetachedCandidateBeforeCommit(NotebookBehavior behavior, string operation)
    {
        await using var agent = NewAgent();
        agent.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "aa", ["other"] = "keep" });
        var before = agent.State.Get("notebooks")!.Value.GetRawText();
        var input = Input(behavior, operation == "create" ? "create" : "write", "notes", operation is "create" or "replace" ? "12345" : "bb");
        if (operation == "replace") input[Key(behavior, "oldStr", "old_str")] = "aa";
        if (operation == "insert") input[Key(behavior, "insertLine", "insert_line")] = 1;
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(Notebook.Create(new() { Behavior = behavior, MaximumNotebookSizeBytes = 4 }), agent, input));
        Assert.Equal(before, agent.State.Get("notebooks")!.Value.GetRawText());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task DefaultUtf8CapAndAstralBoundaryArePerNotebook(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var tool = Notebook.Create(new() { Behavior = behavior });
        var exact = new string('x', 1_048_576);
        await Invoke(tool, agent, Input(behavior, "create", "full", exact));
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, Input(behavior, "write", "full", "x")));
        Assert.Equal(exact, agent.State.Get("notebooks")!.Value.GetProperty("full").GetString());
        var small = Notebook.Create(new() { Behavior = behavior, MaximumNotebookSizeBytes = 4 });
        await Invoke(small, agent, Input(behavior, "create", "astral", "😀"));
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(small, agent, Input(behavior, "write", "astral", "x")));
        Assert.Equal("😀", agent.State.Get("notebooks")!.Value.GetProperty("astral").GetString());
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task OversizedUnrelatedNotebookDoesNotBlockReadClearOrAnotherWrite(NotebookBehavior behavior)
    {
        await using var agent = NewAgent(); var oversized = new string('z', 100);
        agent.State.Set("notebooks", new Dictionary<string, string> { ["big"] = oversized, ["small"] = "a" });
        var tool = Notebook.Create(new() { Behavior = behavior, MaximumNotebookSizeBytes = 3 });
        Assert.Equal(oversized, await Invoke(tool, agent, Input(behavior, "read", "big")));
        await Invoke(tool, agent, Input(behavior, "write", "small", "b"));
        Assert.Equal("a\nb", agent.State.Get("notebooks")!.Value.GetProperty("small").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke(tool, agent, Input(behavior, "write", "big", "")));
        Assert.Equal("Cleared notebook 'big'", await Invoke(tool, agent, Input(behavior, "clear", "big")));
        Assert.Equal("", agent.State.Get("notebooks")!.Value.GetProperty("big").GetString());
    }

    [Fact]
    public async Task StateAndInputStayDetachedAfterCallerDocumentsAndCopiesChange()
    {
        await using var agent = NewAgent(); var tool = Notebook.Create();
        using (var document = JsonDocument.Parse("{\"notes\":\"initial\"}")) agent.State.Set("notebooks", document.RootElement);
        var json = JsonSerializer.SerializeToElement(Input(NotebookBehavior.TypeScript, "write", "notes", "appended"));
        using (var document = JsonDocument.Parse(json.GetRawText()))
            await tool.InvokeAsync(document.RootElement, new(agent, new("detached", tool.Spec.Name, document.RootElement), new Dictionary<string, object?>()));
        var copy = agent.State.Get<Dictionary<string, string>>("notebooks")!; copy.Clear();
        Assert.Equal("initial\nappended", await Invoke(tool, agent, Input(NotebookBehavior.TypeScript, "read", "notes")));
    }

    [Fact]
    public async Task OneToolIsIndependentAcrossAgentsAndDistinctToolsCoordinateOneState()
    {
        await using var first = NewAgent(); await using var second = NewAgent(); var shared = Notebook.Create();
        await Invoke(shared, first, Input(NotebookBehavior.TypeScript, "create", "notes", "first"));
        await Invoke(shared, second, Input(NotebookBehavior.TypeScript, "create", "notes", "second"));
        Assert.Equal("first", await Invoke(shared, first, Input(NotebookBehavior.TypeScript, "read", "notes")));
        var alias = Notebook.Create(new() { Name = "scratchpad", Behavior = NotebookBehavior.Python });
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 4).Select(async worker =>
        {
            await start.Task;
            for (var index = 0; index < 32; index++)
            {
                var python = (worker & 1) != 0;
                await Invoke(python ? alias : shared, first, Input(python ? NotebookBehavior.Python : NotebookBehavior.TypeScript, "write", "notes", worker + ":" + index));
            }
        }).ToArray();
        start.TrySetResult(true); await Task.WhenAll(workers);
        var lines = first.State.Get("notebooks")!.Value.GetProperty("notes").GetString()!.Split('\n');
        Assert.Equal(129, lines.Length); Assert.Equal(129, lines.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("second", second.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task RequestedCallerAndCapturedSignalLeaveStateUnchangedWithExactTokens()
    {
        await using var agent = NewAgent(); using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(Notebook.Create(), agent, Input(NotebookBehavior.TypeScript, "create", "notes", "must not write"), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Null(agent.State.Get("notebooks"));
        await using var signaled = new Agent(new() { Model = new SingleCallModel(new { mode = "list" }), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        signaled.Cancel("pending immediate cancellation"); var signal = signaled.CancelSignal; Assert.True(signal.IsCancellationRequested);
        error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(Notebook.Create(), signaled, Input(NotebookBehavior.TypeScript, "create", "notes", "must not write")));
        Assert.Equal(signal, error.CancellationToken); Assert.Null(signaled.State.Get("notebooks"));
    }

    [Theory]
    [InlineData(NotebookBehavior.TypeScript)]
    [InlineData(NotebookBehavior.Python)]
    public async Task PublicSnapshotAndFreshFileSessionRecoverTheSameNotebook(NotebookBehavior behavior)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-notebook-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            await using (var writer = new Agent(new() { Model = new SingleCallModel(Input(behavior, "create", "notes", "saved😀")), Tools = [Notebook.Create(new() { Behavior = behavior })], SessionStore = store, SessionId = "notebook" }))
            {
                await writer.InvokeAsync("create notebook");
                var snapshot = await writer.TakeSnapshotAsync(new() { Include = SnapshotFields.State });
                await using var copy = NewAgent(); await copy.LoadSnapshotAsync(snapshot);
                Assert.Equal("saved😀", await Invoke(Notebook.Create(new() { Behavior = behavior }), copy, Input(behavior, "read", "notes")));
                copy.State.Set("notebooks", new Dictionary<string, string> { ["notes"] = "changed copy" });
                Assert.Equal("saved😀", writer.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
            }
            await using (var reader = new Agent(new() { Model = new SingleCallModel(Input(behavior, "read", "notes")), Tools = [Notebook.Create(new() { Behavior = behavior })], SessionStore = store, SessionId = "notebook" }))
            {
                Assert.Equal("saved😀", (await reader.InvokeAsync("read saved notebook")).ToString());
                Assert.Equal(2, reader.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Count());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AssembledHarnessAcceptsTheExactCustomToolWithoutChangingDefaults()
    {
        var tool = Notebook.Create(); var model = new SingleCallModel(Input(NotebookBehavior.TypeScript, "create", "notes", "harness"));
        await using var agent = await Harness.CreateAsync(new() { Model = model, Tools = [tool], BuiltinTools = [], BuiltinPlugins = [], BackgroundTasks = null, Skills = new() { Enabled = false }, Memory = new() { Enabled = false }, Context = new() { Enabled = false }, Session = new() { Enabled = false } });
        Assert.Same(tool, Assert.Single(agent.Tools)); await agent.InvokeAsync("create custom notebook");
        Assert.Equal("harness", agent.State.Get("notebooks")!.Value.GetProperty("notes").GetString());
        Assert.Equal(new[] { "shell", "read", "write", "edit", "web_fetch", "web_search", "programmatic_tool_caller", "subagent" }, Harness.BuiltinToolNames);
        Assert.Equal(2, model.Calls); Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    internal sealed class SingleCallModel(object input) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref Calls); yield return new ModelMessageStartEvent();
            if (call == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("notebook-call-" + Guid.NewGuid().ToString("N"), "notebook", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: JsonSerializer.Serialize(input));
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Last().Content);
            }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(call == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
