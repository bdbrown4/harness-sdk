using System.Diagnostics;
using System.Text;
using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class QmdCompositionTests
{
    [Fact]
    public async Task NamespacedMemoryHydratesSelectedBytesAfterIndependentRefresh()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root, strategy);
        for (var index = 0; index < 25; index++) directory.Write($"memory/foreign/{index:00}.md", "# Walk\nwalk walk walk");
        directory.Write("memory/facts/selected.md", "# Selected\nwalking along the river");
        var selected = new FileMemoryStore(new() { Name = "facts", Storage = storage, UseStorageSearch = true });
        var entry = Assert.Single(await selected.SearchAsync("walk", 1));
        Assert.Contains("walking along the river", entry.Content);
        Assert.Equal("selected.md", entry.Metadata!.Value.GetProperty("path").GetString());
        Assert.InRange(entry.Metadata.Value.GetProperty("score").GetDouble(), double.Epsilon, 0.5);
        // Default FileMemoryStore keyword scan remains unchanged and does not stem walk→walking.
        var unchanged = new FileMemoryStore(new() { Name = "facts", Storage = storage });
        Assert.Empty(await unchanged.SearchAsync("walk"));
        await strategy.DisposeAsync();
        Assert.NotNull(await storage.ReadAsync("memory/facts/selected.md"));
    }

    [Fact]
    public async Task CustomSearchKeepsOriginalIdentityNamespaceFilteringAndDetachedData()
    {
        var bytes = Encoding.UTF8.GetBytes("accepted");
        var backend = new CustomStorage { Results = [new("outside/topic.md", 9), new("outer/selected/topic.md", 0.25, bytes)] };
        var nested = backend.Namespace("outer").Namespace("selected");
        var result = Assert.Single(await nested.SearchAsync("needle"));
        Assert.Equal("topic.md", result.Key);
        Assert.Equal(0.25, result.Score);
        Assert.Equal("accepted", Encoding.UTF8.GetString(result.Data!));
        Assert.NotSame(bytes, result.Data);
        Array.Clear(bytes, 0, bytes.Length);
        Assert.Equal("accepted", Encoding.UTF8.GetString(result.Data!));
        Assert.Equal(1, backend.SearchCalls);
        Assert.Equal("needle", backend.Query);
    }

    [Fact]
    public async Task ExplicitQmdRejectsCustomStorageWithoutCallingItsOperationsOrHostFallback()
    {
        var backend = new CustomStorage();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.SearchAsync(backend, "needle").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.UpdateAsync(backend).AsTask());
        Assert.Equal(0, backend.SearchCalls);
        Assert.Equal(0, backend.ByteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeywordAndPythonBm25NestedNamespacesKeepTheirOwnStrategies(bool python)
    {
        using var directory = new QmdDirectory();
        await using var bm25 = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root, python ? bm25 : new KeywordSearchStrategy());
        await storage.WriteAsync("outer/selected/topic.md", Encoding.UTF8.GetBytes("needle selected"));
        await storage.WriteAsync("outer/foreign/topic.md", Encoding.UTF8.GetBytes("needle foreign"));
        var result = Assert.Single(await storage.Namespace("outer").Namespace("selected").SearchAsync("needle"));
        Assert.Equal("topic.md", result.Key);
        Assert.Null(result.Data);
        if (python) Assert.InRange(result.Score, double.Epsilon, 1);
        else Assert.Equal(1, result.Score);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASelectedRootOrNamespaceLinkNeverRedirectsQmdAuthority(bool selectedNamespace)
    {
        using var directory = new QmdDirectory();
        var target = Path.Combine(directory.Parent, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "secret.md"), Encoding.UTF8.GetBytes("outsidesecret"));
        var link = selectedNamespace ? directory.PathFor("selected") : directory.Root;
        if (selectedNamespace) Directory.CreateDirectory(directory.Root);
        CreateDirectoryLink(link, target);
        try
        {
            await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
            var storage = new LocalFileStorage(directory.Root, strategy);
            var selected = selectedNamespace ? storage.Namespace("selected") : storage;
            await Assert.ThrowsAsync<StorageException>(() => selected.SearchAsync("outsidesecret").AsTask());
            Assert.Equal("outsidesecret", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(target, "secret.md"))));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("# Notes", "needle.md", "Notes")]
    [InlineData("# Notes\n##   ", "needle.md", "needle")]
    [InlineData("# \U0001f4dd Notes\n##   ", "nested/needle.MD", "needle")]
    [InlineData("#+TITLE: Specific\n* Other", "needle.ORG", "Specific")]
    [InlineData("no heading", "nested/Original.Name.txt", "Original.Name")]
    [InlineData("# Notes\n## Specific Heading", "needle.md", "Specific Heading")]
    public void TitleExtractionPreservesCaseInsensitiveExtensionAndExactFallback(string content, string key, string expected)
        => Assert.Equal(expected, QmdSearchStrategy.Title(content, key));

    private static void CreateDirectoryLink(string link, string target)
    {
#if NETFRAMEWORK
        const bool windows = true;
#else
        var windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
#endif
        var start = new ProcessStartInfo
        {
            FileName = windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/ln",
            Arguments = windows ? $"/d /c mklink /J \"{link}\" \"{target}\"" : $"-s \"{target}\" \"{link}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class CustomStorage : ISearchableStorage
    {
        internal IReadOnlyList<StorageSearchResult> Results { get; init; } = [];
        internal int SearchCalls;
        internal int ByteCalls;
        internal string? Query;
        public ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        { SearchCalls++; Query = query; return new(Results); }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { ByteCalls++; throw new InvalidOperationException("Unexpected byte read."); }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { ByteCalls++; throw new InvalidOperationException("Unexpected byte write."); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { ByteCalls++; throw new InvalidOperationException("Unexpected byte delete."); }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { ByteCalls++; throw new InvalidOperationException("Unexpected byte list."); }
    }
}
