using System.Runtime.InteropServices;

namespace Strands.Harness.Tests;

public sealed class EnvironmentSandboxTests
{
    [Fact]
    public async Task LocalPlatformIncludesItsOperatingSystemFamily()
    {
        IEnvironmentSandbox sandbox = new LocalFileSandbox();
        var platform = await sandbox.GetPlatformAsync();
        var family = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : null;

        Assert.NotNull(platform);
        if (family is not null) Assert.StartsWith(family + " ", platform, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.OSDescription, platform, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalFilesystemReportsItsOwnEnvironmentAndDirectoryEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-environment-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "instructions");
            IEnvironmentSandbox sandbox = new LocalFileSandbox();

            Assert.Equal(Directory.GetCurrentDirectory(), await sandbox.GetWorkingDirectoryAsync());
            Assert.False(string.IsNullOrWhiteSpace(await sandbox.GetPlatformAsync()));
            var entries = await sandbox.ListFilesAsync(directory);
            Assert.Contains(entries, entry => entry.Name == "AGENTS.md" && !entry.IsDirectory);
            Assert.Contains(entries, entry => entry.Name == "nested" && entry.IsDirectory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task EnvironmentOperationsHonorCancellation()
    {
        IEnvironmentSandbox sandbox = new LocalFileSandbox();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.GetWorkingDirectoryAsync(source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.GetPlatformAsync(source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.ListFilesAsync(Directory.GetCurrentDirectory(), source.Token).AsTask());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/entry")]
    [InlineData("nested\\entry")]
    [InlineData(".")]
    [InlineData("")]
    public void DirectoryEntryNamesCannotChangeTheDiscoveryPath(string name)
    {
        Assert.Throws<ArgumentException>(() => new SandboxEntry(name, true));
    }
}
