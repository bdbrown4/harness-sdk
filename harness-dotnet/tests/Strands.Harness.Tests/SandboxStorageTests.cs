using Strands.Agents.Files;
using Strands.Agents.Storage;
using Strands.Harness.Storage;

namespace Strands.Harness.Tests;

public sealed class SandboxStorageTests
{
    [Fact]
    public async Task RemoteStoragePreservesOpaqueBytesNamespacesAndSortedPrefixesWithoutTextOrHostIo()
    {
        var files = new RemoteFiles();
        var storage = new SandboxStorage(files, "/remote/.agent/memory");
        var nested = storage.Namespace("tenant").Namespace("user");
        byte[] data = [0xff, 0, 0xc3, 0x28];

        var write = nested.WriteAsync("//topics//first.bin/", data).AsTask();
        data[0] = 0;
        await write;
        await storage.WriteAsync("a.bin", [42]);
        var read = await nested.ReadAsync("topics/first.bin");

        Assert.Equal(new byte[] { 0xff, 0, 0xc3, 0x28 }, read);
        read![0] = 0;
        Assert.Equal(0xff, (await nested.ReadAsync("topics/first.bin"))![0]);
        Assert.Equal(new[] { "a.bin", "tenant/user/topics/first.bin" }, await storage.ListAsync());
        Assert.Equal(new[] { "topics/first.bin" }, await nested.ListAsync("topics/"));
        Assert.Null(await storage.ReadAsync("missing.bin"));
        await nested.DeleteAsync("topics/first.bin");
        await nested.DeleteAsync("topics/first.bin");
        Assert.Equal(new[] { "a.bin" }, await storage.ListAsync());
        Assert.All(files.Accesses, path => Assert.StartsWith("/remote/.agent/memory", path, StringComparison.Ordinal));
        Assert.Equal(0, files.TextReads);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("tenant/../../escape")]
    [InlineData("tenant\\escape")]
    [InlineData("")]
    public async Task RejectsInvalidKeysBeforeCallingTheBackend(string key)
    {
        var files = new RemoteFiles();
        var storage = new SandboxStorage(files, "/remote");

        await Assert.ThrowsAsync<StorageException>(() => storage.WriteAsync(key, [1]).AsTask());

        Assert.Empty(files.Accesses);
    }

    [Fact]
    public async Task CancellationDoesNotWriteAndLinkEntriesAreNotTraversed()
    {
        var files = new RemoteFiles();
        var storage = new SandboxStorage(files, "/remote");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync("one.bin", [1], canceled.Token).AsTask());
        Assert.Empty(files.Accesses);
        files.LinkDirectory = "/remote/loop";

        await Assert.ThrowsAsync<StorageException>(() => storage.ListAsync().AsTask());

        Assert.DoesNotContain("/remote/loop", files.Accesses);
    }

    internal sealed class RemoteFiles : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public List<string> Accesses { get; } = [];
        public int TextReads { get; private set; }
        public string? LinkDirectory { get; set; }
        public async Task<string> ReadTextForAssertionAsync(string path) => System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(path));
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            TextReads++; throw new InvalidOperationException("Opaque storage cannot use text conversion.");
        }
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Accesses.Add(path);
            return _files.TryGetValue(path, out var data) ? new((byte[])data.Clone()) : throw new FileNotFoundException("Missing remote file.", path);
        }
        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Accesses.Add(path); _files[path] = (byte[])data.Clone(); return default;
        }
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Accesses.Add(path); _files.Remove(path); return default;
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Accesses.Add(path);
            var prefix = path.TrimEnd('/') + "/";
            var entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            foreach (var file in _files.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var relative = file.Substring(prefix.Length);
                var slash = relative.IndexOf('/');
                var name = slash < 0 ? relative : relative.Substring(0, slash);
                entries[name] = new(name, slash >= 0);
            }
            if (LinkDirectory is { } link && link.StartsWith(prefix, StringComparison.Ordinal) && link.Substring(prefix.Length).IndexOf('/') < 0)
                entries[link.Substring(prefix.Length)] = new(link.Substring(prefix.Length), true, isSymbolicLink: true);
            return new((IReadOnlyList<FileEntry>)entries.Values.Reverse().ToArray());
        }
    }
}
