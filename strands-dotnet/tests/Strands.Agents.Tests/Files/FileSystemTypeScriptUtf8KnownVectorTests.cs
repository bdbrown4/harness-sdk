using Strands.Agents.Files;

namespace Strands.Agents.Tests.Files;

public sealed class FileSystemTypeScriptUtf8KnownVectorTests
{
    [Theory]
    [InlineData("E0A080", "\u0800")]
    [InlineData("ED9FBF", "\ud7ff")]
    [InlineData("F0908080", "\ud800\udc00")]
    [InlineData("F48FBFBF", "\udbff\udfff")]
    [InlineData("E28241", "\ufffdA")]
    [InlineData("E2E282AC", "\ufffd\u20ac")]
    [InlineData("C2", "\ufffd")]
    [InlineData("F09080", "\ufffd")]
    [InlineData("EFBBBF41", "A")]
    [InlineData("41EFBBBF", "A\ufeff")]
    [InlineData("EFBB", "\ufffd")]
    public async Task TypeScriptReadPreservesScalarBoundariesAndRecovery(string hex, string expected)
    {
        var selected = new ByteReader(HexBytes(hex));
        using var operation = new CancellationTokenSource();

        var actual = await selected.ReadTextWithProfileAsync("./byte-profile-only", FileTextProfile.TypeScript, operation.Token);

        Assert.Equal(expected, actual);
        Assert.Equal(1, selected.ReadCalls);
        Assert.Equal("./byte-profile-only", selected.ReadPath);
        Assert.Equal(operation.Token, selected.ReadToken);
        Assert.Equal(0, selected.DisposeCalls);
    }

    private static byte[] HexBytes(string value) => Enumerable.Range(0, value.Length / 2)
        .Select(index => Convert.ToByte(value.Substring(index * 2, 2), 16)).ToArray();

    private sealed class ByteReader(byte[] bytes) : IFileSystem, IDisposable
    {
        public int ReadCalls { get; private set; }
        public string? ReadPath { get; private set; }
        public CancellationToken ReadToken { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            ReadPath = path;
            ReadToken = cancellationToken;
            return new((byte[])bytes.Clone());
        }

        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Explicit TypeScript decoding must use the selected byte reader.");

        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This fixture permits no directory operations.");

        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This fixture permits no writes.");

        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This fixture permits no deletes.");

        public void Dispose() => DisposeCalls++;
    }
}
