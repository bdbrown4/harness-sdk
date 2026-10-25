using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Memory;

namespace Strands.Compatibility.Tests;

public sealed class ToolFilterCompatibilityTests
{
    [Fact]
    public async Task FiltersGeneratedMemoryToolsWithoutLosingTypedManagerBindings()
    {
        var store = new Store();
        var memory = new MemoryManager(new() { Stores = [store], AddTool = new(), Injection = null });
        await using var agent = new Agent(new()
        {
            Model = new Model(),
            MemoryManager = memory,
            ToolFilter = tool => tool.Spec.Name == "search_memory"
        });
        await agent.InitializeAsync();
        Assert.Same(memory, agent.MemoryManager);
        Assert.Equal("search_memory", Assert.Single(agent.Tools).Spec.Name);
        await memory.AddAsync("application write");
        Assert.Equal("application write", Assert.Single(store.Values));
        Assert.Equal("application write", Assert.Single(await memory.SearchAsync("write")).Content);
    }

    [Fact]
    public async Task AddedBatchesRemainAtomicAndDefaultChildrenInheritTheFilter()
    {
        var failure = new IOException("predicate failed");
        var parent = new Agent(new()
        {
            Model = new Model(),
            Tools = [Tool("allowed_first")],
            ToolFilter = tool => tool.Spec.Name == "boom" ? throw failure : tool.Spec.Name.StartsWith("allowed", StringComparison.Ordinal)
        });
        parent.AddTools([Tool("allowed_second"), Tool("denied")]);
        Assert.Same(failure, Assert.Throws<IOException>(() => parent.AddTools([Tool("allowed_candidate"), Tool("boom")])));
        await using var child = parent.CreateChild();
        child.AddTool(Tool("denied_child"));
        Assert.Equal(new[] { "allowed_first", "allowed_second" }, child.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal(new[] { "allowed_first", "allowed_second" }, parent.Tools.Select(tool => tool.Spec.Name));
    }

    private static FunctionTool Tool(string name) => new(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ok"));
    private sealed class Store : MemoryStore, IWritableMemoryStore
    {
        internal List<string> Values { get; } = [];
        public override string Name => "facts";
        public override bool Writable => true;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
            => new(Values.Select(value => new MemoryEntry(value)).ToArray());
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Values.Add(content); return default; }
    }
    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "reply");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.Yield();
        }
    }
}
