using System.Text.Json;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxOutputFileTests
{
    [Fact]
    public void OldResultConstructorDeconstructionAndEmptyArtifactsRemainAvailable()
    {
        var result = new Core.ExecutionResult(7, "stdout", "stderr") { OutputTruncated = true };
        var (exit, stdout, stderr) = result;
        Assert.Equal(7, exit);
        Assert.Equal("stdout", stdout);
        Assert.Equal("stderr", stderr);
        Assert.True(result.OutputTruncated);
        Assert.Empty(result.OutputFiles);
    }

    [Fact]
    public void ArtifactCarriesLiteralNameBinaryBytesAndExplicitOrDefaultMime()
    {
        var bytes = new byte[] { 0, 255, 195, 169 };
        var artifact = new Core.OutputFile("../plot-雪.png", bytes, "image/png; custom=literal");
        Assert.Equal("../plot-雪.png", artifact.Name);
        Assert.Equal(bytes, artifact.Content);
        Assert.Equal("image/png; custom=literal", artifact.MimeType);
        Assert.Equal("application/octet-stream", new Core.OutputFile("", Array.Empty<byte>()).MimeType);
    }

    [Fact]
    public void CallerBufferMutationCannotChangeAnArtifact()
    {
        var bytes = new byte[] { 0, 255, 17 };
        var artifact = new Core.OutputFile("bytes.bin", bytes);
        bytes[1] = 42;
        Assert.Equal(new byte[] { 0, 255, 17 }, artifact.Content);
    }

    [Fact]
    public void ReturnedBufferMutationCannotChangeSubsequentArtifactBytes()
    {
        var artifact = new Core.OutputFile("bytes.bin", [0, 255, 17]);
        var exposed = artifact.Content;
        exposed[1] = 42;
        Assert.Equal(new byte[] { 0, 255, 17 }, artifact.Content);
        Assert.NotSame(exposed, artifact.Content);
    }

    [Fact]
    public void ResultSnapshotsArtifactOrderAndExposesOnlyReadOnlyMembership()
    {
        var first = new Core.OutputFile("first", [1]);
        var second = new Core.OutputFile("second", [2]);
        var supplied = new List<Core.OutputFile> { first, second };
        var result = new Core.ExecutionResult(0, "", "") { OutputFiles = supplied };
        supplied.Clear();
        Assert.Equal(new[] { "first", "second" }, result.OutputFiles.Select(file => file.Name));
        if (result.OutputFiles is IList<Core.OutputFile> mutable)
            Assert.Throws<NotSupportedException>(() => mutable.Add(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandAndCodeKeepOriginalArtifactResultAfterPhysicalCleanup(bool executeCode)
    {
        var artifact = new Core.OutputFile("plot.png", [0, 255], "image/png");
        var terminal = new Core.ExecutionResult(3, "out", "err") { OutputFiles = new[] { artifact } };
        var iterator = new ArtifactIterator(terminal);
        var sandbox = new ArtifactSandbox(iterator);
        var operation = (executeCode ? sandbox.ExecuteCodeAsync("fake source", "fake-language") : sandbox.ExecuteAsync("fake command")).AsTask();
        Core.ExecutionResult? result = null;
        Exception? disposalError = null;
        Exception? operationError = null;
        try
        {
            Assert.True(iterator.Entered.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            iterator.Release.TrySetResult(true);
            if (iterator.Disposal is { } disposal) disposalError = await Record.ExceptionAsync(() => disposal);
            operationError = await Record.ExceptionAsync(async () => result = await operation);
        }
        Assert.Null(disposalError);
        Assert.Null(operationError);
        Assert.Same(terminal, result);
        Assert.Same(artifact, Assert.Single(result!.OutputFiles));
        Assert.Equal(new byte[] { 0, 255 }, result.OutputFiles[0].Content);
        Assert.Equal(1, iterator.MoveCalls);
        Assert.Equal(1, iterator.DisposeCalls);
        Assert.Equal(0, sandbox.Disposals);
    }

    [Fact]
    public void ResultWithCopyRetainsArtifactMembershipAndPayload()
    {
        var result = new Core.ExecutionResult(0, "out", "err") { OutputFiles = new[] { new Core.OutputFile("x", [7]) } };
        var copy = result with { OutputTruncated = true };
        Assert.Same(result.OutputFiles, copy.OutputFiles);
        Assert.Equal(new byte[] { 7 }, Assert.Single(copy.OutputFiles).Content);
        Assert.False(result.OutputTruncated);
        Assert.True(copy.OutputTruncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingPartialResultsKeepTheirOriginalArtifacts(bool aborted)
    {
        var result = new Core.ExecutionResult(9, "partial", "error") { OutputFiles = new[] { new Core.OutputFile("partial.bin", [1, 2]) } };
        using var caller = new CancellationTokenSource();
        var observed = aborted ? new Core.SandboxAbortException(caller.Token, result).PartialResult
            : new Core.SandboxTimeoutException(TimeSpan.FromSeconds(1), result).PartialResult;
        Assert.Same(result, observed);
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(observed.OutputFiles).Content);
    }

    [Fact]
    public void NullArtifactInputsAreRejectedWithoutChangingLiteralDataPolicy()
    {
        Assert.Throws<ArgumentNullException>(() => new Core.OutputFile("name", null!));
        Assert.Throws<ArgumentNullException>(() => new Core.OutputFile(null!, [1]));
        Assert.Throws<ArgumentNullException>(() => new Core.OutputFile("name", [1], null!));
        Assert.Throws<ArgumentNullException>(() => new Core.ExecutionResult(0, "", "") { OutputFiles = null! });
        Assert.Throws<ArgumentException>(() => new Core.ExecutionResult(0, "", "") { OutputFiles = new Core.OutputFile[] { null! } });
        Assert.Equal("", new Core.OutputFile("", [1], "").MimeType);
    }

    [Fact]
    public void NativeResultJsonRoundtripPreservesArtifactFieldsAndBytes()
    {
        var original = new Core.ExecutionResult(4, "out", "err") { OutputTruncated = true, OutputFiles = new[] { new Core.OutputFile("plot.png", [0, 255], "image/png") } };
        var restored = JsonSerializer.Deserialize<Core.ExecutionResult>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original.ExitCode, restored.ExitCode);
        Assert.Equal(original.Stdout, restored.Stdout);
        Assert.Equal(original.Stderr, restored.Stderr);
        Assert.True(restored.OutputTruncated);
        var file = Assert.Single(restored.OutputFiles);
        Assert.Equal("plot.png", file.Name);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal(new byte[] { 0, 255 }, file.Content);
    }

    private sealed class ArtifactIterator(Core.ExecutionResult terminal) : IAsyncEnumerable<Core.SandboxStreamEvent>, IAsyncEnumerator<Core.SandboxStreamEvent>
    {
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Disposal;
        internal int MoveCalls;
        internal int DisposeCalls;
        public Core.SandboxStreamEvent Current => terminal;
        public IAsyncEnumerator<Core.SandboxStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            MoveCalls++;
            if (MoveCalls != 1) throw new InvalidOperationException("No additional MoveNext belongs after the artifact terminal.");
            return new(true);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; Disposal = CloseAsync(); return new(Disposal); }
        private async Task CloseAsync() { Entered.TrySetResult(true); await Release.Task; }
    }

    private sealed class ArtifactSandbox(ArtifactIterator iterator) : Core.Sandbox, IDisposable
    {
        internal int Disposals;
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => iterator;
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => iterator;
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem call belongs to artifact tests.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem call belongs to artifact tests.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem call belongs to artifact tests.");
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem call belongs to artifact tests.");
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No filesystem call belongs to artifact tests.");
        public void Dispose() => Disposals++;
    }
}
