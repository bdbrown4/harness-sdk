using System.Runtime.ExceptionServices;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryDeletionPrefixTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("x/./y")]
    [InlineData("plain")]
    public async Task DeletionHonorsEveryAcceptedFileRepositoryNamespace(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-repository-delete-prefix-" + Guid.NewGuid().ToString("N"));
        Exception? primary = null;
        try
        {
            var options = new SessionRepositoryOptions { Prefix = prefix };
            var repository = new FileSessionRepository(directory, options);
            await repository.CreateSessionAsync(new("selected"));
            await repository.CreateSessionAsync(new("sibling"));
            Assert.NotNull(await new FileSessionRepository(directory, options).ReadSessionAsync("selected"));

            await repository.DeleteSessionAsync("selected");

            Assert.Null(await new FileSessionRepository(directory, options).ReadSessionAsync("selected"));
            Assert.NotNull(await new FileSessionRepository(directory, options).ReadSessionAsync("sibling"));
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup)
            {
                if (primary is null) primary = cleanup;
                else primary.Data["RepositoryDeletionTest.Cleanup"] = cleanup;
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
