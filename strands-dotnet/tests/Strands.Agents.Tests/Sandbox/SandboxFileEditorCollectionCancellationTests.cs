using System.Collections;
using Core = Strands.Agents.Sandbox;
using FileEntry = Strands.Agents.Files.FileEntry;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorCollectionCancellationTests
{
    [Theory]
    [InlineData("probe")]
    [InlineData("walk")]
    public async Task CallerCancellationAcceptedDuringDirectorySnapshotPrecedesClassificationOrEmptyOutput(string location)
    {
        using var caller = new CancellationTokenSource();
        var selected = new EditorFakeSandbox();
        var snapshot = new CancelOnSnapshot(caller, location == "probe" ? [new("other.txt", false)] : []);
        if (location == "probe") selected.Listings["/work"] = snapshot;
        else
        {
            selected.Listings["/work"] = [new("tree", true)];
            selected.Listings["/work/tree"] = snapshot;
        }
        await using var agent = EditorTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await EditorTestContext.Invoke(Core.SandboxFileEditor.Create(selected), agent,
            new { command = "view", path = location == "probe" ? "/work/missing.txt" : "/work/tree" }, caller.Token));
        Assert.True(snapshot.Callbacks > 0); // Establish the requested-token boundary independently of the expected exception.
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        Assert.DoesNotContain(selected.Calls, call => call.Operation != "list");
        Assert.Empty(selected.Writes);
    }

    private sealed class CancelOnSnapshot(CancellationTokenSource caller, FileEntry[] items) : IReadOnlyList<FileEntry>
    {
        internal int Callbacks;
        private void Enter() { Callbacks++; caller.Cancel(); }
        public int Count { get { Enter(); return items.Length; } }
        public FileEntry this[int index] { get { Enter(); return items[index]; } }
        public IEnumerator<FileEntry> GetEnumerator() { Enter(); return ((IEnumerable<FileEntry>)items).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
