using static Strands.Agents.Tests.ToolProviders.AgentToolProviderUpdatesTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewUpdateCleanupCannotSelfAttachToALaterInitializationOrShutdownFailure(bool shutdown)
    {
        var primary = new IOException("shared lifecycle failure");
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        provider.Lease.BeforeDispose = () => new(Task.FromException(primary));
        var agent = new Agent(new()
        { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, Plugins = shutdown ? [] : [new FailingPlugin(primary)] });
        try
        {
            Exception error;
            if (shutdown)
            {
                await agent.InitializeAsync();
                using var hook = agent.Hooks.Register<ShutdownEvent>((_, _) => throw primary);
                error = await Assert.ThrowsAsync<IOException>(() => agent.ShutdownAsync());
            }
            else error = await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync());
            Assert.Same(primary, error);
            var secondary = error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> errors ? errors.ToArray() : [];
            Assert.DoesNotContain(secondary, value => ReferenceEquals(value, error));
            Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes);
        }
        finally { try { await agent.DisposeAsync(); } catch (Exception error) when (ReferenceEquals(error, primary)) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialLeaseCleanupIsReportedOnceAndCannotAttachThePrimaryToItself(bool reusedPrimary)
    {
        var primary = new IOException("initial source failure");
        var cleanup = reusedPrimary ? primary : new IOException("lease cleanup failure");
        var provider = new UpdatingProvider([new FixedTool("initial")]) { NoInitial = !reusedPrimary };
        if (reusedPrimary) provider.BeforeReturn = () => provider.Observer!.OnError(primary);
        provider.Lease.BeforeDispose = () => new(Task.FromException(cleanup));
        var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => agent.InitializeAsync());
            if (reusedPrimary) Assert.Same(primary, error); else Assert.IsType<InvalidOperationException>(error);
            var secondary = error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> errors ? errors.ToArray() : [];
            if (reusedPrimary) Assert.DoesNotContain(secondary, value => ReferenceEquals(value, error));
            else Assert.Same(cleanup, Assert.Single(secondary));
            Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes);
        }
        finally
        {
            // Preserve the failing implementation's repeated-close observation without leaving owned work alive.
            try { await agent.DisposeAsync(); }
            catch (Exception error) when (ReferenceEquals(error, cleanup)) { }
        }
    }

    private sealed class FailingPlugin(Exception error) : IPlugin
    {
        public string Name => "test:update-setup-failure";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => new(Task.FromException(error));
        public IReadOnlyList<ITool> GetTools() => [];
    }
}
