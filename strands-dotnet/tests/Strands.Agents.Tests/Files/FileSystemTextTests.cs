using System.Text;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Files;

public sealed class FileSystemTextTests
{
    [Theory]
    [InlineData("TXT-01", "é🧵", "w6nwn6e1")]
    [InlineData("TXT-02", "", "")]
    public async Task NativeWritesUseOnlyExactSelectedBytes(string id, string text, string expectedBase64)
    {
        Assert.StartsWith("TXT-", id);
        var files = new TextFiles();
        using var caller = new CancellationTokenSource();
        await files.WriteTextAsync("relative\\literal", text, caller.Token);
        Assert.Equal(Convert.FromBase64String(expectedBase64), Assert.Single(files.Writes));
        AssertCall(files, "write", "relative\\literal", caller.Token);
        Assert.Equal(0, files.Disposals);
    }

    [Theory]
    [InlineData("TXT-03", FileTextProfile.TypeScript)]
    [InlineData("TXT-04", FileTextProfile.Python)]
    public async Task ExplicitWriteProfilesDistinguishInvalidScalarHandling(string id, FileTextProfile profile)
    {
        Assert.StartsWith("TXT-", id);
        var files = new TextFiles();
        if (profile == FileTextProfile.TypeScript)
        {
            await files.WriteTextWithProfileAsync("p", "\ud800", profile);
            Assert.Equal(new byte[] { 0xef, 0xbf, 0xbd }, Assert.Single(files.Writes));
        }
        else
        {
            await Assert.ThrowsAsync<EncoderFallbackException>(async () => await files.WriteTextWithProfileAsync("p", "\ud800", profile));
            Assert.Empty(files.Calls);
            Assert.Empty(files.Writes);
        }
    }

    [Theory]
    [InlineData("TXT-05", FileTextProfile.TypeScript)]
    [InlineData("TXT-06", FileTextProfile.Python)]
    public async Task ExplicitReadProfilesRetainTheirBomBehavior(string id, FileTextProfile profile)
    {
        Assert.StartsWith("TXT-", id);
        var files = new TextFiles { Bytes = profile == FileTextProfile.TypeScript ? [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, 0x78] : [0xef, 0xbb, 0xbf, 0x78] };
        using var caller = new CancellationTokenSource();
        Assert.Equal("\ufeffx", await files.ReadTextWithProfileAsync("p", profile, caller.Token));
        AssertCall(files, "bytes", "p", caller.Token);
    }

    [Theory]
    [InlineData("TXT-07", FileTextProfile.TypeScript)]
    [InlineData("TXT-08", FileTextProfile.Python)]
    public async Task ExplicitReadProfilesDistinguishMalformedUtf8(string id, FileTextProfile profile)
    {
        Assert.StartsWith("TXT-", id);
        var files = new TextFiles { Bytes = [0xff, 0x41] };
        if (profile == FileTextProfile.TypeScript)
            Assert.Equal("\ufffdA", await files.ReadTextWithProfileAsync("p", profile));
        else
            await Assert.ThrowsAsync<DecoderFallbackException>(async () => await files.ReadTextWithProfileAsync("p", profile));
        AssertCall(files, "bytes", "p", default);
    }

    [Theory]
    [InlineData("TXT-09", false)]
    [InlineData("TXT-10", true)]
    public async Task BclEncodingUsesExactSelectedByteCapability(string id, bool write)
    {
        Assert.StartsWith("TXT-", id);
        var files = new TextFiles { Bytes = [0xe9] };
        var encoding = Encoding.GetEncoding(28591);
        if (write)
        {
            await files.WriteTextWithEncodingAsync("p", "é", encoding);
            Assert.Equal(new byte[] { 0xe9 }, Assert.Single(files.Writes));
            AssertCall(files, "write", "p", default);
        }
        else
        {
            Assert.Equal("é", await files.ReadTextWithEncodingAsync("p", encoding));
            AssertCall(files, "bytes", "p", default);
        }
    }

    [Fact]
    public async Task NullEncodingRejectsBeforeSelectedCallbacks()
    {
        var files = new TextFiles();
        var error = await Assert.ThrowsAsync<ArgumentNullException>(async () => await files.ReadTextWithEncodingAsync("p", null!));
        Assert.Equal("encoding", error.ParamName);
        Assert.Empty(files.Calls);
    }

    [Fact]
    public async Task UnknownProfileRejectsBeforeSelectedCallbacks()
    {
        var files = new TextFiles();
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await files.ReadTextWithProfileAsync("p", (FileTextProfile)99));
        Assert.Equal("profile", error.ParamName);
        Assert.Empty(files.Calls);
    }

    [Fact]
    public async Task OriginalRequestedCancellationPreemptsCodecAndBackend()
    {
        var files = new TextFiles();
        var encoding = new CountingEncoding();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await files.WriteTextWithEncodingAsync("p", "x", encoding, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, encoding.Callbacks);
        Assert.Empty(files.Calls);
    }

    [Fact]
    public async Task AcceptedWritePhysicallyJoinsEvenWhenBackendIgnoresLateCancellation()
    {
        var files = new TextFiles { HoldWrite = true };
        using var caller = new CancellationTokenSource();
        var operation = files.WriteTextAsync("p", "x", caller.Token).AsTask();
        Exception? observed = null;
        try
        {
            Assert.True(files.WriteEntered.Task.IsCompleted);
            caller.Cancel();
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            files.ReleaseWrite.TrySetResult(true);
            observed = await Record.ExceptionAsync(() => operation);
            if (files.WriteEntered.Task.IsCompleted) await files.WriteExited.Task;
        }
        Assert.Null(observed);
        Assert.Equal(new byte[] { 0x78 }, Assert.Single(files.Writes));
        AssertCall(files, "write", "p", caller.Token);
    }

    [Fact]
    public async Task EncodingConfigurationIsCapturedBeforeHeldRead()
    {
        var files = new TextFiles { Bytes = [0xff], HoldRead = true };
        var encoding = (Encoding)new UTF8Encoding(false, false).Clone();
        encoding.DecoderFallback = new DecoderReplacementFallback("?");
        var operation = files.ReadTextWithEncodingAsync("p", encoding).AsTask();
        string? result = null;
        Exception? observed = null;
        try
        {
            Assert.True(files.ReadEntered.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
            encoding.DecoderFallback = new DecoderReplacementFallback("!");
        }
        finally
        {
            files.ReleaseRead.TrySetResult(true);
            observed = await Record.ExceptionAsync(async () => result = await operation);
            if (files.ReadEntered.Task.IsCompleted) await files.ReadExited.Task;
        }
        Assert.Null(observed);
        Assert.Equal("?", result);
        AssertCall(files, "bytes", "p", default);
    }

    [Fact]
    public async Task IndependentSelectedFailureRetainsIdentityWithoutFallback()
    {
        var failure = new IOException("selected bytes failed");
        var files = new TextFiles { ReadFailure = failure };
        var caught = await Record.ExceptionAsync(async () => await files.ReadTextWithProfileAsync("p", FileTextProfile.TypeScript));
        Assert.Same(failure, caught);
        AssertCall(files, "bytes", "p", default);
        Assert.Equal(0, files.Disposals);
    }

    [Fact]
    public async Task ExplicitNativeReadDelegatesToSelectedNativePolicy()
    {
        var files = new TextFiles { NativeText = "\ufeffnative" };
        using var caller = new CancellationTokenSource();
        Assert.Equal("\ufeffnative", await files.ReadTextWithProfileAsync("relative\\literal", FileTextProfile.Native, caller.Token));
        AssertCall(files, "native", "relative\\literal", caller.Token);
    }

    [Fact]
    public async Task ExistingCallShapesAndDefaultArgumentsRemainUnambiguous()
    {
        IFileSystem files = new TextFiles { NativeText = "native" };
        Assert.Equal("native", await files.ReadTextAsync("p"));
        Assert.Equal("native", await files.ReadTextAsync("p", CancellationToken.None));
        Assert.Equal("native", await files.ReadTextAsync("p", default));
        Assert.Equal("native", await files.ReadTextAsync("p", cancellationToken: default));
        var sandbox = new OldSandbox(files);
        Assert.Equal("native", await sandbox.ReadTextAsync("p", default));
        Func<ValueTask> nativeWrite = () => files.WriteTextAsync("p", "x", default);
        Func<ValueTask<string>> nativeProfile = () => files.ReadTextWithProfileAsync("p", default);
        Func<ValueTask<string>> nullEncoding = () => files.ReadTextWithEncodingAsync("p", default!);
        Assert.NotNull(nativeWrite);
        Assert.NotNull(nativeProfile);
        Assert.NotNull(nullEncoding);
        Assert.Equal(new[] { "native", "native", "native", "native", "native" }, ((TextFiles)files).Calls.Select(call => call.Operation));
    }

    private static void AssertCall(TextFiles files, string operation, string path, CancellationToken token)
    {
        var call = Assert.Single(files.Calls);
        Assert.Equal(operation, call.Operation);
        Assert.Equal(path, call.Path);
        Assert.Equal(token, call.Token);
    }

    private sealed class CountingEncoding : UTF8Encoding
    {
        internal int Callbacks;
        public override object Clone() { Callbacks++; return base.Clone(); }
        public override byte[] GetBytes(string s) { Callbacks++; return base.GetBytes(s); }
    }

    private sealed class TextFiles : IFileSystem, IDisposable
    {
        internal byte[] Bytes = [];
        internal string NativeText = "native";
        internal bool HoldRead;
        internal bool HoldWrite;
        internal Exception? ReadFailure;
        internal int Disposals;
        internal readonly List<(string Operation, string Path, CancellationToken Token)> Calls = [];
        internal readonly List<byte[]> Writes = [];
        internal readonly TaskCompletionSource<bool> ReadEntered = Signal();
        internal readonly TaskCompletionSource<bool> ReadExited = Signal();
        internal readonly TaskCompletionSource<bool> ReleaseRead = Signal();
        internal readonly TaskCompletionSource<bool> WriteEntered = Signal();
        internal readonly TaskCompletionSource<bool> WriteExited = Signal();
        internal readonly TaskCompletionSource<bool> ReleaseWrite = Signal();
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Calls.Add(("native", path, cancellationToken)); return new(NativeText); }
        public async ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(("bytes", path, cancellationToken));
            ReadEntered.TrySetResult(true);
            try
            {
                if (HoldRead) await ReleaseRead.Task;
                if (ReadFailure is not null) throw ReadFailure;
                return (byte[])Bytes.Clone();
            }
            finally { ReadExited.TrySetResult(true); }
        }
        public async ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Calls.Add(("write", path, cancellationToken));
            Writes.Add((byte[])data.Clone());
            WriteEntered.TrySetResult(true);
            try { if (HoldWrite) await ReleaseWrite.Task; }
            finally { WriteExited.TrySetResult(true); }
        }
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Text conveniences cannot delete files.");
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Text conveniences cannot enumerate files.");
        public void Dispose() => Disposals++;
    }

    private sealed class OldSandbox(IFileSystem files) : Core.Sandbox
    {
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => files.ReadTextAsync(path, cancellationToken);
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => files.ReadBytesAsync(path, cancellationToken);
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => files.WriteBytesAsync(path, data, cancellationToken);
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => files.DeleteFileAsync(path, cancellationToken);
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => files.ListFilesAsync(path, cancellationToken);
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No command, model or interpreter belongs to text convenience tests.");
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
