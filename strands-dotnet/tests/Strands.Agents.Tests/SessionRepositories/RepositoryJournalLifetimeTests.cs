using System.Text.Json;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalLifetimeTests
{
    [Fact]
    public async Task TheSameBorrowedManagerCanReopenTheSameIdAfterDisposal()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        await using (var agent = Create(manager)) await agent.InvokeAsync("persisted");
        await using var resumed = Create(manager);
        await resumed.InitializeAsync();
        Assert.Equal(new[] { "persisted", "answer" }, resumed.Messages.Select(message => message.Text));
        Assert.False(repository.Disposed);
    }

    [Fact]
    public async Task FailedRestoreReleasesTheReservationBeforeAReplacementAgentIsConstructed()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        var failure = new IOException("read failed");
        repository.Before = (operation, _) => { if (operation == "read-session") throw failure; return default; };
        var failed = Create(manager);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failed.InitializeAsync()));
        repository.Before = null;
        await using var resumed = Create(manager);
        await resumed.InitializeAsync();
        // A late, idempotent release of the failed binding must not remove this new reservation.
        await failed.DisposeAsync();
        Assert.Throws<ArgumentException>(() => Create(manager));
    }

    [Fact]
    public async Task ShutdownObserverFailureStillReleasesTheBindingAndPreservesThePrimaryError()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        var primary = new IOException("shutdown");
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => throw primary);
        var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Hooks = hooks, Plugins = [manager] });
        await agent.InitializeAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await agent.DisposeAsync()));
        await using var resumed = Create(manager);
        await resumed.InitializeAsync();
    }

    [Fact]
    public async Task ConcurrentDuplicateBindingsFailWithoutIoAndReleasedUniqueIdsDoNotAccumulate()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        var active = Create(manager);
        Assert.Throws<ArgumentException>(() => Create(manager));
        Assert.Empty(repository.Operations);
        await active.DisposeAsync();
        for (var index = 0; index < 8; index++)
        {
            await using var first = new Agent(new() { Id = "id-" + index, Model = new JournalTestModel(), Plugins = [manager] });
        }
        for (var index = 0; index < 8; index++)
        {
            await using var second = new Agent(new() { Id = "id-" + index, Model = new JournalTestModel(), Plugins = [manager] });
        }
    }

    [Fact]
    public async Task ConstructorValidationDoesNotReserveAnIdentity()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Id = "agent", Model = new JournalTestModel(), MaxTurns = 0, Plugins = [manager] }));
        await using var valid = Create(manager);
        await valid.InitializeAsync();
    }

    [Fact]
    public async Task ARepositoryCallbackCannotAwaitItsOwningAgentsInitialization()
    {
        var repository = new JournalProbeRepository();
        Agent? agent = null;
        repository.Before = async (operation, token) => { if (operation == "read-session") await agent!.InitializeAsync(token); };
        await using var owned = agent = Create(new("session", repository));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
    }

    [Fact]
    public async Task InvalidNativeMetadataIsRejectedBeforeStateOrHistoryMutation()
    {
        var repository = new JournalProbeRepository();
        await repository.Inner.CreateSessionAsync(new("session"));
        var metadata = JsonSerializer.SerializeToElement(new { nativeSessionVersion = 1, snapshot = new SessionSnapshot(2, []) });
        await repository.Inner.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { stored = true }), JsonSerializer.SerializeToElement(new { }), metadata));
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), State = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) }, Messages = [Message.User("configured")], Plugins = [new RepositorySessionManager("session", repository)] });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Null(agent.State.Get("stored"));
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
    }

    private static Agent Create(RepositorySessionManager manager) => new(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [manager] });
}
