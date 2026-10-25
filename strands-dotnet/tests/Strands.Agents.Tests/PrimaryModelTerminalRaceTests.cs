using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#if NETFRAMEWORK
using Strands.Agents.Tests.PrimaryModelPortability;
#endif

namespace Strands.Agents.Tests;

public sealed class PrimaryModelTerminalRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTerminalLifecycleDuringCapabilityReadPreventsPublication(bool dispose)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new RecordingOwner();
        var candidate = new RecordingOwner { CapabilityEntered = entered, CapabilityRelease = release };
        var resource = new RecordingResource();
        var hooks = new HookRegistry();
        var shutdownCalls = 0;
        hooks.Register<ShutdownEvent>((_, _) => { shutdownCalls++; return default; });
        var options = new AgentOptions { Model = original, OwnsModel = true, OwnedResources = [resource], Hooks = hooks };
        var agent = new Agent(options);
        Task? lifecycle = null;
        var setter = Task.Run(() => Record.Exception(() => agent.SetModel(candidate)));
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            Assert.False(setter.IsCompleted);
            Assert.Equal(0, candidate.CapabilityExits);

            lifecycle = dispose ? agent.DisposeAsync().AsTask() : agent.ShutdownAsync();
            await lifecycle.WaitAsync(Watchdog);

            Assert.Equal(1, shutdownCalls);
            Assert.Equal(dispose ? 1 : 0, original.DisposeCalls);
            Assert.Equal(dispose ? 1 : 0, resource.DisposeCalls);
            Assert.False(setter.IsCompleted);
            Assert.Equal(0, candidate.CapabilityExits);
            release.Set();
            var rejection = Assert.IsType<ObjectDisposedException>(await setter.WaitAsync(Watchdog));
            Assert.Equal(nameof(Agent), rejection.ObjectName);
            Assert.Equal(1, candidate.CapabilityReads);
            Assert.Equal(1, candidate.CapabilityExits);
            Assert.Same(original, agent.Model);
            Assert.Same(original, options.Model);
            Assert.Equal(0, original.FactoryCalls);
            Assert.Equal(0, candidate.FactoryCalls);
            Assert.Equal(0, original.ProviderCalls);
            Assert.Equal(0, candidate.ProviderCalls);
            Assert.Equal(0, candidate.DisposeCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.Set();
            await Join(setter, primary);
            if (lifecycle is not null) await Join(lifecycle, primary);
            await Join(agent.DisposeAsync().AsTask(), primary);
        }
        Assert.Equal(1, shutdownCalls);
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(0, candidate.DisposeCalls);
        Assert.Same(original, agent.Model);
    }

    private static async Task Join(Task operation, Exception? primary)
    {
        try { await operation; }
        catch (Exception cleanup) when (primary is not null)
        {
            if (!ReferenceEquals(primary, cleanup))
                primary.Data["PrimaryModelTerminalRace.JoinFailure"] = cleanup;
        }
    }

    private sealed class RecordingOwner : IInvocationModel, IStatefulModel, IAsyncDisposable
    {
        internal TaskCompletionSource<bool>? CapabilityEntered { get; init; }
        internal ManualResetEventSlim? CapabilityRelease { get; init; }
        private int _capabilityReads;
        private int _capabilityExits;
        internal int CapabilityReads => Volatile.Read(ref _capabilityReads);
        internal int CapabilityExits => Volatile.Read(ref _capabilityExits);
        internal int FactoryCalls { get; private set; }
        internal int ProviderCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public bool Stateful
        {
            get
            {
                Interlocked.Increment(ref _capabilityReads);
                CapabilityEntered?.TrySetResult(true);
                try { CapabilityRelease?.Wait(); return false; }
                finally { Interlocked.Increment(ref _capabilityExits); }
            }
        }
        public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState)
        {
            FactoryCalls++;
            return this;
        }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            ProviderCalls++;
            throw new InvalidOperationException("The terminal-race fixture does not invoke a provider.");
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class RecordingResource : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
}
