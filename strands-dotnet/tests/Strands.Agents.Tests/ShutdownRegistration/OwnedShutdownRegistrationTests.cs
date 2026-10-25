using System.Runtime.CompilerServices;

namespace Strands.Agents.Tests.ShutdownRegistration;

public sealed class OwnedShutdownRegistrationTests
{
    [Fact]
    public async Task RegistrationBeforeShutdownRunsOnceAndLeavesApplicationHooksIntact()
    {
        var applicationCalls = 0;
        var application = new HookRegistry();
        using var external = application.Register<ShutdownEvent>((_, _) => { applicationCalls++; return default; });
        await using var agent = new Agent(new() { Model = new EmptyModel(), Hooks = application });
        var calls = 0;
        using var owned = agent.RegisterOwnedShutdownCallback((_, _) => { calls++; return default; });

        await agent.ShutdownAsync();
        await agent.ShutdownAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, applicationCalls);
    }

    [Fact]
    public async Task RegistrationAfterCompletedShutdownRejectsBeforeAddingACallback()
    {
        await using var agent = NewAgent();
        await agent.ShutdownAsync();
        var calls = 0;

        Assert.Throws<ObjectDisposedException>(() => agent.RegisterOwnedShutdownCallback((_, _) => { calls++; return default; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RegistrationAfterShutdownAdmissionRejectsWhileAnEarlierHookIsBlocked()
    {
        await using var agent = NewAgent();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = agent.RegisterOwnedShutdownCallback(async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        });
        var shutdown = agent.ShutdownAsync();
        try
        {
            await entered.Task;
            Assert.Throws<ObjectDisposedException>(() => agent.RegisterOwnedShutdownCallback((_, _) => default));
        }
        finally
        {
            release.TrySetResult(true);
            await shutdown;
        }
    }

    private static Agent NewAgent() => new(new() { Model = new EmptyModel() });
    private sealed class EmptyModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
