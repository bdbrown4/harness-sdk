using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;
using Strands.Agents.Memory;
using Strands.Agents.Storage;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class MemoryHarnessCompatibilityTests
{
    [Fact]
    public async Task BinaryFilesystemRetainsBomAndOpaqueLargeValuesAndPreservesCanceledOverwrites()
    {
        var directory = TemporaryDirectory();
        try
        {
            var files = new LocalFileSystem(maxReadBytes: 1);
            var path = Path.Combine(directory, "nested", "opaque.bin");
            var data = new byte[6 * 1024 * 1024];
            data[0] = 0xef; data[1] = 0xbb; data[2] = 0xbf; data[data.Length - 1] = 0xff;
            var writing = files.WriteBytesAsync(path, data).AsTask();
            data[3] = 99;
            await Bounded(writing);
            data[3] = 0;
            Assert.True(data.SequenceEqual(await files.ReadBytesAsync(path)), "The complete opaque byte payload must be retained.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.WriteBytesAsync(path, new byte[] { 1 }, canceled.Token).AsTask());
            Assert.True(data.SequenceEqual(await files.ReadBytesAsync(path)), "The complete opaque byte payload must be retained.");
            await Assert.ThrowsAsync<IOException>(() => files.ReadTextAsync(path).AsTask());
            await files.DeleteFileAsync(path);
            await files.DeleteFileAsync(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => files.ReadBytesAsync(path).AsTask());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DefaultMemoryPersistsThroughTheNativeSandboxAndRecallsAcrossIndependentAgents()
    {
        var directory = TemporaryDirectory();
        try
        {
            var auxiliary = new Model(Text("[{\"content\":\"# Preferences\\nPrefers Café coffee.\"}]"));
            await using (var first = await Bounded(HarnessFactory.CreateAsync(Options(new Model(Text("saved"))) with
            {
                BackgroundTasks = null,
                AuxModel = auxiliary,
                Memory = new() { Directory = directory },
            })))
            {
                Assert.IsAssignableFrom<IFileSystem>(first.FileSystem);
                Assert.Equal("search_memory", Assert.Single(first.Tools).Spec.Name);
                Assert.Equal("saved", (await Bounded(first.InvokeAsync("I prefer Café coffee."))).ToString());
            }
            Assert.Single(auxiliary.Requests);
            Assert.Contains("Prefers Café coffee.", File.ReadAllText(Path.Combine(directory, "preferences.md"), Encoding.UTF8));
            var recall = new Model(Text("recalled"));
            await using var second = await Bounded(HarnessFactory.CreateAsync(Options(recall) with
            {
                BackgroundTasks = null,
                AuxModel = new Model(Text("[]")),
                Memory = new() { Directory = directory },
            }));

            Assert.Equal("recalled", (await Bounded(second.InvokeAsync("What coffee do I prefer?"))).ToString());

            Assert.Contains("Prefers Café coffee.", Assert.Single(recall.Requests).Messages.Last().Text);
            Assert.All(second.Messages, message => Assert.DoesNotContain("<memory>", message.Text));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TextOnlySandboxNeedsExplicitByteStorageAndNeverFallsBackToTheHost()
    {
        var sandbox = new TextOnlySandbox();
        var options = Options(new Model(Text("unused"))) with { Sandbox = sandbox };
        await Assert.ThrowsAsync<NotSupportedException>(() => HarnessFactory.CreateAsync(options with { BackgroundTasks = null }));
        var storage = new InMemoryStorage();
        await using var agent = await Bounded(HarnessFactory.CreateAsync(options with { BackgroundTasks = null, Memory = new() { Storage = storage } }));

        await Assert.IsAssignableFrom<IWritableMemoryStore>(Assert.Single(agent.MemoryManager!.Stores)).AddAsync("# Preferences\nPrefers coffee.");

        Assert.NotNull(await storage.ReadAsync("preferences.md"));
        Assert.Equal(0, sandbox.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatesHaveReadOnlyMemoryAndCannotRegenerateExcludedSearchTools(bool explicitManager)
    {
        var store = new WritableStore();
        var model = new Model(Call("subagent", new { task = "recall", tools = Array.Empty<string>() }), Text("child"), Text("parent"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Bounded(HarnessFactory.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks, MemoryManager = explicitManager ? new(new() { Stores = new MemoryStore[] { store } }) : null },
            BuiltinTools = new[] { "subagent" },
            Memory = new() { Stores = new MemoryStore[] { store } },
        }));

        Assert.Equal("parent", (await Bounded(parent.InvokeAsync("delegate"))).ToString());

        var child = agents[1];
        Assert.Empty(child.Tools);
        if (explicitManager) Assert.Null(child.MemoryManager);
        else
        {
            var readOnly = Assert.Single(child.MemoryManager!.Stores);
            Assert.False(readOnly.Writable);
            Assert.False(readOnly is IWritableMemoryStore);
            Assert.False(readOnly is IMessageMemoryStore);
            Assert.Null(readOnly.Extraction);
            Assert.Empty(readOnly.GetTools());
            Assert.Contains("Persisted preference", model.Requests[1].Messages.Last().Text);
        }
        Assert.Equal(0, store.Writes);
    }

    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = Array.Empty<string>(),
        BuiltinPlugins = Array.Empty<string>(),
        Skills = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
    };
    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "strands memory compatibility " + Guid.NewGuid().ToString("N"));
    private static async Task<T> Bounded<T>(Task<T> task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))))) throw new TimeoutException("The memory compatibility operation did not complete.");
        return await task;
    }
    private static async Task Bounded(Task task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))))) throw new TimeoutException("The memory compatibility operation did not complete.");
        await task;
    }
    private static ModelStreamEvent[] Text(string text) => new ModelStreamEvent[] { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn) };
    private static ModelStreamEvent[] Call(string name, object input) => new ModelStreamEvent[] { new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("delegate", name, JsonSerializer.SerializeToElement(input))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse) };
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
    private sealed class TextOnlySandbox : ISandbox
    {
        public int Accesses { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Accesses++; throw new InvalidOperationException("Text I/O is not granted by this test."); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) { Accesses++; throw new InvalidOperationException("Text I/O is not granted by this test."); }
    }
    private sealed class WritableStore : MemoryStore, IWritableMemoryStore, IMessageMemoryStore
    {
        public int Writes { get; private set; }
        public override string Name => "facts";
        public override bool Writable => true;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)new[] { new MemoryEntry("Persisted preference") });
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default) { Writes++; return default; }
        public ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default) { Writes++; return default; }
        public override IReadOnlyList<ITool> GetTools() => new ITool[] { new FunctionTool(new("backend_write", "Write a fact", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Writes++; return new("written"); }) };
    }
}
