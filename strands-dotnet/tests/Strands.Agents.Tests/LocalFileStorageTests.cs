using System.Text;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "strands-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValuesPersistAcrossInstancesWithNestedKeysAndAtomicReplacement()
    {
        var writer = new LocalFileStorage(_directory);
        Assert.False(Directory.Exists(_directory));
        await writer.WriteAsync("//team///note/", [0, 255, 10]);
        var reader = new LocalFileStorage(_directory);
        Assert.Equal([0, 255, 10], await reader.ReadAsync("team/note"));
        await writer.WriteAsync("team/note", [9]);
        Assert.Equal([9], await reader.ReadAsync("team/note"));
        Assert.Equal(["team/note"], await reader.ListAsync());
        Assert.Empty(Directory.GetFiles(_directory, "*.__strands_tmp*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingValuesAreAbsentAndPrefixesDoNotIncludeSiblingDirectories()
    {
        var storage = new LocalFileStorage(_directory);
        Assert.Null(await storage.ReadAsync("missing"));
        await storage.DeleteAsync("missing");
        Assert.Empty(await storage.ListAsync());
        await storage.WriteAsync("team/z", Encoding.UTF8.GetBytes("apple"));
        await storage.WriteAsync("team/a", Encoding.UTF8.GetBytes("apple pear"));
        await storage.WriteAsync("team-other/b", [3]);
        Assert.Equal(["team/a", "team/z"], await storage.ListAsync("//team///"));
        Assert.Equal(["a", "z"], await storage.Namespace("team/").ListAsync());
        Assert.Equal(["team/a", "team/z"], (await storage.SearchAsync("apple pear")).Select(result => result.Key));
        await storage.DeleteAsync("team/a");
        Assert.Null(await storage.ReadAsync("team/a"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("a\\escape")]
    public async Task TraversalIsRejectedBeforeFilesystemAccess(string key)
    {
        var storage = new LocalFileStorage(_directory);
        await Assert.ThrowsAsync<StorageException>(() => storage.WriteAsync(key, []).AsTask());
        await Assert.ThrowsAsync<StorageException>(() => storage.ReadAsync(key).AsTask());
        await Assert.ThrowsAsync<StorageException>(() => storage.DeleteAsync(key).AsTask());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task CancelledWritesLeaveThePreviousValueAndNoTemporaryFile()
    {
        var storage = new LocalFileStorage(_directory);
        await storage.WriteAsync("note", [1]);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync("note", [2], source.Token).AsTask());
        Assert.Equal([1], await storage.ReadAsync("note"));
        Assert.Equal(["note"], await storage.ListAsync());
    }

    [Fact]
    public async Task AValidLongFileNameCanBeCreatedAndAtomicallyReplaced()
    {
#if NETFRAMEWORK
        var nameLength = 245 - _directory.Length - 1;
#else
        const int nameLength = 245;
#endif
        Assert.InRange(nameLength, 1, 245);
        await AssertValidDestinationCanBeStoredAsync(new string('v', nameLength));
    }

    [Fact]
    public async Task AValidDeepParentCanBeCreatedAndAtomicallyReplaced()
    {
        var paddingLength = 220 - _directory.Length - 1;
        Assert.InRange(paddingLength, 1, 200);
        var parent = Path.Combine(_directory, new string('d', paddingLength));
        Assert.Equal(220, parent.Length);
        var key = parent.Substring(_directory.Length + 1).Replace(Path.DirectorySeparatorChar, '/') + "/value";
        await AssertValidDestinationCanBeStoredAsync(key);
    }

    private async Task AssertValidDestinationCanBeStoredAsync(string key)
    {
        var path = Path.Combine(_directory, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2]);
        Assert.Equal([1, 2], File.ReadAllBytes(path));
        File.Delete(path);

        var storage = new LocalFileStorage(_directory);
        await storage.WriteAsync(key, [3, 4]);
        Assert.Equal([3, 4], await storage.ReadAsync(key));
        Assert.Equal([key], await storage.ListAsync());
        await storage.WriteAsync(key, [5, 6, 7]);
        Assert.Equal([5, 6, 7], await storage.ReadAsync(key));
        Assert.Equal([key], await storage.ListAsync());
        Assert.Equal([path], Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
