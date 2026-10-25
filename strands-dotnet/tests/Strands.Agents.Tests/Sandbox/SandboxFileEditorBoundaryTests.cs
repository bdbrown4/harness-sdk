using System.Text;
using System.Text.Json;
using Core = Strands.Agents.Sandbox;
using FileEntry = Strands.Agents.Files.FileEntry;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedWriteJoinsBeforeReturningSuccessOrItsOriginalCausalFailureAfterCancellation(bool fail)
    {
        using var caller = new CancellationTokenSource();
        var entered = Gate(); var release = Gate();
        var primary = new IOException("Known accepted-write failure.");
        var selected = new BoundarySandbox();
        selected.Write = WriteAsync;
        async ValueTask WriteAsync(string path, byte[] bytes, CancellationToken token)
        {
            Assert.Equal(caller.Token, token); entered.TrySetResult(true);
            await release.Task;
            if (fail) throw primary;
            selected.Files[path] = Encoding.UTF8.GetString(bytes);
        }
        await using var agent = EditorTestContext.Agent(selected);
        var operation = EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "create", path = "/work/new.txt", file_text = "accepted" }, caller.Token).AsTask();
        Exception? observed = null; string? result = null;
        try { await Started(entered.Task, operation); caller.Cancel(); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); observed = await Record.ExceptionAsync(async () => { result = await operation; }); }
        if (fail) { Assert.Same(primary, observed); Assert.False(selected.Files.ContainsKey("/work/new.txt")); }
        else { Assert.Null(observed); Assert.Equal("File created successfully at: /work/new.txt", result); Assert.Equal("accepted", selected.Files["/work/new.txt"]); }
        Assert.True(caller.IsCancellationRequested); Assert.Equal(1, selected.Calls.Count(call => call.Kind == "write"));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("read")]
    public async Task RequestedCallerTokenStopsBeforeAnyNextMutation(string point)
    {
        using var caller = new CancellationTokenSource();
        var selected = new BoundarySandbox(); selected.Files["/work/a.txt"] = "before";
        if (point == "entry") caller.Cancel();
        else selected.Read = (_, token) => { Assert.Equal(caller.Token, token); caller.Cancel(); return new("before"); };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent,
            new { command = "str_replace", path = "/work/a.txt", old_str = "before", new_str = "after" }, caller.Token));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        Assert.DoesNotContain(selected.Calls, call => call.Kind == "write"); Assert.Equal("before", selected.Files["/work/a.txt"]);
        if (point == "entry") Assert.Empty(selected.Calls);
    }

    [Theory]
    [InlineData("io", Core.SandboxDirectoryErrorPolicy.SkipIoFailures, true)]
    [InlineData("permission", Core.SandboxDirectoryErrorPolicy.SkipIoFailures, true)]
    [InlineData("unknown", Core.SandboxDirectoryErrorPolicy.SkipIoFailures, false)]
    [InlineData("unknown", Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures, true)]
    public async Task DirectoryWalkProfilesOnlySkipTheSelectedFailureClasses(string kind, Core.SandboxDirectoryErrorPolicy policy, bool skip)
    {
        Exception failure = kind == "io" ? new IOException("Known walk I/O.") : kind == "permission" ? new UnauthorizedAccessException("Known walk permission.") : new InvalidOperationException("Known walk application cause.");
        var selected = new BoundarySandbox { List = (path, _) => path == "/work" ? new([new("tree", true)]) : throw failure };
        await using var agent = EditorTestContext.Agent(selected);
        string? result = null;
        var caught = await Record.ExceptionAsync(async () => { result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected, new() { DirectoryErrorPolicy = policy }), agent, new { command = "view", path = "/work/tree" }); });
        if (skip) { Assert.Null(caught); Assert.Equal("Here's the files and directories up to 2 levels deep in /work/tree, excluding hidden items:\n\n", result); }
        else Assert.Same(failure, caught);
        Assert.DoesNotContain(selected.Calls, call => call.Kind != "list");
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("walk")]
    [InlineData("read")]
    [InlineData("write")]
    public async Task IndependentBackendCancellationKeepsItsExactIdentityTokenAndData(string point)
    {
        using var independent = new CancellationTokenSource(); independent.Cancel();
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException("Known independent backend cancellation.", independent.Token);
        var marker = new object(); primary.Data["fixture"] = marker;
        var selected = new BoundarySandbox(); selected.Files["/work/a.txt"] = "before";
        if (point == "probe") selected.List = (_, _) => throw primary;
        if (point == "walk") selected.List = (path, _) => path == "/work" ? new([new("tree", true)]) : throw primary;
        if (point == "read") selected.Read = (_, _) => throw primary;
        if (point == "write") selected.Write = (_, _, _) => throw primary;
        await using var agent = EditorTestContext.Agent(selected);
        object input = point == "walk" ? new { command = "view", path = "/work/tree" }
            : point == "write" ? new { command = "create", path = "/work/new.txt", file_text = "new" }
            : new { command = "view", path = "/work/a.txt" };
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected, new() { DirectoryErrorPolicy = Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures }), agent, input, caller.Token));
        Assert.Same(primary, caught); Assert.Equal(independent.Token, primary.CancellationToken); Assert.Same(marker, primary.Data["fixture"]); Assert.False(caller.IsCancellationRequested);
    }

    [Theory]
    [InlineData("probe", Core.SandboxDirectoryErrorPolicy.SkipIoFailures)]
    [InlineData("walk", Core.SandboxDirectoryErrorPolicy.SkipIoFailures)]
    [InlineData("walk", Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures)]
    public async Task CleanupBearingIoCauseIsNeverReclassifiedAsMissingOrSkipped(string point, Core.SandboxDirectoryErrorPolicy policy)
    {
        var cleanup = new IOException("Independent cleanup."); var journal = new[] { cleanup };
        var primary = new DirectoryNotFoundException("Known primary with independent cleanup."); primary.Data["Strands.CleanupErrors"] = journal;
        var selected = new BoundarySandbox { List = (path, _) => point == "walk" && path == "/work" ? new([new("tree", true)]) : throw primary };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected, new() { DirectoryErrorPolicy = policy }), agent,
            new { command = "view", path = point == "walk" ? "/work/tree" : "/work/a.txt" }));
        Assert.Same(primary, caught); Assert.Same(journal, primary.Data["Strands.CleanupErrors"]); Assert.Same(cleanup, Assert.Single(journal));
        Assert.DoesNotContain(selected.Calls, call => call.Kind != "list");
    }

    [Fact]
    public async Task DirectoryEntryBatchIsDetachedBeforeTheFirstRecursiveAwait()
    {
        var entered = Gate(); var release = Gate(); var rows = new List<FileEntry> { new("branch", true), new("later.txt", false) };
        var selected = new BoundarySandbox { List = ListAsync };
        ValueTask<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken token) => path == "/work" ? new([new("tree", true)]) : path == "/work/tree" ? new(rows) : new(ChildAsync());
        async Task<IReadOnlyList<FileEntry>> ChildAsync() { entered.TrySetResult(true); await release.Task; return Array.Empty<FileEntry>(); }
        await using var agent = EditorTestContext.Agent(selected);
        var operation = EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/tree" }).AsTask();
        Exception? caught = null; string? result = null;
        try { await Started(entered.Task, operation); rows.Clear(); rows.Add(new("corrupted.txt", false)); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); caught = await Record.ExceptionAsync(async () => { result = await operation; }); }
        Assert.Null(caught); Assert.Contains("later.txt", result!); Assert.DoesNotContain("corrupted.txt", result!);
    }

    [Fact]
    public async Task DirectoryDuplicatesRemainVisibleRatherThanInventingDeduplication()
    {
        var selected = new BoundarySandbox { List = (path, _) => path == "/work" ? new([new("tree", true)]) : new([new("same.txt", false), new("same.txt", false)]) };
        await using var agent = EditorTestContext.Agent(selected);
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/tree" });
        Assert.EndsWith("\nsame.txt\nsame.txt\n", result);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("walk")]
    public async Task MalformedNullDirectoryEntryIsNotSwallowedByTheBroadWalkProfile(string point)
    {
        var selected = new BoundarySandbox { List = (path, _) => point == "walk" && path == "/work" ? new([new("tree", true)]) : new(new FileEntry[] { null! }) };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected, new() { DirectoryErrorPolicy = Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures }), agent,
            new { command = "view", path = point == "walk" ? "/work/tree" : "/work/a.txt" }));
        Assert.IsAssignableFrom<InvalidDataException>(caught); Assert.DoesNotContain(selected.Calls, call => call.Kind != "list");
    }

    [Theory]
    [InlineData(Core.SandboxDirectoryErrorPolicy.SkipIoFailures)]
    [InlineData(Core.SandboxDirectoryErrorPolicy.SkipNonCancellationFailures)]
    public async Task SnapshotEnumerationFailureStaysCausalInsteadOfBecomingASkippedWalk(Core.SandboxDirectoryErrorPolicy policy)
    {
        var primary = new IOException("Known collection snapshot cause.");
        var selected = new BoundarySandbox { List = (path, _) => path == "/work" ? new([new("tree", true)]) : new(new ThrowingEntries(primary)) };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected, new() { DirectoryErrorPolicy = policy }), agent, new { command = "view", path = "/work/tree" }));
        Assert.Same(primary, caught); Assert.DoesNotContain(selected.Calls, call => call.Kind != "list");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("multiple")]
    [InlineData("overlap")]
    [InlineData("multiline")]
    [InlineData("large-delete")]
    [InlineData("empty-old")]
    public async Task LiteralReplacementCountsBytesAndSourceSnippetArithmeticArePreserved(string scenario)
    {
        var selected = new BoundarySandbox();
        var before = scenario == "multiple" ? "XX\nXX" : scenario == "overlap" ? "aaa" : scenario == "multiline" ? "head\r\nfirst\nsecond\r\nfoot"
            : scenario == "large-delete" ? "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\na\nb\nc\nd\ne\nf\ng\nh\ni\nj" : "original";
        var old = scenario == "multiple" ? "XX" : scenario == "overlap" ? "aa" : scenario == "multiline" ? "first\nsecond"
            : scenario == "large-delete" ? "0\n1\n2\n3\n4\n5\n6\n7\n8\n9" : scenario == "empty-old" ? "" : "absent";
        var replacement = scenario == "multiline" ? "$1\nnext" : scenario == "large-delete" ? "" : "Z";
        selected.Files["/work/a.txt"] = before;
        await using var agent = EditorTestContext.Agent(selected);
        string? result = null;
        var caught = await Record.ExceptionAsync(async () => { result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "str_replace", path = "/work/a.txt", old_str = old, new_str = replacement }); });
        if (scenario is "none" or "multiple" or "empty-old")
        {
            Assert.NotNull(caught); Assert.Equal(before, selected.Files["/work/a.txt"]); Assert.DoesNotContain(selected.Calls, call => call.Kind == "write");
            Assert.Contains(scenario == "none" ? "did not appear verbatim" : scenario == "multiple" ? "lines [1,2]" : "old_str must not be empty", caught!.Message);
        }
        else
        {
            Assert.Null(caught); Assert.Equal(1, selected.Calls.Count(call => call.Kind == "write"));
            Assert.Equal(scenario == "overlap" ? "Za" : scenario == "multiline" ? "head\r\n$1\nnext\r\nfoot" : "\na\nb\nc\nd\ne\nf\ng\nh\ni\nj", selected.Files["/work/a.txt"]);
            if (scenario == "large-delete") { Assert.Contains("     7  f\n", result!); Assert.DoesNotContain("     8  g\n", result!); }
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("////")]
    [InlineData("C:\\")]
    public async Task BareRootsFollowThePinnedTrailingSeparatorRejectionWithoutHostResolution(string path)
    {
        var selected = new BoundarySandbox(); await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path }));
        Assert.IsAssignableFrom<ArgumentException>(caught); Assert.Empty(selected.Calls);
    }

    [Theory]
    [InlineData("C:\\a", "C:/")]
    [InlineData("\\\\server\\share\\a", "//server/share")]
    public async Task PortableAbsolutePathsKeepTheirSelectedParentRootAndOriginalReadPath(string path, string parent)
    {
        var selected = new BoundarySandbox { List = (actual, _) => { Assert.Equal(parent, actual); return new([new("a", false)]); }, Read = (actual, _) => { Assert.Equal(path, actual); return new("content"); } };
        await using var agent = EditorTestContext.Agent(selected);
        var result = await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path });
        Assert.Contains("     1  content", result); Assert.Equal(new[] { "list", "read" }, selected.Calls.Select(call => call.Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredChildrenRebindOnlyUnboundToolsAndBorrowBothSandboxes(bool bound)
    {
        var first = new BoundarySandbox(); first.Files["/work/a.txt"] = "parent-data";
        var second = new BoundarySandbox(); second.Files["/work/a.txt"] = "child-data";
        var tool = bound ? Core.SandboxFileEditor.Create(first) : Core.SandboxFileEditor.CreateForAgent();
        await using (var parent = new Agent(new() { Model = new NoModel(), Sandbox = first, Tools = [tool] }))
        {
            await parent.InitializeAsync();
            await using var child = parent.CreateChild(options => options with { Sandbox = second });
            var result = await child.InvokeToolAsync(new("child-editor", tool.Spec.Name, JsonSerializer.SerializeToElement(new { command = "view", path = "/work/a.txt" })));
            Assert.Equal(ToolResultStatus.Success, result.Status); Assert.Contains(bound ? "parent-data" : "child-data", result.Content); Assert.Same(tool, Assert.Single(child.Tools));
        }
        Assert.Equal(0, first.Disposals); Assert.Equal(0, second.Disposals);
        Assert.Equal(bound ? 2 : 0, first.Calls.Count); Assert.Equal(bound ? 0 : 2, second.Calls.Count);
    }

    [Theory]
    [InlineData("str_replace")]
    [InlineData("insert")]
    public async Task MutationReadCapIsCheckedInUtf8BeforeWriting(string command)
    {
        var selected = new BoundarySandbox(); selected.Files["/work/a.txt"] = new string('\u00e9', 524288) + "a";
        await using var agent = EditorTestContext.Agent(selected);
        object input = command == "insert" ? new { command, path = "/work/a.txt", insert_line = 0, new_str = "new" }
            : new { command, path = "/work/a.txt", old_str = "absent", new_str = "new" };
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, input));
        Assert.NotNull(caught); Assert.Contains("1048577 bytes", caught!.Message); Assert.DoesNotContain(selected.Calls, call => call.Kind == "write");
    }

    [Fact]
    public async Task ReadSizeLimitDoesNotBecomeAnInventedCreateLimit()
    {
        var selected = new BoundarySandbox(); var text = new string('a', 1048577);
        await using var agent = EditorTestContext.Agent(selected);
        await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "create", path = "/work/new.txt", file_text = text });
        Assert.Equal(text, selected.Files["/work/new.txt"]); Assert.DoesNotContain(selected.Calls, call => call.Kind == "read");
    }

    [Theory]
    [InlineData("one")]
    [InlineData("three")]
    [InlineData("fraction")]
    [InlineData("replacement-number")]
    public async Task RegisteredSchemaRejectsMalformedNonemptyValuesBeforeSelectedCallbacks(string kind)
    {
        var selected = new BoundarySandbox(); selected.Files["/work/a.txt"] = "original";
        var tool = Core.SandboxFileEditor.CreateForAgent();
        await using var agent = new Agent(new() { Model = new NoModel(), Sandbox = selected, Tools = [tool] });
        var input = new Dictionary<string, object?> { ["command"] = "view", ["path"] = "/work/a.txt" };
        input[kind == "replacement-number" ? "new_str" : "view_range"] = kind == "one" ? new[] { 1 } : kind == "three" ? new[] { 1, 2, 3 } : kind == "fraction" ? new[] { 1.5, 2.0 } : 12;
        var result = await agent.InvokeToolAsync(new("invalid-editor", tool.Spec.Name, JsonSerializer.SerializeToElement(input)));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("schema", result.Content); Assert.Empty(selected.Calls);
    }

    [Fact]
    public async Task ViewRangeOnDirectoryFailsBeforeDirectoryWalk()
    {
        var selected = new BoundarySandbox { List = (path, _) => { Assert.Equal("/work", path); return new([new("tree", true)]); } };
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/tree", view_range = new[] { 1, 1 } }));
        Assert.IsAssignableFrom<ArgumentException>(caught); Assert.Single(selected.Calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 99)]
    public async Task FileRangeBoundsFailWithoutMutation(int start, int end)
    {
        var selected = new BoundarySandbox(); selected.Files["/work/a.txt"] = "one\ntwo";
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent, new { command = "view", path = "/work/a.txt", view_range = new[] { start, end } }));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(caught); Assert.Equal("one\ntwo", selected.Files["/work/a.txt"]); Assert.DoesNotContain(selected.Calls, call => call.Kind == "write");
    }

    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Started(Task expected, Task operation)
    {
        var observed = await Task.WhenAny(expected, operation, Task.Delay(TimeSpan.FromSeconds(5)));
        if (ReferenceEquals(observed, operation)) await operation;
        Assert.Same(expected, observed);
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Boundary tests never invoke a model, command or interpreter.");
    }
    private sealed class ThrowingEntries(Exception primary) : IReadOnlyList<FileEntry>
    {
        public int Count => 2;
        public FileEntry this[int index] => index == 0 ? new("first.txt", false) : throw primary;
        public IEnumerator<FileEntry> GetEnumerator() => Enumerate().GetEnumerator();
        private IEnumerable<FileEntry> Enumerate() { yield return new("first.txt", false); throw primary; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class BoundarySandbox : Core.Sandbox, IDisposable
    {
        internal readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        internal readonly List<(string Kind, string Path, CancellationToken Token)> Calls = [];
        internal Func<string, CancellationToken, ValueTask<IReadOnlyList<FileEntry>>>? List;
        internal Func<string, CancellationToken, ValueTask<string>>? Read;
        internal Func<string, byte[], CancellationToken, ValueTask>? Write;
        internal int Disposals;
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(("list", path, cancellationToken));
            return List is { } handler ? handler(path, cancellationToken) : new(Files.Keys.Where(value => value.StartsWith(path + "/", StringComparison.Ordinal) && value.IndexOf('/', path.Length + 1) < 0).Select(value => new FileEntry(value.Substring(path.Length + 1), false)).ToArray());
        }
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(("read", path, cancellationToken)); return Read is { } handler ? handler(path, cancellationToken) : new(Files[path]);
        }
        public override async ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Calls.Add(("write", path, cancellationToken));
            if (Write is { } handler) await handler(path, data, cancellationToken);
            else Files[path] = Encoding.UTF8.GetString(data);
        }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process or interpreter is permitted.");
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Editor must use its selected text contract.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Editor protocol has no delete-file operation.");
        public void Dispose() => Disposals++;
    }
}
