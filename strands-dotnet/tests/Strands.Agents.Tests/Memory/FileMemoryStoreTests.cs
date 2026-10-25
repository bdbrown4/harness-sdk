using System.Runtime.CompilerServices;
using System.Text;
using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.Memory;

public sealed class FileMemoryStoreTests
{
    [Fact]
    public async Task ConcurrentFactsShareTheirHeadingTopicWithoutLosingAppendsAndSearchIncludesTheFilename()
    {
        var storage = new InMemoryStorage();
        var delayed = new GatedStorage(storage);
        var store = new FileMemoryStore(new() { Name = "facts", Storage = delayed });
        var pending = Enumerable.Range(0, 20).Select(index => store.AddAsync("# User preferences\nFact " + index).AsTask()).ToArray();
        string[] keys;
        try
        {
            Assert.Equal(1, delayed.Reads);
            delayed.Release.TrySetResult(true);
            keys = await Task.WhenAll(pending);
        }
        finally { delayed.Release.TrySetResult(true); await Task.WhenAll(pending); }
        Assert.All(keys, key => Assert.Equal("user-preferences.md", key));
        Assert.Equal(["memory/facts/user-preferences.md"], await storage.ListAsync());
        var body = Encoding.UTF8.GetString((await storage.ReadAsync("memory/facts/user-preferences.md"))!);
        Assert.StartsWith("# User preferences\n", body);
        Assert.Equal(21, body.Split('\n').Length);
        for (var index = 0; index < 20; index++) Assert.Contains("Fact " + index, body);
        var match = Assert.Single(await store.SearchAsync("preferences"));
        Assert.Equal(body, match.Content);
        Assert.Equal("user-preferences.md", match.Metadata!.Value.GetProperty("path").GetString());
    }

    private sealed class GatedStorage(IStorage storage) : IStorage
    {
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) => storage.WriteAsync(key, data, cancellationToken);
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _reads); await Release.Task.ConfigureAwait(false); return await storage.ReadAsync(key, cancellationToken).ConfigureAwait(false); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => storage.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => storage.ListAsync(prefix, cancellationToken);
    }

    [Fact]
    public async Task DefaultFileExtractionReusesExistingTopicsAndAppendsItsContractAfterCustomGuidance()
    {
        var storage = new InMemoryStorage();
        var extraction = new Model("[{\"content\":\"# User preferences\\nPrefers native C#\"}]");
        var auxiliary = new Model("[]");
        var store = new FileMemoryStore(new()
        {
            Name = "facts",
            Storage = storage,
            Extraction = new() { Model = extraction, SystemPrompt = "Remember durable platform preferences.", Triggers = [new InvocationTrigger()] }
        });
        await store.AddAsync("# User preferences\nUses NuGet");
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model("ready"), AuxModel = auxiliary, MemoryManager = manager });

        await agent.InvokeAsync("I prefer native C#");
        await manager.FlushAsync();

        var prompt = Assert.Single(extraction.Requests).SystemPrompt!;
        Assert.StartsWith("Remember durable platform preferences.", prompt);
        Assert.Contains("Return ONLY a JSON array", prompt);
        Assert.Contains("first line is a markdown heading", prompt);
        Assert.Contains("Existing topics: user preferences.", prompt);
        Assert.Empty(auxiliary.Requests);
        Assert.Equal(["memory/facts/user-preferences.md"], await storage.ListAsync());
        var body = Encoding.UTF8.GetString((await storage.ReadAsync("memory/facts/user-preferences.md"))!);
        Assert.Contains("Uses NuGet", body);
        Assert.Contains("Prefers native C#", body);
    }

    private sealed class Model(string output) : IModel
    {
        internal readonly List<ModelRequest> Requests = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
