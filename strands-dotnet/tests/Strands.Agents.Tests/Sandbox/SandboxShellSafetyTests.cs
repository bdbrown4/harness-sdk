using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxShellSafetyTests
{
    [Theory]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    public async Task IteratorPrimaryKeepsIdentityAndReferenceUniqueIndependentCleanup(string kind)
    {
        using var caller = new CancellationTokenSource();
        Exception primary = kind == "timeout"
            ? new Core.SandboxTimeoutException(TimeSpan.FromSeconds(1), new(4, "partial", "warn"))
            : new OperationCanceledException("Known cancellation primary", caller.Token);
        var cleanup = new IOException("Known independent iterator cleanup");
        var selected = new ShellFakeSandbox((_, _) => new(() => throw primary, dispose: () => throw cleanup));
        await using var agent = ShellTestContext.Agent(selected);
        var caught = await Record.ExceptionAsync(async () => await ShellTestContext.Invoke(Core.SandboxShell.Create(selected), agent, new { command = "never executes" }, caller.Token));
        Assert.Same(primary, caught);
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IEnumerable<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, selected.LastIterator!.Disposals);
    }

    [Fact]
    public async Task TerminalResultDoesNotReturnBeforeIteratorDisposalPhysicallyCompletes()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new ShellFakeSandbox((_, _) => new(() => true, new Core.ExecutionResult(0, "complete", ""), () =>
        {
            entered.TrySetResult(true);
            return new(release.Task);
        }));
        await using var agent = ShellTestContext.Agent(selected);
        var operation = ShellTestContext.Invoke(Core.SandboxShell.Create(selected), agent, new { command = "never executes" }).AsTask();
        try
        {
            var observed = await Task.WhenAny(entered.Task, operation, Task.Delay(TimeSpan.FromSeconds(5)));
            if (ReferenceEquals(observed, operation)) await operation;
            Assert.Same(entered.Task, observed);
            Assert.False(operation.IsCompleted);
            Assert.Equal(1, selected.LastIterator!.Disposals);
        }
        finally
        {
            release.TrySetResult(true);
            await operation;
        }
    }
}
