using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnboundDeletionClearsContextOnlyOnTheExactRawConstructorBackend(bool callerNamespaced)
    {
        var root = new InMemoryStorage(); IStorage constructor = callerNamespaced ? root.Namespace("tenant") : root;
        var catalog = callerNamespaced ? constructor : constructor.Namespace("session");
        await catalog.WriteAsync("selected/scopes/agent/archived/snapshots/corrupt.json", [0xff]);
        await constructor.WriteAsync("context/selected/scopes/agent/archived/original", [1]);
        await constructor.WriteAsync("context/other/keep", [2]); await root.WriteAsync("outside/keep", [3]);
        var manager = new SnapshotSessionManager(constructor, "selected");
        await manager.DeleteSessionAsync();
        Assert.Empty(await catalog.ListAsync("selected/"));
        Assert.Null(await constructor.ReadAsync("context/selected/scopes/agent/archived/original"));
        Assert.Equal(new byte[] { 2 }, await constructor.ReadAsync("context/other/keep"));
        Assert.Equal(new byte[] { 3 }, await root.ReadAsync("outside/keep"));
    }
}
