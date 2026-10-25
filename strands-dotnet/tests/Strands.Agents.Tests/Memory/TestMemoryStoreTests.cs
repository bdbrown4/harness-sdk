using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

public sealed class TestMemoryStoreTests
{
    [Fact]
    public async Task PersistentRecordsUseTheSharedJsonFormatAndAreRecalledByAFreshInstance()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        try
        {
            var first = new TestMemoryStore(new() { Name = "notes", Path = path });
            Assert.False(File.Exists(path));
            var added = await first.AddAsync("User prefers dark mode", JsonSerializer.SerializeToElement(new { source = "user" }));
            Assert.True(Guid.TryParse(added.Id, out var id));
            Assert.Equal('4', id.ToString("D")[14]);
            var raw = File.ReadAllText(path);
            Assert.Contains("\n  ", raw, StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(raw);
            var record = Assert.Single(parsed.RootElement.EnumerateArray());
            Assert.Equal(new[] { "content", "createdAt", "id", "metadata" }, record.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(added.Id, record.GetProperty("id").GetString());
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", record.GetProperty("createdAt").GetString()!);

            var second = new TestMemoryStore(new() { Name = "notes", Path = path });
            var result = Assert.Single(await second.SearchAsync("dark mode preference"));
            Assert.Equal("User prefers dark mode", result.Content);
            Assert.Null(result.StoreName);
            Assert.Equal("user", result.Metadata!.Value.GetProperty("source").GetString());
            Assert.Equal(2, result.Metadata.Value.GetProperty("_relevanceScore").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrimmedDuplicatesKeepTheOriginalIdContentTimestampMetadataAndUnknownFields()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        const string original = "[{\"id\":\"opaque-existing\",\"content\":\"  coffee preference  \",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"metadata\":{\"source\":\"original\"},\"futureField\":{\"retained\":true}}]";
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, original);
            var store = new TestMemoryStore(new() { Name = "notes", Path = path });
            var result = await store.AddAsync("coffee preference", JsonSerializer.SerializeToElement(new { source = "replacement" }));
            Assert.Equal("opaque-existing", result.Id);
            Assert.Equal(original, File.ReadAllText(path));
            var found = Assert.Single(await store.SearchAsync("coffee"));
            Assert.Equal("  coffee preference  ", found.Content);
            Assert.Equal("original", found.Metadata!.Value.GetProperty("source").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SearchRanksDistinctContentOverlapThenRecencyWithoutMatchingIdOrMetadata()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        var records = new[]
        {
            new { id = "first", content = "cat dog", createdAt = "2026-01-01T00:00:00.000Z", metadata = new { hint = "" } },
            new { id = "second", content = "cat lives here", createdAt = "2026-01-02T00:00:00.000Z", metadata = new { hint = "" } },
            new { id = "third", content = "cat stays there", createdAt = "2026-01-03T00:00:00.000Z", metadata = new { hint = "" } },
            new { id = "cat dog", content = "unrelated note", createdAt = "2026-01-04T00:00:00.000Z", metadata = new { hint = "cat dog" } },
        };
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, JsonSerializer.Serialize(records));
            var store = new TestMemoryStore(new() { Name = "notes", Path = path });
            var results = await store.SearchAsync("cat cat dog");
            Assert.Equal(new[] { "cat dog", "cat stays there", "cat lives here" }, results.Select(result => result.Content));
            Assert.Equal(new[] { 2, 1, 1 }, results.Select(result => result.Metadata!.Value.GetProperty("_relevanceScore").GetInt32()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConstructionIsInertAndTheDefaultFilenameUsesTheSharedAsciiSanitizer()
    {
        var root = Root();
        var path = Path.Combine(root, "absent", "notes.json");
        var extraction = new MemoryExtractionOptions();
        var store = new TestMemoryStore(new() { Name = "notes", Description = "description", MaxSearchResults = 7, Path = path, Extraction = extraction });
        Assert.True(store.Writable);
        Assert.Equal("notes", store.Name);
        Assert.Equal("description", store.Description);
        Assert.Equal(7, store.MaxSearchResults);
        Assert.Same(extraction, store.Extraction);
        Assert.False(Directory.Exists(root));
        Assert.Equal(Path.Combine(root, ".strands", "memory", "__weird_name.json"), TestMemoryStore.DefaultPath(root, "../weird/name"));
        Assert.Equal(Path.Combine(root, ".strands", "memory", "___-_.json"), TestMemoryStore.DefaultPath(root, "日本é-_"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("path")]
    [InlineData("limit")]
    [InlineData("null")]
    public void InvalidConfigurationFailsBeforeTheBackingDirectoryExists(string invalid)
    {
        var root = Root();
        var options = new TestMemoryStoreOptions { Name = "notes", Path = Path.Combine(root, "notes.json") };
        if (invalid == "name") options = options with { Name = "  " };
        if (invalid == "path") options = options with { Path = "  ", Persist = false };
        if (invalid == "limit") options = options with { MaxSearchResults = 0 };
        Assert.ThrowsAny<ArgumentException>(() => new TestMemoryStore(invalid == "null" ? null! : options));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ReadonlyAndInvalidAddsNeverCreateFiles()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        var readOnly = new TestMemoryStore(new() { Name = "notes", Path = path, Writable = false });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await readOnly.AddAsync("fact"));
        var writable = new TestMemoryStore(new() { Name = "notes", Path = path });
        await Assert.ThrowsAsync<ArgumentException>(async () => await writable.AddAsync("  "));
        await Assert.ThrowsAsync<ArgumentException>(async () => await writable.AddAsync("fact", JsonSerializer.SerializeToElement(new[] { 1 })));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await writable.SearchAsync("fact", 0));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task EphemeralInstancesIgnoreTheirFilePathAndNeverShareRecords()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        var first = new TestMemoryStore(new() { Name = "notes", Persist = false, Path = path });
        await first.AddAsync("private ephemeral fact");
        var second = new TestMemoryStore(new() { Name = "notes", Persist = false, Path = path });
        Assert.Single(await first.SearchAsync("fact"));
        Assert.Empty(await second.SearchAsync("fact"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DirectLimitsUseCallerThenStoreThenTenAndTiesRetainRecordOrder()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        try
        {
            Directory.CreateDirectory(root);
            var records = Enumerable.Range(0, 12).Select(i => new { id = i.ToString(), content = "fact " + i, createdAt = "same-unparsed-string" });
            File.WriteAllText(path, JsonSerializer.Serialize(records));
            var configured = new TestMemoryStore(new() { Name = "notes", Path = path, MaxSearchResults = 3 });
            Assert.Equal(new[] { "fact 0", "fact 1", "fact 2" }, (await configured.SearchAsync("fact")).Select(value => value.Content));
            Assert.Equal(7, (await configured.SearchAsync("fact", 7)).Count);
            Assert.Equal(10, (await new TestMemoryStore(new() { Name = "notes", Path = path }).SearchAsync("fact")).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AppendsRetainUnknownFieldsAndSubsequentSearchesReadExternalRewrites()
    {
        var root = Root();
        var path = Path.Combine(root, "notes.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "[{\"id\":\"\",\"content\":\"old fact\",\"createdAt\":\"not-an-ISO-date\",\"metadata\":null,\"future\":{\"keep\":[1,2]}}]");
            var store = new TestMemoryStore(new() { Name = "notes", Path = path });
            Assert.Equal("", (await store.AddAsync("old fact")).Id);
            var old = Assert.Single(await store.SearchAsync("old"));
            Assert.Equal(new[] { "_relevanceScore" }, old.Metadata!.Value.EnumerateObject().Select(property => property.Name));
            await store.AddAsync("new fact", JsonSerializer.SerializeToElement<object?>(null));
            using (var file = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(2, file.RootElement[0].GetProperty("future").GetProperty("keep")[1].GetInt32());
                Assert.Equal(JsonValueKind.Null, file.RootElement[0].GetProperty("metadata").ValueKind);
                Assert.False(file.RootElement[1].TryGetProperty("metadata", out _));
            }
            File.WriteAllText(path, "[{\"id\":\"external\",\"content\":\"external fact\",\"createdAt\":\"later\"}]");
            Assert.Equal("external fact", Assert.Single(await store.SearchAsync("fact")).Content);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnreachableFileErrorsRemainCausalAndTheStoreRecoversWhenThePathIsRepaired()
    {
        var root = Root();
        var blocker = Path.Combine(root, "blocker");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(blocker, "not a directory");
            var store = new TestMemoryStore(new() { Name = "notes", Path = Path.Combine(blocker, "notes.json") });
            var error = await Assert.ThrowsAsync<Storage.StorageException>(async () => await store.AddAsync("first fact"));
            Assert.NotNull(error.InnerException);
            File.Delete(blocker);
            await store.AddAsync("recovered fact");
            Assert.Equal("recovered fact", Assert.Single(await store.SearchAsync("fact")).Content);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingMemoryManagerRoutesWritesNamesResultsAndRunsRealAgentExtraction()
    {
        var extractor = new FixedExtractor();
        var store = new TestMemoryStore(new()
        {
            Name = "notes",
            Persist = false,
            Extraction = new() { Extractor = extractor, Triggers = [new InvocationTrigger()] },
        });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await manager.AddAsync("manual fact", new() { Metadata = JsonSerializer.SerializeToElement(new { source = "application" }) });
        Assert.Equal("notes", Assert.Single(await manager.SearchAsync("manual")).StoreName);
        var model = new AnswerModel();
        await using var agent = new Agent(new() { Model = model, MemoryManager = manager });
        Assert.Equal("answer", (await agent.InvokeAsync("Remember the extracted preference.")).ToString());
        await manager.FlushAsync();
        var extracted = Assert.Single(await manager.SearchAsync("extracted"));
        Assert.Equal("notes", extracted.StoreName);
        Assert.Equal("extracted preference", extracted.Content);
        Assert.Equal(1, extractor.Calls);
        Assert.Equal(new[] { "Remember the extracted preference.", "answer" }, extractor.Messages.Select(message => message.Text));
        var view = Assert.Single(manager.ToReadOnly().Stores);
        Assert.False(view.Writable);
        Assert.False(view is IWritableMemoryStore);
        Assert.Null(view.Extraction);
        Assert.Single(await view.SearchAsync("extracted", 3));
        await agent.DisposeAsync();
        Assert.Equal(0, model.DisposeCalls);
    }

    private sealed class FixedExtractor : IExtractor
    {
        internal int Calls;
        internal IReadOnlyList<Message> Messages = [];
        public ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; Messages = messages;
            return new(new[] { new ExtractionResult("extracted preference") });
        }
    }

    private sealed class AnswerModel : IModel, IDisposable
    {
        internal int DisposeCalls;
        public void Dispose() => DisposeCalls++;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer"); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private static string Root() => Path.Combine(Path.GetTempPath(), "test-memory-" + Guid.NewGuid().ToString("N"));
}
