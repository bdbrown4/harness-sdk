using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapedLifecycleWorkGetsNoStaleAdmissionOrFalseSelfCycle(bool duringAnotherInvocation)
    {
        var releaseEscaped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = async () => { if (Interlocked.Increment(ref calls) == 2) { entered.TrySetResult(true); await releaseModel.Task; } } };
        var manager = new EscapingManager(releaseEscaped.Task);
        await using var agent = new Agent(new() { Model = model, ConversationManager = manager });
        await agent.InvokeAsync("first");
        Task<AgentResult>? second = null;
        try
        {
            if (duringAnotherInvocation)
            {
                second = agent.InvokeAsync("second");
                Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            }
            releaseEscaped.TrySetResult(true);
            var escaped = manager.Escaped!;
            Assert.Same(escaped, await Task.WhenAny(escaped, Task.Delay(8000)));
            var error = await escaped;
            if (duringAnotherInvocation) { Assert.IsType<ConcurrentInvocationException>(error); Assert.Equal(0, manager.ExplicitReductions); }
            else { Assert.Null(error); Assert.Equal(1, manager.ExplicitReductions); }
        }
        finally { releaseEscaped.TrySetResult(true); releaseModel.TrySetResult(true); if (second is not null) await second; }
    }

    [Fact]
    public async Task AnotherManagerCannotBorrowTheRegisteredCallbackAdmission()
    {
        var other = new Classic.NullConversationManager();
        var manager = new RedirectingManager(other);
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager });
        await agent.InvokeAsync("task");
        Assert.IsType<ConcurrentInvocationException>(manager.Error);
        Assert.Equal(0, other.RemovedMessageCount);
    }

    private sealed class EscapingManager(Task release) : Classic.ConversationManager
    {
        internal Task<Exception?>? Escaped;
        internal int ExplicitReductions;
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
        {
            Escaped ??= Task.Run(async () =>
            {
                await release;
                try { await ReduceContextAsync(agent); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            return default;
        }
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) { ExplicitReductions++; return default; }
    }
    private sealed class RedirectingManager(Classic.ConversationManager other) : Classic.ConversationManager
    {
        internal Exception? Error;
        protected override async ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
        {
            try { await other.ReduceContextAsync(agent, cancellationToken: cancellationToken); }
            catch (Exception error) { Error = error; }
        }
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) => default;
    }
}
