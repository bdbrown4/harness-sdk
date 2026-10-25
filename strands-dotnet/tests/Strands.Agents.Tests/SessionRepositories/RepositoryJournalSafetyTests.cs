using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAcceptedByAnUncooperativeReadPreventsSubsequentCreationOrRestoreMutation(bool existing)
    {
        using var caller = new CancellationTokenSource();
        var repository = new JournalProbeRepository { IgnoreReadCancellation = true };
        if (existing)
        {
            await repository.Inner.CreateSessionAsync(new("session"));
            await repository.Inner.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { stored = true }), JsonSerializer.SerializeToElement(new { })));
            await repository.Inner.CreateMessageAsync("session", "agent", new(Message.User("stored"), 0));
        }
        repository.Before = (operation, _) => { if (operation == (existing ? "list-messages" : "read-session")) caller.Cancel(); return default; };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Messages = [Message.User("configured")], State = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) }, Plugins = [new RepositorySessionManager("session", repository)] });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Null(agent.State.Get("stored"));
        if (!existing) Assert.DoesNotContain("create-session", repository.Operations);
    }
    [Fact]
    public async Task RequestedCancellationCannotHideIndependentBackendCleanupDiagnostics()
    {
        using var caller = new CancellationTokenSource();
        var repository = new JournalProbeRepository();
        var primary = new IOException("backend failure");
        var cleanup = new IOException("independent cleanup");
        var diagnostics = new List<Exception> { cleanup };
        primary.Data["Strands.CleanupErrors"] = diagnostics;
        repository.Before = (operation, _) => { if (operation == "read-session") { caller.Cancel(); throw primary; } return default; };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("question", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Same(primary, error.InnerException);
        Assert.Same(diagnostics, error.Data["Strands.CleanupErrors"]);
    }

    [Fact]
    public async Task CallerCancellationWaitsForThePhysicalAcceptedWriteAndItsFinalRetry()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var repository = new JournalProbeRepository();
        repository.Before = async (operation, _) =>
        {
            if (operation != "create-message" || !first) return;
            first = false; entered.SetResult(true); await release.Task;
        };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        var invocation = agent.InvokeAsync("accepted", cancellationToken: caller.Token);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
            caller.Cancel();
            Assert.False(invocation.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.DisposeAsync());
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(new long[] { 0, 0 }, repository.MessageAttempts);
        Assert.Equal("accepted", Assert.Single(await repository.Inner.ListMessagesAsync("session", "agent")).Message.Text);
    }
    [Fact]
    public async Task PendingPersistenceSettlesBeforeEvenAPreregisteredMinimumPriorityInvocationHook()
    {
        var repository = new JournalProbeRepository();
        var observations = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { observations++; return default; }, int.MinValue);
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Hooks = hooks, Plugins = [new RepositorySessionManager("session", repository)] });
        repository.Before = (operation, _) => { if (operation == "create-message") throw new IOException("still pending"); return default; };
        await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("accepted"));
        Assert.Equal(1, observations);
        await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null));
        Assert.Equal(1, observations);
    }
    [Fact]
    public async Task APartialAliasWriteIsRetriedDuringFinalCleanupWithoutAnotherModelCall()
    {
        var repository = new JournalProbeRepository();
        var primary = new IOException("second alias");
        var reject = true;
        repository.Before = (operation, _) => { if (operation == "update-message" && repository.MessageUpdates.Count == 2 && reject) { reject = false; throw primary; } return default; };
        var shared = Message.User("secret");
        var model = new JournalRedactingModel();
        await using var agent = new Agent(new() { Id = "agent", Model = model, Messages = [shared, shared], Plugins = [new RepositorySessionManager("session", repository)] });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null)));
        Assert.Equal(1, model.Calls);
        Assert.Equal(new long[] { 0, 1, 1 }, repository.MessageUpdates);
        Assert.All(await repository.Inner.ListMessagesAsync("session", "agent"), row => Assert.Equal("redacted", row.ToMessage().Text));
        Assert.All(agent.Messages, message => Assert.Equal("redacted", message.Text));
    }

    [Fact]
    public async Task ConsecutiveProviderRedactionsRetainAllLiveAliasReceipts()
    {
        var repository = new JournalProbeRepository();
        var shared = Message.User("secret");
        await using var agent = new Agent(new() { Id = "agent", Model = new TwoRedactionsModel(), InputRedactionTiming = InputRedactionTiming.ProviderEvent, Messages = [shared, shared], Plugins = [new RepositorySessionManager("session", repository)] });
        await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null));
        await agent.InvokeAsync(null);
        Assert.Equal(new[] { "two", "two", "answer" }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.ToMessage().Text));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedInitializationCancellationHasExactCallerIdentityAndRetainsTheBackendCause(bool useCancellationError)
    {
        using var caller = new CancellationTokenSource();
        var repository = new JournalProbeRepository();
        Exception cause = useCancellationError ? new OperationCanceledException("backend", CancellationToken.None) : new IOException("backend");
        repository.Before = (operation, _) =>
        {
            if (operation == "read-session") { caller.Cancel(); throw cause; }
            return default;
        };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        // Cached initialization preserves cancellation identity; ordinary non-cancellation backend failure must be causal.
        if (error.InnerException is not null) Assert.Same(cause, error.InnerException);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task FailedAcceptedAppendRetainsItsIdAndIsRetriedWithoutRepeatingAModelCall()
    {
        var repository = new JournalProbeRepository();
        var failure = new IOException("first accepted row");
        var reject = true;
        repository.Before = (operation, _) => { if (operation == "create-message" && reject) { reject = false; throw failure; } return default; };
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("accepted")));
        Assert.Equal(new long[] { 0, 0 }, repository.MessageAttempts);
        Assert.Equal("accepted", Assert.Single(await repository.Inner.ListMessagesAsync("session", "agent")).Message.Text);
        await agent.InvokeAsync("next");
        Assert.Equal(new long[] { 0, 1, 2 }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
    }

    [Fact]
    public async Task RedactionPreservesTheOriginalAuditRowAndRestoresOnlyItsEffectiveReplacement()
    {
        var repository = new JournalProbeRepository();
        await using (var agent = new Agent(new() { Id = "agent", Model = new JournalRedactingModel(), Plugins = [new RepositorySessionManager("session", repository)] }))
            await agent.InvokeAsync("secret");
        var row = await repository.Inner.ReadMessageAsync("session", "agent", 0);
        Assert.Equal("secret", row!.Message.Text);
        Assert.Equal("redacted", row.RedactMessage?.Text);
        await using var restored = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        await restored.InitializeAsync();
        Assert.Equal("redacted", restored.Messages[0].Text);
    }

    [Fact]
    public async Task ReferenceAliasesAreUpdatedButDuplicateTrackingIdsAreNotAnAlias()
    {
        var repository = new JournalProbeRepository();
        var shared = new Message(Role.User, [new TextBlock("shared")], "reused-tracking-id");
        var distinct = new Message(Role.User, [new TextBlock("distinct")], "reused-tracking-id");
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalRedactingModel(), Messages = [shared, distinct, shared], Plugins = [new RepositorySessionManager("session", repository)] });
        await agent.InvokeAsync(null);
        var rows = await repository.Inner.ListMessagesAsync("session", "agent");
        Assert.Equal(new[] { "redacted", "distinct", "redacted", "answer" }, rows.Select(row => row.ToMessage().Text));
        Assert.Equal(new[] { "redacted", "distinct", "redacted", "answer" }, agent.Messages.Select(message => message.Text));
        Assert.Equal(3, rows.Take(3).Select(row => row.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task PublicHistoryReplacementFailsBeforeInitializationButStateOnlySnapshotsRemainAvailable()
    {
        var repository = new JournalProbeRepository();
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        var messages = new AgentSnapshot(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") }) });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.LoadSnapshotAsync(messages));
        Assert.Empty(repository.Operations);
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(new { application = "updated" }) }));
        Assert.Equal("updated", agent.State.Get("application")?.GetString());
    }

    [Fact]
    public async Task ConflictingBindingsAreRejectedWithoutRepositoryOrToolFilterEffects()
    {
        var repository = new JournalProbeRepository();
        var manager = new RepositorySessionManager("session", repository);
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new JournalTestModel(), Plugins = [manager], SessionId = "another" }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new JournalTestModel(), Plugins = [manager], SessionId = "session", SessionStore = new FileSessionStore("unused") }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new JournalTestModel(), Plugins = [manager, new RepositorySessionManager("other", repository)] }));
        Assert.Empty(repository.Operations);
    }

    [Fact]
    public async Task ChildStripsTheParentBindingAndCanBeExplicitlyBoundToAnIndependentJournal()
    {
        var repository = new JournalProbeRepository();
        await using var parent = new Agent(new() { Id = "parent", Model = new JournalTestModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        await using var child = parent.CreateChild();
        await child.InvokeAsync("child");
        Assert.Empty(repository.Operations);
        await using var rebound = parent.CreateChild(options => options with { Id = "rebound", Plugins = [new RepositorySessionManager("session", repository)] });
        await rebound.InvokeAsync("rebound");
        Assert.NotNull(await repository.Inner.ReadAgentAsync("session", "rebound"));
    }
}

internal sealed class JournalProbeRepository : ISessionRepository, IAsyncDisposable
{
    internal StorageSessionRepository Inner { get; } = new(new InMemoryStorage());
    internal Func<string, CancellationToken, ValueTask>? Before { get; set; }
    internal List<string> Operations { get; } = [];
    internal List<long> MessageAttempts { get; } = [];
    internal List<long> MessageUpdates { get; } = [];
    internal bool Disposed { get; private set; }
    internal bool IgnoreReadCancellation { get; init; }
    public ValueTask DisposeAsync() { Disposed = true; return default; }
    private async ValueTask Enter(string operation, CancellationToken token) { Operations.Add(operation); if (Before is { } before) await before(operation, token); }
    public async ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default) { await Enter("create-session", cancellationToken); return await Inner.CreateSessionAsync(session, cancellationToken); }
    public async ValueTask<SessionRecord?> ReadSessionAsync(string sessionId, CancellationToken cancellationToken = default) { await Enter("read-session", cancellationToken); return await Inner.ReadSessionAsync(sessionId, IgnoreReadCancellation ? CancellationToken.None : cancellationToken); }
    public async ValueTask CreateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default) { await Enter("create-agent", cancellationToken); await Inner.CreateAgentAsync(sessionId, agent, cancellationToken); }
    public async ValueTask<SessionAgentRecord?> ReadAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default) { await Enter("read-agent", cancellationToken); return await Inner.ReadAgentAsync(sessionId, agentId, cancellationToken); }
    public async ValueTask UpdateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default) { await Enter("update-agent", cancellationToken); await Inner.UpdateAgentAsync(sessionId, agent, cancellationToken); }
    public async ValueTask CreateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default) { MessageAttempts.Add(message.MessageId); await Enter("create-message", cancellationToken); await Inner.CreateMessageAsync(sessionId, agentId, message, cancellationToken); }
    public async ValueTask<SessionMessageRecord?> ReadMessageAsync(string sessionId, string agentId, long messageId, CancellationToken cancellationToken = default) { await Enter("read-message", cancellationToken); return await Inner.ReadMessageAsync(sessionId, agentId, messageId, cancellationToken); }
    public async ValueTask UpdateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default) { MessageUpdates.Add(message.MessageId); await Enter("update-message", cancellationToken); await Inner.UpdateMessageAsync(sessionId, agentId, message, cancellationToken); }
    public async ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string sessionId, string agentId, int? limit = null, int offset = 0, CancellationToken cancellationToken = default) { await Enter("list-messages", cancellationToken); return await Inner.ListMessagesAsync(sessionId, agentId, limit, offset, IgnoreReadCancellation ? CancellationToken.None : cancellationToken); }
}

internal sealed class JournalRedactingModel : IModel
{
    internal int Calls { get; private set; }
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        await foreach (var value in new JournalTestModel().StreamAsync(request, cancellationToken))
        {
            yield return value;
            if (value is ModelMessageStartEvent) yield return new ModelRedactionEvent(InputReplacement: "redacted");
        }
    }
}

internal sealed class TwoRedactionsModel : IModel
{
    private int _calls;
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls++;
        yield return new ModelMessageStartEvent();
        yield return new ModelRedactionEvent(InputReplacement: _calls == 1 ? "one" : "two");
        if (_calls == 1) throw new IOException("interrupted response");
        await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelContentBlockStartEvent(0);
        yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
        yield return new ModelContentBlockStopEvent(0);
        yield return new ModelMessageStopEvent(StopReasons.EndTurn);
    }
}
