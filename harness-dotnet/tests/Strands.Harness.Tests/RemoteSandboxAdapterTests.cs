using System.Runtime.CompilerServices;
using System.Text;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Harness.Tests;

public sealed class RemoteSandboxAdapterTests
{
    [Fact]
    public async Task AdapterUsesOnlyTheSelectedCommandAndByteFilesystem()
    {
        var selected = new SelectedSandbox();
        var adapter = new SandboxAdapter(selected);
        await adapter.WriteTextAsync("/remote/file", "é🧵");
        Assert.Equal("é🧵", await adapter.ReadTextAsync("/remote/file"));
        Assert.Equal(Encoding.UTF8.GetBytes("é🧵"), await adapter.ReadBytesAsync("/remote/file"));
        Assert.Equal("/remote", await adapter.GetWorkingDirectoryAsync());
        Assert.Equal("Linux", await adapter.GetPlatformAsync());
        Assert.Equal("file", Assert.Single(await adapter.ListFilesAsync("/remote")).Name);
        Assert.True(Assert.Single(await ((IReadOnlyFileSystem)adapter).ListFilesAsync("/remote")).IsSymbolicLink);
        var result = await adapter.ExecuteAsync("remote-command", new() { Timeout = TimeSpan.FromSeconds(7), MaxOutputBytes = 123 });
        Assert.Equal(new SandboxExecutionResult("out", "err", 17, true), result);
        Assert.Equal(TimeSpan.FromSeconds(7), selected.Options!.Timeout);
        Assert.Equal(123, selected.Options.MaximumOutputBytes);
        await adapter.DeleteFileAsync("/remote/file");
        Assert.Empty(selected.Files);
    }

    [Fact]
    public void RawConfiguredCoreSandboxExportsBindWithoutHostFallback()
    {
        var selected = new SelectedSandbox();
        var configured = HarnessAgentConfig.Parse("{\"model\":\"provided\",\"sandbox\":{\"module\":\"selected.dll\",\"language\":\"csharp\"},\"builtinTools\":[],\"builtinPlugins\":[],\"skills\":false,\"memory\":false,\"contextManager\":false}");
        var options = configured.ToOptions(new() { ModelResolver = _ => new NoModel(), ModuleResolver = _ => selected });
        Assert.Same(selected, Assert.IsType<SandboxAdapter>(options.Sandbox).Sandbox);
    }

    [Fact]
    public async Task TimeoutTranslationPreservesTheCoreCauseAndDeferredCleanup()
    {
        var cleanup = Task.FromResult<IReadOnlyList<Exception>>(new[] { new IOException("cleanup") });
        var core = new Core.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(124, "partial", ""), new IOException("native cause"));
        core.Data["Strands.CleanupTask"] = cleanup;
        var selected = new SelectedSandbox { Failure = core };
        var caught = await Assert.ThrowsAsync<SandboxTimeoutException>(async () => await new SandboxAdapter(selected).ExecuteAsync("opaque"));
        Assert.Same(core, caught.InnerException);
        Assert.Same(cleanup, caught.Data["Strands.CleanupTask"]);
        Assert.Equal("partial", caught.PartialResult.StandardOutput);
        Assert.Single(await (Task<IReadOnlyList<Exception>>)caught.Data["Strands.CleanupTask"]!);
    }

    [Fact]
    public async Task CancellationPassesThroughWithItsOriginalPartialOutput()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var original = new Core.SandboxAbortException(cancellation.Token, new(-1, "partial", ""));
        var selected = new SelectedSandbox { Failure = original };
        Core.SandboxAbortException? caught = null;
        try { await new SandboxAdapter(selected).ExecuteAsync("opaque"); }
        catch (Core.SandboxAbortException error) { caught = error; }
        Assert.Same(original, caught);
    }

    private sealed class NoModel : Strands.Agents.IModel
    {
        public IAsyncEnumerable<Strands.Agents.ModelStreamEvent> StreamAsync(Strands.Agents.ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class SelectedSandbox : Core.Sandbox
    {
        internal Exception? Failure { get; init; }
        internal Dictionary<string, byte[]> Files { get; } = new();
        internal Core.ExecuteOptions? Options { get; private set; }
        public override async IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.CompletedTask;
            if (Failure is { } failure) throw failure;
            yield return command == "pwd -P" ? new Core.ExecutionResult(0, "/remote\n", "") : command == "uname -s" ? new Core.ExecutionResult(0, "Linux\n", "") : new Core.ExecutionResult(17, "out", "err") { OutputTruncated = true };
        }
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => new((byte[])Files[path].Clone());
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) { Files[path] = (byte[])data.Clone(); return default; }
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) { Files.Remove(path); return default; }
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => new(Encoding.UTF8.GetString(Files[path]));
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => new(new[] { new FileEntry("file", false, true) });
    }
}
