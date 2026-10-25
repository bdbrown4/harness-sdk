using System.Text;
using System.Text.Json;
using Core = Strands.Agents.Sandbox;
using FileEntry = Strands.Agents.Files.FileEntry;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorTests
{
    [Fact]
    public async Task BoundViewUsesSelectedFilesAndFormatsRangeTabsAndTrailingLine()
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "first\n\tsecond\nthird\n";
        var other = new EditorFakeSandbox { Forbidden = true };
        await using var agent = EditorTestContext.Agent(other);
        using var caller = new CancellationTokenSource();
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/a.txt/", view_range = new[] { 2, -1 } }, caller.Token);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     2  " + new string(' ', 8) + "second\n     3  third\n     4  \n", result);
        Assert.Equal(new[] { "list:/work", "read:/work/a.txt" }, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.All(selected.Calls, call => Assert.Equal(caller.Token, call.Token));
        Assert.Empty(other.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal("first\n\tsecond\nthird\n", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateWritesEvenEmptyTextButNeverOverwritesAnExistingFile(bool exists)
    {
        var selected = new EditorFakeSandbox();
        if (exists) selected.Files["/work/new.txt"] = "original";
        await using var agent = EditorTestContext.Agent(selected);
        var tool = Core.SandboxFileEditor.CreateForAgent();
        if (exists)
        {
            var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(tool, agent, new { command = "create", path = "/work/new.txt", file_text = "" }));
            Assert.NotNull(caught);
            Assert.Contains("already exists", caught!.Message);
            Assert.Equal("original", selected.Files["/work/new.txt"]);
            Assert.Empty(selected.Writes);
        }
        else
        {
            Assert.Equal("File created successfully at: /work/new.txt", await EditorTestContext.Invoke(tool, agent, new { command = "create", path = "/work/new.txt", file_text = "" }));
            Assert.Equal("", selected.Files["/work/new.txt"]);
            Assert.Equal("/work/new.txt", Assert.Single(selected.Writes).Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniqueLiteralReplacementPreservesOtherBytesAndOmittedReplacementDeletes(bool delete)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = "pre\t\r\nliteral $&\\x\r\npost\0\n";
        await using var agent = EditorTestContext.Agent(selected);
        var input = new Dictionary<string, object?> { ["command"] = "str_replace", ["path"] = "/work/a.txt", ["old_str"] = "literal $&\\x" };
        if (!delete) input["new_str"] = "new$1";
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, input);
        Assert.Equal("pre\t\r\n" + (delete ? "" : "new$1") + "\r\npost\0\n", selected.Files["/work/a.txt"]);
        Assert.Single(selected.Writes);
        Assert.Contains("The file /work/a.txt has been edited.", result);
        Assert.Contains("a snippet of /work/a.txt", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb\n")]
    public async Task InsertAtZeroHandlesEmptyAndNonemptyFilesWithSourceSnippet(string original)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = original;
        await using var agent = EditorTestContext.Agent(selected);
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "insert", path = "/work/a.txt", insert_line = 0, new_str = "intro\nnext" });
        Assert.Equal(original.Length == 0 ? "intro\nnext" : "intro\nnext\n" + original, selected.Files["/work/a.txt"]);
        Assert.Contains("Here's the result of running `cat -n` on a snippet of the edited file:\n     1  intro\n     2  next", result);
        Assert.Single(selected.Writes);
    }

    [Fact]
    public async Task DirectoryViewUsesSourceDepthSortedRelativePathsAndExcludesHiddenItems()
    {
        var selected = new EditorFakeSandbox();
        selected.Listings["/work/tree"] = [new("z.txt", false), new("adir", true), new(".hidden", true)];
        selected.Listings["/work/tree/adir"] = [new("bdir", true), new("a.txt", false)];
        selected.Listings["/work/tree/adir/bdir"] = [new("tooDeepDir", true), new("deep.txt", false)];
        await using var agent = EditorTestContext.Agent(selected);
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.CreateForAgent(), agent, new { command = "view", path = "/work/tree" });
        Assert.Equal("Here's the files and directories up to 2 levels deep in /work/tree, excluding hidden items:\nadir\nadir/a.txt\nadir/bdir\nadir/bdir/deep.txt\nadir/bdir/tooDeepDir\nz.txt\n", result);
        Assert.DoesNotContain(selected.Calls, call => call.Path.EndsWith("/.hidden", StringComparison.Ordinal) || call.Path.EndsWith("/tooDeepDir", StringComparison.Ordinal));
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task MetadataFactoryBorrowsTheBackendAndPreservesExplicitTypeScriptName()
    {
        var selected = new EditorFakeSandbox { Forbidden = true };
        var tool = Core.SandboxFileEditor.Create(selected, new() { Name = "fileEditor", Description = "Selected full editor" });
        Assert.Equal("fileEditor", tool.Spec.Name);
        Assert.Equal("Selected full editor", tool.Spec.Description);
        Assert.Equal("command", tool.Spec.InputSchema.GetProperty("required")[0].GetString());
        Assert.Equal("file_editor", Core.SandboxFileEditor.Create().Spec.Name);
        await using (var agent = EditorTestContext.Agent(selected)) Assert.Same(selected, agent.Sandbox);
        Assert.Empty(selected.Calls);
        Assert.Equal(0, selected.Disposals);
    }
}

internal static class EditorTestContext
{
    internal static Agent Agent(Core.Sandbox selected) => new(new() { Model = new NoModel(), Sandbox = selected });
    internal static ValueTask<string> Invoke(ITool tool, Agent agent, object arguments, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(arguments);
        return tool.InvokeAsync(input, new(agent, new("editor-call", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken);
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fake editor tests must not invoke a model or process.");
    }
}

internal sealed class EditorFakeSandbox : Core.Sandbox, IDisposable
{
    internal readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, IReadOnlyList<FileEntry>> Listings = new(StringComparer.Ordinal);
    internal readonly List<(string Operation, string Path, CancellationToken Token)> Calls = [];
    internal readonly List<(string Path, byte[] Data)> Writes = [];
    internal Func<string, Exception?>? ListFailure;
    internal bool Forbidden;
    internal int Disposals;
    private void Observe(string operation, string path, CancellationToken token)
    {
        if (Forbidden) throw new InvalidOperationException("Wrong selected environment; no host fallback allowed.");
        Calls.Add((operation, path, token));
    }
    public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        Observe("list", path, cancellationToken);
        if (ListFailure?.Invoke(path) is { } failure) throw failure;
        if (Listings.TryGetValue(path, out var explicitEntries)) return new(explicitEntries);
        if (path != "/work") throw new DirectoryNotFoundException("Fake directory is missing: " + path);
        var entries = Files.Keys.Where(value => Parent(value) == path).Select(value => new FileEntry(Name(value), false))
            .Concat(Listings.Keys.Where(value => Parent(value) == path).Select(value => new FileEntry(Name(value), true)))
            .OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        return new(entries);
    }
    public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        Observe("read", path, cancellationToken);
        return new(Files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException("Fake file is missing.", path));
    }
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        Observe("write", path, cancellationToken);
        Writes.Add((path, (byte[])data.Clone()));
        Files[path] = Encoding.UTF8.GetString(data);
        return default;
    }
    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The editor text contract must use selected ReadTextAsync.");
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No deletion belongs to the editor protocol.");
    public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No command, interpreter or process may run in fake editor tests.");
    public void Dispose() => Disposals++;
    private static string Parent(string path) => path.Substring(0, path.LastIndexOf('/'));
    private static string Name(string path) => path.Substring(path.LastIndexOf('/') + 1);
}
