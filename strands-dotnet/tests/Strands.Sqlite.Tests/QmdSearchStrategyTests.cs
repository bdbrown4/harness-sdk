using System.Text;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class QmdSearchStrategyTests
{
    [Fact]
    public async Task RealFilesMatchFrozenCompatibleEngineTitlePathBodyRanksAndDoubleScores()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var files = new Dictionary<string, string>
        {
            ["01-title.md"] = "# Glimmer\nordinary filler ordinary filler",
            ["02-body.md"] = "# Ordinary\nglimmer filler ordinary filler",
            ["glimmer/03-path.md"] = "# Ordinary\nordinary filler ordinary filler",
            ["04-stem.md"] = "# Ordinary\nwalking walked walker",
            ["05-prefix.md"] = "# Ordinary\nauthentication authorization",
            ["06-notes.MD"] = "# Notes\n## Specific Heading\nhandbook outline",
            ["07-org.ORG"] = "#+TITLE: Organization Heading\n* Other\nhandbook outline",
            ["08-unicode.md"] = "# Unicode\nCaf\u00e9 \u6771\u4eac \U00010400\U00010428",
            ["Nested Dir/Original.Name.txt"] = "projection sample text",
            [".hidden.md"] = "hiddenmarker",
            ["node_modules/excluded.txt"] = "excludedmarker",
            ["build/excluded.txt"] = "excludedmarker",
            ["09-white.txt"] = "   \n"
        };
        for (var index = 0; index < 30; index++) files.Add($"filler-{index:00}.txt", "ordinary unrelated document padding");
        foreach (var file in files) directory.Write(file.Key, file.Value);
        var results = await strategy.SearchAsync(new LocalFileStorage(directory.Root), "glimmer");
        Assert.Equal(new[] { "01-title.md", "glimmer/03-path.md", "02-body.md" }, results.Select(row => row.Key));
        var reference = new[] { 0.4463837703011528, 0.42373752948263327, 0.41226386463734893 };
        Assert.Equal(reference.Length, results.Count);
        for (var index = 0; index < reference.Length; index++) Assert.InRange(Math.Abs(reference[index] - results[index].Score), 0, 1e-12);
        Assert.All(results, row => { Assert.InRange(row.Score, double.Epsilon, 0.5); Assert.Null(row.Data); });
        Assert.Equal("04-stem.md", Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "walk")).Key);
        Assert.Equal("05-prefix.md", Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "auth")).Key);
    }

    [Fact]
    public async Task EmptySecondaryNotesHeadingFallsBackToFilenameWithReferenceTitleWeight()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        directory.Write("needle.md", "# Notes\n##   ");
        directory.Write("other.md", "# Ordinary\nneedle filler ordinary filler");
        for (var index = 0; index < 30; index++) directory.Write($"filler-{index:00}.txt", "ordinary unrelated document padding");
        var results = await strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle");
        Assert.Equal(new[] { "needle.md", "other.md" }, results.Select(row => row.Key));
        // Actual compatible2.8.3 engine fixture: independently refreshed indexes, no imported row IDs.
        Assert.InRange(Math.Abs(0.4531495362751522 - results[0].Score), 0, 1e-12);
        Assert.InRange(Math.Abs(0.4190713241335534 - results[1].Score), 0, 1e-12);
        Assert.Equal("needle", QmdSearchStrategy.Title("# Notes\n##   ", "needle.md"));
    }

    [Fact]
    public async Task SearchesRefreshExternalChangesButWhitespaceKeepsThePreviouslyIndexedBody()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("stable.txt", "oldmarker source text");
        directory.Write("removed.txt", "deletedmarker source text");
        directory.Write("rename-before.txt", "renamemarker source text");
        directory.Write("newly-empty.txt", "stalemarker source text");
        Assert.Equal("stable.txt", Assert.Single(await strategy.SearchAsync(storage, "oldmarker")).Key);
        directory.Write("stable.txt", "newmarker source text");
        File.Delete(directory.PathFor("removed.txt"));
        File.Move(directory.PathFor("rename-before.txt"), directory.PathFor("Renamed.Name.txt"));
        directory.Write("newly-empty.txt", "   \n");
        directory.Write("new-created.txt", "createdmarker source text");
        Assert.Empty(await strategy.SearchAsync(storage, "oldmarker"));
        Assert.Empty(await strategy.SearchAsync(storage, "deletedmarker"));
        Assert.Equal("stable.txt", Assert.Single(await strategy.SearchAsync(storage, "newmarker")).Key);
        Assert.Equal("Renamed.Name.txt", Assert.Single(await strategy.SearchAsync(storage, "renamemarker")).Key);
        Assert.Equal("newly-empty.txt", Assert.Single(await strategy.SearchAsync(storage, "stalemarker")).Key);
        Assert.Equal("new-created.txt", Assert.Single(await strategy.SearchAsync(storage, "createdmarker")).Key);
    }

    [Fact]
    public async Task StopwordOnlySearchStillRefreshesBeforeReturningEmpty()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("topic.txt", "emptyqueryrefreshmarker source");
        Assert.Empty(await strategy.SearchAsync(storage, "the and a"));
        directory.Write("topic.txt", "   ");
        Assert.Equal("topic.txt", Assert.Single(await strategy.SearchAsync(storage, "emptyqueryrefreshmarker")).Key);
    }

    [Fact]
    public async Task ExplicitUpdateIndexesWithoutSearching()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("topic.txt", "explicitupdatemarker source");
        await strategy.UpdateAsync(storage);
        directory.Write("topic.txt", "\t\r\n");
        Assert.Equal("topic.txt", Assert.Single(await strategy.SearchAsync(storage, "explicitupdatemarker")).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedPhysicalNamespaceIsRefreshedBeforeTheTopTwentyLimit(bool nested)
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        for (var index = 0; index < 25; index++) directory.Write($"outside-{index:00}.md", "# Needle\nneedle");
        var prefix = nested ? "outer/selected/" : "selected/";
        directory.Write(prefix + "inside.txt", "needle " + string.Join(" ", Enumerable.Repeat("ordinary", 200)));
        var storage = new LocalFileStorage(directory.Root, strategy);
        Assert.Equal(20, (await storage.SearchAsync("needle")).Count);
        IStorage selected = nested ? storage.Namespace("outer").Namespace("selected") : storage.Namespace("selected");
        Assert.Equal("inside.txt", Assert.Single(await selected.SearchAsync("needle")).Key);
    }

    [Fact]
    public async Task UnicodeSeventeenSupplementaryLettersAndCjkQueriesReachTheRealIndex()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        directory.Write("unicode.md", "# Unicode\n\U00010400\U00010428 \u6771\u4eac Caf\u00e9");
        var storage = new LocalFileStorage(directory.Root);
        Assert.Equal("unicode.md", Assert.Single(await strategy.SearchAsync(storage, "\U00010400")).Key);
        Assert.Equal("unicode.md", Assert.Single(await strategy.SearchAsync(storage, "\u6771\u4eac")).Key);
        Assert.Equal("unicode.md", Assert.Single(await strategy.SearchAsync(storage, "CAF\u00c9")).Key);
    }

    [Fact]
    public async Task TopTwentyDoesNotPromiseAnArbitraryTieOrder()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        for (var index = 0; index < 25; index++) directory.Write($"{index:00}-needle.md", "# Needle\nneedle common corpus");
        var results = await strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle");
        Assert.Equal(20, results.Count);
        Assert.Equal(20, results.Select(result => result.Key).Distinct().Count());
        Assert.All(results, result => Assert.EndsWith("-needle.md", result.Key));
    }

    [Fact]
    public async Task ExistingPythonBm25StillNeedsAnIndexedWriteWhileKeywordStillBackfills()
    {
        using var directory = new QmdDirectory();
        directory.Write("topic.md", "needle content");
        await using var python = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        Assert.Empty(await python.SearchAsync(new LocalFileStorage(directory.Root), "needle"));
        Assert.Equal("topic.md", Assert.Single(await new LocalFileStorage(directory.Root).SearchAsync("needle")).Key);
    }
}

internal sealed class QmdDirectory : IDisposable
{
    internal string Parent { get; } = Path.Combine(Path.GetTempPath(), "qmd-native-" + Guid.NewGuid().ToString("N"));
    internal string Root => Path.Combine(Parent, "files");
    internal string PathFor(string key) => Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
    internal void Write(string key, string content)
    {
        var path = PathFor(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }
    public void Dispose() { if (Directory.Exists(Parent)) Directory.Delete(Parent, true); }
}
