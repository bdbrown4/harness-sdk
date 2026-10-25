using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerLifetimeTests
{
    [Fact]
    public async Task ExecutorDisposalCallbacksCannotAwaitTheExecutorsOwnDisposal()
    {
        A2AExecutor? executor = null; Exception? rejection = null;
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { try { _ = executor!.DisposeAsync(); } catch (Exception error) { rejection = error; } return default; });
        executor = new(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel(), Hooks = hooks })) });
        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "hello"), new A2AExecutorTests.Sink());

        await executor.DisposeAsync();

        Assert.IsType<InvalidOperationException>(rejection);
        await executor.DisposeAsync();
    }
    [Fact]
    public async Task OwnedExecutorDisposalCallbacksCannotAwaitTheirServersOwnDisposal()
    {
        A2AServer? server = null; Exception? rejection = null;
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { try { _ = server!.DisposeAsync(); } catch (Exception error) { rejection = error; } return default; });
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel(), Hooks = hooks })) });
        server = new(executor, A2AServerTests.Options() with { OwnsExecutor = true });
        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "hello"), new A2AExecutorTests.Sink());

        await server.DisposeAsync();

        Assert.IsType<InvalidOperationException>(rejection);
        await server.DisposeAsync();
    }
    [Fact]
    public async Task OwnedCleanupAttemptsEveryResourceAndRetainsTheOriginalShutdownFailure()
    {
        var primary = new IOException("shutdown"); var first = new IOException("first resource"); var second = new IOException("second resource");
        var firstResource = new Resource(first); var secondResource = new Resource(second); var otherResource = new Resource(null);
        var hooks = new HookRegistry(); hooks.Register<ShutdownEvent>((_, _) => throw primary);
        var executor = new A2AExecutor(new()
        {
            AgentFactory = (context, _) => new(new Agent(new()
            {
                Model = new A2AServerTests.EchoModel(),
                Hooks = context == "first" ? hooks : new(),
                OwnedResources = context == "first" ? [firstResource, secondResource] : [otherResource]
            }))
        });
        var server = new A2AServer(executor, A2AServerTests.Options() with { OwnsExecutor = true });
        await executor.ExecuteAsync(A2AExecutorTests.Context("one", "first", "hello"), new A2AExecutorTests.Sink());
        await executor.ExecuteAsync(A2AExecutorTests.Context("two", "other", "hello"), new A2AExecutorTests.Sink());

        var error = await Assert.ThrowsAsync<IOException>(async () => { await server.DisposeAsync(); });

        Assert.Same(primary, error); Assert.Equal(new[] { second, first }, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, firstResource.Calls); Assert.Equal(1, secondResource.Calls); Assert.Equal(1, otherResource.Calls);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => { await server.DisposeAsync(); }));
        Assert.Equal(1, otherResource.Calls);
    }
    [Fact]
    public async Task DisposingABorrowingServerLeavesItsExecutorUsable()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        var server = new A2AServer(executor, A2AServerTests.Options()); await server.DisposeAsync();
        var sink = new A2AExecutorTests.Sink();
        await executor.ExecuteAsync(A2AExecutorTests.Context("task", "context", "hello"), sink);
        Assert.Equal("completed", sink.Events.Last().GetProperty("status").GetProperty("state").GetString());
    }
    private sealed class Resource(Exception? failure) : IAsyncDisposable
    {
        internal int Calls { get; private set; }
        public ValueTask DisposeAsync() { Calls++; if (failure is not null) throw failure; return default; }
    }
}
