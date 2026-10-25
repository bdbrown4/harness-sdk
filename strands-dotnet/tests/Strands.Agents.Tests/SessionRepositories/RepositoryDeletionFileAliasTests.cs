using System.Runtime.ExceptionServices;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryDeletionFileAliasTests
{
    [Theory]
    [InlineData("", ".hidden|alpha|x/.inner|x/y|x/yellow")]
    [InlineData("./", "./.hidden|./alpha|./x/.inner|./x/y|./x/yellow")]
    [InlineData("x/./", "x/./.inner|x/./y|x/./yellow")]
    [InlineData("x/./y", "x/./y|x/./yellow")]
    [InlineData("././", "././.hidden|././alpha|././x/.inner|././x/y|././x/yellow")]
    [InlineData(".", ".hidden")]
    [InlineData("x/.", "x/.inner")]
    [InlineData(".hidden", ".hidden")]
    public Task FileListingProjectsCompletedDotAliasesButPreservesPartialDotPrefixes(string prefix, string expected)
        => WithStorage(async storage =>
        {
            foreach (var key in new[] { ".hidden", "alpha", "x/.inner", "x/y", "x/yellow" }) await storage.WriteAsync(key, [1]);
            Assert.Equal(expected.Split('|'), await storage.ListAsync(prefix));
        });

    [Theory]
    [InlineData("./alpha", "alpha")]
    [InlineData("x/./y", "x/y")]
    public Task FileOperationsShareTheSameCompletedDotAlias(string alias, string physical)
        => WithStorage(async storage =>
        {
            await storage.WriteAsync(alias, [4, 8]);
            Assert.Equal(new byte[] { 4, 8 }, await storage.ReadAsync(physical));
            Assert.Equal(new byte[] { 4, 8 }, await storage.ReadAsync(alias));
            await storage.DeleteAsync(alias);
            Assert.Null(await storage.ReadAsync(physical));
        });

    [Theory]
    [InlineData("../escape")]
    [InlineData("x/../../escape")]
    [InlineData("escape\\path")]
    public Task ParentAndBackslashKeysRemainRejectedBeforeFilesystemMutation(string key)
        => WithStorage(async storage =>
        {
            await Assert.ThrowsAsync<StorageException>(async () => await storage.WriteAsync(key, [1]));
            Assert.Empty(await storage.ListAsync());
        });

    private static async Task WithStorage(Func<LocalFileStorage, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-file-dot-alias-" + Guid.NewGuid().ToString("N"));
        Exception? primary = null;
        try { await body(new LocalFileStorage(directory)); }
        catch (Exception error) { primary = error; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup)
            { if (primary is null) primary = cleanup; else primary.Data["RepositoryDeletionTest.Cleanup"] = cleanup; }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
