using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

public sealed class MemoryIntegrationTests
{
    [Fact]
    public async Task InitializationExposesScopedToolsThatCannotWidenWriteAuthority()
    {
        var first = new Store("one");
        var second = new Store("two");
        var manager = new MemoryManager(new() { Stores = [first, second], AddTool = new() { Stores = ["one"] }, Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), Plugins = [manager] });
        await agent.InitializeAsync();
        Assert.Equal(1, first.Initializations);
        Assert.Equal(1, second.Initializations);
        Assert.Contains(agent.Tools, tool => tool.Spec.Name == "search_memory");
        var add = Assert.Single(agent.Tools, tool => tool.Spec.Name == "add_memory");
        var mixed = JsonSerializer.SerializeToElement(new { entries = new[] { "prefers C#" }, stores = new[] { "one", "two" } });
        await add.InvokeAsync(mixed, new(agent, new("add", "add_memory", mixed), new Dictionary<string, object?>()));
        Assert.Equal(1, first.Writes);
        Assert.Equal(0, second.Writes);
        var forbidden = JsonSerializer.SerializeToElement(new { entries = new[] { "prefers C#" }, stores = new[] { "two" } });
        await Assert.ThrowsAsync<ArgumentException>(() => add.InvokeAsync(forbidden, new(agent, new("add2", "add_memory", forbidden), new Dictionary<string, object?>())).AsTask());
        Assert.Equal(1, first.Writes);
        Assert.Equal(0, second.Writes);
    }

    [Fact]
    public async Task AutomaticRetrievalAppendsEscapedMemoryOnlyToModelInputAndRetainsMessageIdentity()
    {
        var store = new Store("facts\"<", [new("Prefers <C#> & NuGet")]);
        var model = new Model();
        var manager = new MemoryManager(new() { Stores = [store] });
        await using var agent = new Agent(new() { Model = model, MemoryManager = manager });

        await agent.InvokeAsync("What do I prefer?");

        var visible = model.Requests[0].Messages[0];
        Assert.Contains("<memory>", visible.Text);
        Assert.Contains("source=\"facts&quot;&lt;\"", visible.Text);
        Assert.Contains("Prefers &lt;C#&gt; &amp; NuGet", visible.Text);
        Assert.Equal(agent.Messages[0].TrackingId, visible.TrackingId);
        Assert.Equal("What do I prefer?", agent.Messages[0].Text);
        Assert.Same(manager, agent.MemoryManager);
        Assert.Equal(1, model.Requests[0].DynamicTrailingBlocks);
    }

    [Fact]
    public async Task ReadOnlyManagersRemoveWriteCapabilitiesAndBackendToolsWhileChildrenGetIndependentBindings()
    {
        var store = new Store("facts", [new("retained")], providesTools: true);
        var manager = new MemoryManager(new() { Stores = [store], AddTool = new() });
        await using var parent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        await parent.InitializeAsync();
        await using var child = parent.CreateChild();
        await child.InitializeAsync();
        Assert.NotSame(parent.MemoryManager, child.MemoryManager);
        var readOnly = manager.ToReadOnly();
        Assert.False(Assert.Single(readOnly.Stores).Writable);
        Assert.False(readOnly.Stores[0] is IWritableMemoryStore);
        Assert.False(readOnly.Stores[0] is IMessageMemoryStore);
        await using var delegated = new Agent(new() { Model = new Model(), MemoryManager = readOnly });
        await delegated.InitializeAsync();
        Assert.DoesNotContain(delegated.Tools, tool => tool.Spec.Name is "add_memory" or "backend_admin");
        Assert.Equal("retained", Assert.Single(await readOnly.SearchAsync("facts")).Content);
        await Assert.ThrowsAsync<ArgumentException>(() => readOnly.AddAsync("forbidden").AsTask());
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task AcceptedBackgroundWritesSurviveCallerCancellationAndFlushWaitsForCompletion()
    {
        var store = new BlockedStore();
        var manager = new MemoryManager(new() { Stores = [store], AddTool = new() { WaitForWrites = false }, Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        await agent.InitializeAsync();
        using var caller = new CancellationTokenSource();
        var input = JsonSerializer.SerializeToElement(new { entries = new[] { "fact" } });
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "add_memory");
        try
        {
            var accepted = await tool.InvokeAsync(input, new(agent, new("add", "add_memory", input), new Dictionary<string, object?>()), caller.Token);
            Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(accepted).GetProperty("accepted").GetInt32());
            caller.Cancel();
            var flush = manager.FlushAsync().AsTask();
            Assert.False(flush.IsCompleted);
            Assert.False(store.Token.CanBeCanceled);
            store.Complete.TrySetResult(true);
            await flush;
            Assert.Equal(1, store.CompletedWrites);
        }
        finally { store.Complete.TrySetResult(true); await manager.FlushAsync(); }
    }

    private sealed class BlockedStore : MemoryStore, IWritableMemoryStore
    {
        public override string Name => "blocked";
        public override bool Writable => true;
        internal readonly TaskCompletionSource<bool> Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int CompletedWrites;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Token = cancellationToken; await Complete.Task; CompletedWrites++; }
    }

    private sealed class Store(string name, IReadOnlyList<MemoryEntry>? entries = null, bool providesTools = false) : MemoryStore, IWritableMemoryStore
    {
        public override string Name => name;
        public override bool Writable => true;
        internal int Initializations;
        internal int Writes;
        public override ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new(entries ?? []);
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default) { Writes++; return default; }
        public override IReadOnlyList<ITool> GetTools() => providesTools ? [new FunctionTool(new("backend_admin", "Backend administrative operation", JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""")), (_, _, _) => new("backend"))] : [];
    }

    private sealed class Model : IModel
    {
        internal readonly List<ModelRequest> Requests = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "ready");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
