using System.Text;
using Strands.Agents.Files;

namespace Strands.Agents.Tests.Files;

public sealed class FileSystemTextBoundaryTests
{
    [Theory]
    [InlineData("UTF8-01", "C0AF", "\ufffd\ufffd", true)]
    [InlineData("UTF8-02", "EDA080", "\ufffd\ufffd\ufffd", true)]
    [InlineData("UTF8-03", "F4908080", "\ufffd\ufffd\ufffd\ufffd", true)]
    [InlineData("UTF8-04", "E282", "\ufffd", true)]
    [InlineData("UTF8-05", "E228A1", "\ufffd(\ufffd", true)]
    [InlineData("UTF8-06", "FFEFBBBF78", "\ufffd\ufeffx", true)]
    [InlineData("UTF8-07", "EFBBBFEFBBBF78", "\ufeffx", false)]
    public async Task ExplicitProfilesKeepNormativeReplacementAndBomBoundaries(string id, string hex, string expected, bool malformed)
    {
        Assert.StartsWith("UTF8-", id);
        var bytes = HexBytes(hex);
        var selected = new ByteFiles(bytes);
        Assert.Equal(expected, await selected.ReadTextWithProfileAsync("./literal", FileTextProfile.TypeScript));
        Assert.Equal(1, selected.Reads);
        Assert.Empty(selected.Writes);
        Assert.Equal(0, selected.Disposals);
        var python = new ByteFiles(bytes);
        if (malformed)
            await Assert.ThrowsAsync<DecoderFallbackException>(async () => await python.ReadTextWithProfileAsync("./literal", FileTextProfile.Python));
        else
            Assert.Equal("\ufeff\ufeffx", await python.ReadTextWithProfileAsync("./literal", FileTextProfile.Python));
        Assert.Equal(1, python.Reads);
        Assert.Empty(python.Writes);
        Assert.Equal(0, python.Disposals);
    }

    [Fact]
    public async Task InvalidHighSurrogateDoesNotConsumeTheFollowingValidPair()
    {
        var text = new string(new[] { '\ud800', '\ud800', '\udc00' });
        var selected = new ByteFiles([]);
        await selected.WriteTextWithProfileAsync("./literal", text, FileTextProfile.TypeScript);
        Assert.Equal(new byte[] { 0xef, 0xbf, 0xbd, 0xf0, 0x90, 0x80, 0x80 }, Assert.Single(selected.Writes));
        Assert.Equal(0, selected.Reads);
        Assert.Equal(0, selected.Disposals);
        var python = new ByteFiles([]);
        await Assert.ThrowsAsync<EncoderFallbackException>(async () => await python.WriteTextWithProfileAsync("./literal", text, FileTextProfile.Python));
        Assert.Empty(python.Writes);
        Assert.Equal(0, python.Reads);
        Assert.Equal(0, python.Disposals);
    }

    private static byte[] HexBytes(string text) => Enumerable.Range(0, text.Length / 2).Select(index => Convert.ToByte(text.Substring(index * 2, 2), 16)).ToArray();

    private sealed class ByteFiles(byte[] content) : IFileSystem, IDisposable
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        internal List<byte[]> Writes { get; } = [];
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            Assert.Equal("./literal", path);
            Reads++;
            return new((byte[])content.Clone());
        }
        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Assert.Equal("./literal", path);
            Writes.Add((byte[])data.Clone());
            return default;
        }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Explicit profiles cannot fall back to native text.");
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture permits byte callbacks only.");
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture permits byte callbacks only.");
        public void Dispose() => Disposals++;
    }
}
