using System.Runtime.CompilerServices;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Files;

public sealed class FileRemovalTests
{
    [Fact]
    public async Task UnsupportedLegacyFilesystemRejectsWithoutFallbackAndKeepsDeleteCallable()
    {
        var selected = new LegacyFilesystem();
        IFileSystem files = selected;
        await Assert.ThrowsAsync<NotSupportedException>(async () => await files.RemoveFileAsync("literal", default));
        Assert.Empty(selected.LegacyCalls);
        await files.DeleteFileAsync("absent", cancellationToken: default);
        Assert.Equal(new[] { "delete:absent" }, selected.LegacyCalls);
    }

    [Fact]
    public async Task SelectedCapabilityReceivesLiteralPathAndTokenAndPhysicallyJoins()
    {
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var selected = new StrictFilesystem
        {
            Remove = async (_, _) => { entered.TrySetResult(true); await release.Task; },
        };
        IFileSystem files = selected;
        Task? pending = null;
        try
        {
            pending = files.RemoveFileAsync("-literal 'name'\nnext", caller.Token).AsTask();
            Assert.Equal(1, selected.RemoveCalls);
            await Bounded(entered.Task);
            Assert.False(pending.IsCompleted);
            Assert.Equal("-literal 'name'\nnext", selected.LastPath);
            Assert.Equal(caller.Token, selected.LastToken);
            release.TrySetResult(true);
            await Bounded(pending);
            Assert.Empty(selected.LegacyCalls);
            Assert.Equal(0, selected.Disposals);
        }
        finally
        {
            release.TrySetResult(true);
            if (pending is not null) await Bounded(pending);
        }
    }

    [Fact]
    public async Task RequestedCancellationPreventsCapabilityAndLegacyCalls()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var selected = new StrictFilesystem();
        IFileSystem files = selected;
        var error = await Record.ExceptionAsync(async () => await files.RemoveFileAsync("literal", caller.Token));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        Assert.Equal(0, selected.RemoveCalls);
        Assert.Empty(selected.LegacyCalls);
        Assert.Equal(0, selected.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedFailureKeepsOriginalExceptionIdentity(bool independentCancellation)
    {
        using var independent = new CancellationTokenSource();
        Exception failure = independentCancellation
            ? new OperationCanceledException("selected independent cancellation", independent.Token)
            : new IOException("selected removal denied");
        var selected = new StrictFilesystem { Remove = (_, _) => throw failure };
        IFileSystem files = selected;
        Assert.Same(failure, await Record.ExceptionAsync(async () => await files.RemoveFileAsync("literal")));
        Assert.Equal(1, selected.RemoveCalls);
        if (failure is OperationCanceledException cancellation) Assert.Equal(independent.Token, cancellation.CancellationToken);
        Assert.Empty(selected.LegacyCalls);
        Assert.Equal(0, selected.Disposals);
    }

    [Fact]
    public async Task LocalRemovalDeletesOnlyTheNamedOwnedFile()
    {
        using var temporary = new OwnedDirectory();
        var removed = Path.Combine(temporary.Path, "removed.bin");
        var sibling = Path.Combine(temporary.Path, "sibling.bin");
        File.WriteAllBytes(removed, [1, 2, 3]);
        File.WriteAllBytes(sibling, [255, 0, 17]);
        IFileSystem files = new LocalFileSystem();
        await files.RemoveFileAsync(removed);
        Assert.False(File.Exists(removed));
        Assert.Equal(new byte[] { 255, 0, 17 }, File.ReadAllBytes(sibling));
    }

    [Fact]
    public async Task MissingLocalLeafFailsWhileLegacyDeleteRemainsIdempotent()
    {
        using var temporary = new OwnedDirectory();
        var missing = Path.Combine(temporary.Path, "absent.bin");
        IFileSystem files = new LocalFileSystem();
        await Assert.ThrowsAnyAsync<IOException>(async () => await files.RemoveFileAsync(missing));
        await files.DeleteFileAsync(missing);
        await files.DeleteFileAsync(missing, cancellationToken: default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task MissingLocalParentFailsWithoutCreatingDirectories()
    {
        using var temporary = new OwnedDirectory();
        var parent = Path.Combine(temporary.Path, "absent-parent");
        IFileSystem files = new LocalFileSystem();
        await Assert.ThrowsAnyAsync<IOException>(async () => await files.RemoveFileAsync(Path.Combine(parent, "absent.bin")));
        Assert.False(Directory.Exists(parent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task LocalSandboxRemovalUsesOnlyItsFilesystemCapability()
    {
        using var temporary = new OwnedDirectory();
        var removed = Path.Combine(temporary.Path, "removed.bin");
        File.WriteAllBytes(removed, [42]);
        var selected = new Core.NotASandboxLocalEnvironment();
        IFileSystem files = selected;
        await files.RemoveFileAsync(removed);
        Assert.False(File.Exists(removed));
        await Assert.ThrowsAnyAsync<IOException>(async () => await files.RemoveFileAsync(removed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public async Task PosixRemovalUsesLiteralSelectedTransportAndRetainsFailure(int exitCode)
    {
        var selected = new ResponseSandbox(new(exitCode, "", "selected removal failure"));
        IFileSystem files = selected;
        var error = await Record.ExceptionAsync(async () => await files.RemoveFileAsync("-file 'quote'\nnext"));
        if (exitCode == 0) Assert.Null(error);
        else Assert.Contains("selected removal failure", Assert.IsAssignableFrom<IOException>(error).Message);
        Assert.Equal("rm './-file '\\''quote'\\''\nnext'", selected.Command);
        Assert.Equal(1, selected.Calls);
        Assert.Equal(1, selected.Disposals);
    }

    [Fact]
    public async Task PosixRemovalRejectsTruncatedSuccessfulCapture()
    {
        var selected = new ResponseSandbox(new(0, "", "") { OutputTruncated = true });
        IFileSystem files = selected;
        var error = await Assert.ThrowsAsync<IOException>(async () => await files.RemoveFileAsync("literal"));
        Assert.Contains("truncated", error.Message);
        Assert.Equal("rm './literal'", selected.Command);
        Assert.Equal(1, selected.Calls);
        Assert.Equal(1, selected.Disposals);
    }

    private class LegacyFilesystem : IFileSystem
    {
        internal List<string> LegacyCalls { get; } = new();
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { LegacyCalls.Add("text:" + path); return new(""); }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) { LegacyCalls.Add("list:" + path); return new(Array.Empty<FileEntry>()); }
        public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) { LegacyCalls.Add("bytes:" + path); return new(Array.Empty<byte>()); }
        public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) { LegacyCalls.Add("write:" + path); return default; }
        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) { LegacyCalls.Add("delete:" + path); return default; }
    }

    private sealed class StrictFilesystem : LegacyFilesystem, IStrictFileRemover, IDisposable
    {
        internal Func<string, CancellationToken, Task> Remove { get; init; } = (_, _) => Task.CompletedTask;
        internal int RemoveCalls { get; private set; }
        internal string? LastPath { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask RemoveFileAsync(string path, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            LastPath = path;
            LastToken = cancellationToken;
            return new(Remove(path, cancellationToken));
        }
        public void Dispose() => Disposals++;
    }

    private sealed class ResponseSandbox(Core.ExecutionResult response) : Core.PosixShellSandbox
    {
        internal string? Command { get; private set; }
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        public override async IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Command = command;
            try { await Task.CompletedTask; yield return response; }
            finally { Disposals++; }
        }
    }

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        internal string Path { get; }
        internal OwnedDirectory()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_temporaryRoot, "strands-remove-" + Guid.NewGuid().ToString("N")));
            if (!Path.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The test directory is outside its selected temporary root.");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("strands-remove-", StringComparison.Ordinal))
                throw new InvalidOperationException("The test directory is outside its selected temporary root.");
            if (!Directory.Exists(resolved)) return;
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(resolved).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Refusing recursive test cleanup through a reparse point.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Bounded(Task operation)
    {
        if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10))) != operation) throw new TimeoutException("Selected removal did not physically settle within the test deadline.");
        await operation;
    }
}
