using System.Runtime.ExceptionServices;

namespace Strands.Agents.Tests;

public sealed partial class SessionDeletionTests
{
    [Fact]
    public Task MissingDirectoryDeletionDoesNotCreateIt() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        await store.DeleteAsync("absent");
        await store.DeleteAsync("absent");
        Assert.False(Directory.Exists(directory));
    });

    [Fact]
    public Task RepeatedDeletionLeavesTheSiblingSnapshotByteExact() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        await store.SaveAsync("removed", Snapshot("removed"));
        await store.SaveAsync("retained", Snapshot("retained"));
        await store.DeleteAsync("removed");
        var file = Assert.Single(Directory.GetFiles(directory));
        var bytes = File.ReadAllBytes(file);
        await store.DeleteAsync("removed");
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal("retained", (await store.LoadAsync("retained"))!.Messages[1].Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreCanceledDeletionKeepsExactTokenAndDoesNotMutateFiles(bool exists) => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        byte[]? bytes = null;
        string? file = null;
        if (exists)
        {
            await store.SaveAsync("selected", Snapshot("keep"));
            file = Assert.Single(Directory.GetFiles(directory));
            bytes = File.ReadAllBytes(file);
        }
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.DeleteAsync("selected", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        _ = caller.Token;
        if (exists) Assert.Equal(bytes, File.ReadAllBytes(file!));
        else Assert.False(Directory.Exists(directory));
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public Task InvalidSessionIdentifiersFailBeforeFilesystemMutation(string? identifier) => WithDirectory(async directory =>
    {
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Deletable(directory).DeleteAsync(identifier!));
        Assert.Equal("sessionId", error.ParamName);
        Assert.False(Directory.Exists(directory));
    });

    [Fact]
    public Task FilesystemDeletionFailuresRemainObservable() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        await store.SaveAsync("selected", Snapshot("keep"));
        var record = Assert.Single(Directory.GetFiles(directory));
        File.Delete(record);
        Directory.CreateDirectory(record);
        var marker = Path.Combine(record, "unrelated.txt");
        File.WriteAllText(marker, "keep this file");

        var failure = await Record.ExceptionAsync(async () => await store.DeleteAsync("selected"));

        Assert.True(failure is IOException or UnauthorizedAccessException, "A directory collision must remain an observable filesystem failure.");
        Assert.Equal("keep this file", File.ReadAllText(marker));
        Assert.True(Directory.Exists(record));
    });

    [Fact]
    public Task OpaqueIdentifiersRemainCaseSensitiveOnEveryHost() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        await store.SaveAsync("Case", Snapshot("upper"));
        await store.SaveAsync("case", Snapshot("lower"));
        await store.DeleteAsync("Case");
        Assert.Null(await store.LoadAsync("Case"));
        Assert.Equal("lower", (await store.LoadAsync("case"))!.Messages[1].Text);
    });

    [Fact]
    public Task DeletionDoesNotDeserializeACorruptedSnapshot() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        await store.SaveAsync("corrupted", Snapshot("saved"));
        File.WriteAllText(Assert.Single(Directory.GetFiles(directory)), "{invalid json");
        await store.DeleteAsync("corrupted");
        Assert.Null(await store.LoadAsync("corrupted"));
        Assert.Empty(Directory.GetFiles(directory));
    });

    [Fact]
    public Task DeletingPersistenceDoesNotClearAnExistingAgent() => WithDirectory(async directory =>
    {
        var store = Deletable(directory);
        var snapshot = Snapshot("already restored");
        await store.SaveAsync("selected", snapshot);
        await using var agent = new Agent(new() { Model = new NeverCalledModel(), SessionStore = store, SessionId = "selected" });
        await agent.InitializeAsync();
        await store.DeleteAsync("selected");
        Assert.Equal(snapshot.Messages[1].TrackingId, agent.Messages[1].TrackingId);
        Assert.Equal("already restored", agent.Messages[1].Text);
        Assert.Null(await new FileSessionStore(directory).LoadAsync("selected"));
    });

    private static IDeletableSessionStore Deletable(string directory) => Assert.IsAssignableFrom<IDeletableSessionStore>(new FileSessionStore(directory));
    private static SessionSnapshot Snapshot(string answer) => new(1, [Message.User("question"), Message.Assistant(answer)]);
    private static async Task WithDirectory(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-session-delete-" + Guid.NewGuid().ToString("N"));
        Exception? primary = null;
        try { await body(directory); }
        catch (Exception error) { primary = error; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup)
            {
                if (primary is null) primary = cleanup;
                else primary.Data["SessionDeletionTest.Cleanup"] = cleanup;
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
