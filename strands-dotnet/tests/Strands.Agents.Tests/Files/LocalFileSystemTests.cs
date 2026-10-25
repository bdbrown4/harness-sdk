using Strands.Agents.Files;

namespace Strands.Agents.Tests.Files;

public sealed class LocalFileSystemTests
{
    [Fact]
    public async Task ByteOperationsCreateDirectoriesPreserveOpaqueDataAndOwnTheirBuffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands binary files " + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new LocalFileSystem();
            var path = Path.Combine(root, "nested", "opaque.bin");
            byte[] data = [0xff, 0x00, 0xc3, 0x28, 0x7f];

            var writing = files.WriteBytesAsync(path, data).AsTask();
            data[0] = 0;
            await writing;
            var first = await files.ReadBytesAsync(path);

            Assert.Equal(new byte[] { 0xff, 0x00, 0xc3, 0x28, 0x7f }, first);
            first[0] = 0;
            Assert.Equal(0xff, (await files.ReadBytesAsync(path))[0]);
            await files.DeleteFileAsync(path);
            await files.DeleteFileAsync(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => files.ReadBytesAsync(path).AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CanceledByteOverwritePreservesTheExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands canceled binary files " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new LocalFileSystem();
            var path = Path.Combine(root, "existing.bin");
            await File.WriteAllBytesAsync(path, [0xff, 0, 42]);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.WriteBytesAsync(path, [1, 2, 3], canceled.Token).AsTask());

            Assert.Equal(new byte[] { 0xff, 0, 42 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(new[] { "existing.bin" }, Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ByteReadsRetainBomAndAreIndependentOfTheTextReadLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "strands large binary files " + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new LocalFileSystem(maxReadBytes: 1);
            var path = Path.Combine(root, "large.bin");
            var bytes = new byte[6 * 1024 * 1024];
            bytes[0] = 0xef; bytes[1] = 0xbb; bytes[2] = 0xbf; bytes[bytes.Length - 1] = 0xff;

            await files.WriteBytesAsync(path, bytes);

            Assert.Equal(bytes, await files.ReadBytesAsync(path));
            await Assert.ThrowsAsync<IOException>(() => files.ReadTextAsync(path).AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
