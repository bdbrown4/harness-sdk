using System.Text.Json;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;
using EditorTests = Strands.Agents.Tests.Sandbox;

namespace Strands.Agents.Tests.Files;

public sealed class FileEntryMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldConstructorsKeepTheirKnownDirectoryAndLinkValues(bool directory)
    {
        var ordinary = new FileEntry("ordinary", directory);
        var linked = new FileEntry("linked", isDirectory: directory, isSymbolicLink: true);
        var defaults = new FileEntry("defaults", default, default);
        Assert.Equal(directory, ordinary.IsDirectory);
        Assert.Equal((bool?)directory, ordinary.DirectoryStatus);
        Assert.Null(ordinary.Size);
        Assert.False(ordinary.IsSymbolicLink);
        Assert.Equal(directory, linked.IsDirectory);
        Assert.True(linked.IsSymbolicLink);
        Assert.Equal((bool?)false, defaults.DirectoryStatus);
        Assert.Throws<ArgumentException>(() => new FileEntry("../bad", directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataDistinguishesUnknownFromKnownFalseAndZero(bool unknown)
    {
        var entry = unknown ? FileEntry.FromMetadata("entry", null) : FileEntry.FromMetadata("entry", false, 0);
        Assert.Equal("entry", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.Equal(unknown ? (bool?)null : false, entry.DirectoryStatus);
        Assert.Equal(unknown ? (long?)null : 0L, entry.Size);
    }

    [Fact]
    public void MetadataProbeReportsExactSizeAndKeepsSuccessfulPeerValues()
    {
        var calls = 0;
        var file = LocalFileSystem.CaptureListedEntry("file", () => { calls++; return FileEntry.FromMetadata("file", false, 3); }, default);
        var directory = LocalFileSystem.CaptureListedEntry("directory", () => FileEntry.FromMetadata("directory", true), default);
        Assert.Equal(1, calls);
        Assert.Equal(3L, file.Size);
        Assert.Equal((bool?)false, file.DirectoryStatus);
        Assert.Equal((bool?)true, directory.DirectoryStatus);
        Assert.Null(directory.Size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PerEntryMetadataFailureRetainsNameAndUnknownValues(bool denied)
    {
        Exception failure = denied ? new UnauthorizedAccessException("entry metadata denied") : new IOException("entry disappeared");
        var entry = LocalFileSystem.CaptureListedEntry("retained", () => throw failure, default);
        var peer = LocalFileSystem.CaptureListedEntry("peer", () => new FileEntry("peer", true), default);
        Assert.Equal("retained", entry.Name);
        Assert.Null(entry.DirectoryStatus);
        Assert.Null(entry.Size);
        Assert.False(entry.IsDirectory);
        Assert.True(peer.IsDirectory);
    }

    [Fact]
    public void CallerCancellationIsNotConvertedToUnknownMetadata()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var calls = 0;
        var caught = Record.Exception(() => LocalFileSystem.CaptureListedEntry("entry", () => { calls++; return new("entry", false); }, caller.Token));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        Assert.Equal(0, calls);
        var independent = new OperationCanceledException("independent metadata cancellation");
        Assert.Same(independent, Record.Exception(() => LocalFileSystem.CaptureListedEntry("entry", () => throw independent, default)));
    }

    [Fact]
    public void UnexpectedMetadataFailureKeepsItsOriginalIdentity()
    {
        var failure = new InvalidOperationException("metadata provider failed");
        Assert.Same(failure, Record.Exception(() => LocalFileSystem.CaptureListedEntry("entry", () => throw failure, default)));
    }

    [Fact]
    public async Task UnknownDirectoryAndKnownLinkRemainOnSelectedListingWithoutRecursion()
    {
        var unknown = FileEntry.FromMetadata("unknown", null);
        var linked = FileEntry.FromMetadata("link", false, isSymbolicLink: true);
        var selected = new EditorTests.EditorFakeSandbox();
        selected.Listings["/work"] = [new("tree", true)];
        selected.Listings["/work/tree"] = [linked, unknown];
        await using var agent = EditorTests.EditorTestContext.Agent(selected);
        var result = await EditorTests.EditorTestContext.Invoke(Core.SandboxFileEditor.CreateForAgent(), agent, new { command = "view", path = "/work/tree" });
        Assert.EndsWith("link\nunknown\n", result);
        Assert.Equal(new[] { "list:/work", "list:/work/tree" }, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.Empty(selected.Writes);
        Assert.True(linked.IsSymbolicLink);
        Assert.Null(unknown.DirectoryStatus);
        Assert.Equal(0, selected.Disposals);
    }

    [Fact]
    public async Task LegacyJsonAndCustomFilesystemCallsKeepTheirExistingShape()
    {
        var entry = JsonSerializer.Deserialize<FileEntry>("{\"Name\":\"old\",\"IsDirectory\":true,\"IsSymbolicLink\":false}")!;
        Assert.Equal("old", entry.Name);
        Assert.True(entry.IsDirectory);
        Assert.Equal((bool?)true, entry.DirectoryStatus);
        Assert.Null(entry.Size);
        IReadOnlyFileSystem old = new OldFilesystem();
        Assert.Empty(await old.ListFilesAsync(".", default));
        Assert.Empty(await old.ListFilesAsync(".", cancellationToken: default));
    }

    [Fact]
    public async Task OrdinaryLocalListingReportsRealByteSizesAndOrdinalNames()
    {
        using var temporary = new MetadataDirectory();
        File.WriteAllBytes(Path.Combine(temporary.Path, "z.bin"), [0, 255, 17]);
        File.WriteAllBytes(Path.Combine(temporary.Path, "a.bin"), [42]);
        Directory.CreateDirectory(Path.Combine(temporary.Path, "sub"));
        var entries = await new LocalFileSystem().ListFilesAsync(temporary.Path);
        Assert.Equal(new[] { "a.bin", "sub", "z.bin" }, entries.Select(entry => entry.Name));
        Assert.Equal(1L, entries[0].Size);
        Assert.Equal((bool?)false, entries[0].DirectoryStatus);
        Assert.Equal((bool?)true, entries[1].DirectoryStatus);
        Assert.True(entries[1].IsDirectory);
        Assert.Null(entries[1].Size);
        Assert.Equal(3L, entries[2].Size);
        Assert.False(entries[2].IsSymbolicLink);
    }

    private sealed class MetadataDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        internal string Path { get; }
        internal MetadataDirectory()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_temporaryRoot, "strands-metadata-" + Guid.NewGuid().ToString("N")));
            if (!Path.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test directory is outside its selected temporary root.");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("strands-metadata-", StringComparison.Ordinal))
                throw new InvalidOperationException("The test directory is outside its selected temporary root.");
            if (!Directory.Exists(resolved)) return;
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(resolved).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Refusing recursive test cleanup through a reparse point.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class OldFilesystem : IReadOnlyFileSystem
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => new(string.Empty);
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(Array.Empty<FileEntry>());
    }
}
