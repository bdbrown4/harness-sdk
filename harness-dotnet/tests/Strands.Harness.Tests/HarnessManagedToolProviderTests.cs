using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents;

namespace Strands.Harness.Tests;

[CollectionDefinition("Managed tool provider logging", DisableParallelization = true)]
public sealed class ManagedToolProviderLoggingCollection { }

[Collection("Managed tool provider logging")]
public sealed class HarnessManagedToolProviderTests
{
    private const string Module = "Strands.Harness.ModuleFixture.dll";

    [Fact]
    public async Task CreationAndConsumerAdmissionDoNotTouchMissingFilesOrUnrelatedModules()
    {
        var missing = Path.Combine(Path.GetTempPath(), "managed-provider-missing-" + Guid.NewGuid().ToString("N"));
        var loader = new HarnessAssemblyLoader(missing);
        var provider = loader.CreateToolProvider(Config("generation", ["version.txt"], unrelated: true));
        await using var consumer = new ContextOwner();
        await consumer.Agent.InitializeAsync();
        await provider.AddConsumerAsync(consumer.Context);
        await provider.RemoveConsumerAsync(consumer.Context);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../outside/nested")]
    public void CreationRejectsPathsOutsideTheFixedRootBeforeFilesystemAccess(string file)
    {
        var missing = Path.Combine(Path.GetTempPath(), "managed-provider-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => new HarnessAssemblyLoader(missing).CreateToolProvider(Config("generation", [file])));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void WatchingRequiresExplicitDeclaredInputs()
        => Assert.Throws<ArgumentException>(() => new HarnessAssemblyLoader(Path.GetTempPath()).CreateToolProvider(Config("generation", [])));

    [Fact]
    public async Task LoadPublishesCachedOriginalStreamingToolsWithoutResolvingOtherConfiguration()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generationStream", ["version.txt"], unrelated: true));
        await using var owner = new ContextOwner();
        await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        try
        {
            var tools = await provider.LoadToolsAsync(owner.Context);
            var original = Assert.Single(tools);
            Assert.IsAssignableFrom<IStreamingTool>(original);
            Assert.IsAssignableFrom<IRichTool>(original);
            Assert.Same(tools, await provider.LoadToolsAsync(owner.Context));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task ChangedDeclaredBytesDeliverAFreshRootWhileOldToolRemainsUsable()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner();
        await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var observer = new Observer();
        try
        {
            await using var lease = await provider.WatchToolsAsync(owner.Context, observer);
            var oldTool = Assert.Single(observer.Initial!);
            var old = await Invoke(oldTool);
            File.WriteAllText(workspace.Side, "changed");
            var next = await Wait(observer.Next.Task);
            var fresh = Assert.Single(next);
            Assert.NotSame(oldTool, fresh);
            Assert.NotEqual(old, await Invoke(fresh));
            Assert.Equal(old, await Invoke(oldTool));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task AssembledHarnessUsesTypedProviderOptionsWithoutResolvingTheToolConfigTwice()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"], unrelated: true));
        await using var agent = await Harness.CreateAsync(new()
        {
            AgentOptions = new() { Model = new UncalledModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, BackgroundTasks = null },
            BuiltinTools = [],
            BuiltinPlugins = [],
            Session = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            BackgroundTasks = null,
        });
        await agent.InitializeAsync();
        Assert.Equal(32, (await agent.InvokeToolAsync(new("actual", "generation", JsonSerializer.SerializeToElement(new { })))).Content.Length);
        Assert.Single(agent.Tools);
    }

    [Fact]
    public async Task ACancelledLeaderCannotPoisonAnotherAdmittedConsumer()
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var entered = Signal(); var release = Signal();
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var first = new ContextOwner(); await using var second = new ContextOwner();
        await first.Agent.InitializeAsync(); await second.Agent.InitializeAsync();
        await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(second.Context);
        using var caller = new CancellationTokenSource();
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }));
        try
        {
            var leader = provider.LoadToolsAsync(first.Context, caller.Token).AsTask();
            await Wait(entered.Task);
            var follower = provider.LoadToolsAsync(second.Context).AsTask();
            caller.Cancel();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await leader);
            Assert.Equal(caller.Token, canceled.CancellationToken);
            Assert.False(follower.IsCompleted);
            release.TrySetResult(true);
            Assert.Single(await Wait(follower));
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            await provider.RemoveConsumerAsync(first.Context); await provider.RemoveConsumerAsync(second.Context);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedPendingOperationsCannotReturnAfterTheSameConsumerIsReadmitted(bool watch)
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var entered = Signal(); var release = Signal();
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var first = new ContextOwner(); await using var second = new ContextOwner();
        await first.Agent.InitializeAsync(); await second.Agent.InitializeAsync();
        await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(second.Context);
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }));
        IAsyncDisposable? staleLease = null;
        var oldDeliveries = 0;
        try
        {
            var pending = WaitForAdmission();
            await Wait(entered.Task);
            await provider.RemoveConsumerAsync(first.Context);
            await provider.AddConsumerAsync(first.Context);
            release.TrySetResult(true);
            var error = await Record.ExceptionAsync(async () => await Wait(pending));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(0, oldDeliveries);
            await using var fresh = await provider.WatchToolsAsync(first.Context, new Receiver());
            Assert.Single(await provider.LoadToolsAsync(second.Context));

            async Task WaitForAdmission()
            {
                if (watch) staleLease = await provider.WatchToolsAsync(first.Context, new Receiver(_ => oldDeliveries++));
                else await provider.LoadToolsAsync(first.Context);
            }
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            if (staleLease is not null) await staleLease.DisposeAsync();
            await provider.RemoveConsumerAsync(first.Context); await provider.RemoveConsumerAsync(second.Context);
        }
    }

    [Fact]
    public async Task LastRemovalPhysicallyJoinsAnAdmittedGetterAfterItsCallerStopsWaiting()
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var entered = Signal(); var release = Signal();
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        using var caller = new CancellationTokenSource();
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }));
        Task? removal = null;
        try
        {
            var load = provider.LoadToolsAsync(owner.Context, caller.Token).AsTask();
            await Wait(entered.Task);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await load);
            removal = provider.RemoveConsumerAsync(owner.Context).AsTask();
            Assert.False(removal.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            if (removal is not null) await Wait(removal);
            else await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnobservedIndependentGetterCancellationRemainsTheRemovalCause(bool tokenBearing)
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var entered = Signal(); var release = Signal();
        using var independent = new CancellationTokenSource();
        var primary = tokenBearing ? new OperationCanceledException(independent.Token) : new OperationCanceledException("independent");
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        using var caller = new CancellationTokenSource();
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); throw primary; }));
        Task? removal = null;
        try
        {
            var load = provider.LoadToolsAsync(owner.Context, caller.Token).AsTask();
            await Wait(entered.Task);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await load);
            removal = provider.RemoveConsumerAsync(owner.Context).AsTask();
            Assert.False(removal.IsCompleted);
            release.TrySetResult(true);
            Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await removal));
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            if (removal is null) await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Fact]
    public async Task AThrowingInitialObserverDoesNotPoisonTheSharedAcceptedTools()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var primary = new InvalidDataException("initial observer");
        try
        {
            Assert.Same(primary, await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await provider.WatchToolsAsync(owner.Context, new Receiver(_ => throw primary))));
            var next = new Receiver();
            await using var lease = await provider.WatchToolsAsync(owner.Context, next);
            Assert.Same(Assert.Single(await provider.LoadToolsAsync(owner.Context)), Assert.Single(await next.WaitForCount(1)));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task MonitoringErrorsTerminateTheSourceAndLastRemovalAllowsAFreshListener()
    {
        using var workspace = new Workspace();
        ManualWatcher? watcher = null;
        var allocations = 0;
        var loader = new HarnessAssemblyLoader(workspace.Root)
        {
            ToolProviderWatcherFactory = () => { allocations++; return watcher = new(); },
        };
        var provider = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        Assert.Equal(0, allocations);
        var accepted = await provider.LoadToolsAsync(owner.Context);
        Assert.Equal(0, allocations);
        var observer = new Receiver();
        var primary = new InternalBufferOverflowException("trusted test overflow");
        await using (var lease = await provider.WatchToolsAsync(owner.Context, observer))
        {
            Assert.Equal(1, allocations);
            watcher!.Fail(primary);
            Assert.Same(primary, await Wait(observer.Error.Task));
            Assert.Same(primary, await Assert.ThrowsAsync<InternalBufferOverflowException>(async () => await provider.LoadToolsAsync(owner.Context)));
            Assert.Same(Assert.Single(accepted), Assert.Single(await observer.WaitForCount(1)));
        }
        await provider.RemoveConsumerAsync(owner.Context);
        await provider.AddConsumerAsync(owner.Context);
        try
        {
            await using var lease = await provider.WatchToolsAsync(owner.Context, new Receiver());
            Assert.Equal(2, allocations);
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task IndependentObserversDoNotBlockTheSourceOrEachOtherAndLeasesJoinTheirOwnCallbacks()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var first = new ContextOwner(); await using var second = new ContextOwner();
        await first.Agent.InitializeAsync(); await second.Agent.InitializeAsync();
        await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(second.Context);
        var entered = Signal(); var release = Signal(); var count = 0;
        var slow = new Receiver(_ => { if (Interlocked.Increment(ref count) == 2) { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); } });
        var fast = new Receiver();
        var slowLease = await provider.WatchToolsAsync(first.Context, slow);
        var fastLease = await provider.WatchToolsAsync(second.Context, fast);
        Task? close = null;
        try
        {
            File.WriteAllText(workspace.Side, "second");
            await Wait(entered.Task);
            var secondBatch = await fast.WaitForCount(2);
            close = slowLease.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted);
            File.WriteAllText(workspace.Side, "third");
            var thirdBatch = await fast.WaitForCount(3);
            Assert.NotSame(Assert.Single(secondBatch), Assert.Single(thirdBatch));
        }
        finally
        {
            release.TrySetResult(true);
            if (close is not null) await Wait(close); else await slowLease.DisposeAsync();
            await fastLease.DisposeAsync();
            await provider.RemoveConsumerAsync(first.Context); await provider.RemoveConsumerAsync(second.Context);
        }
    }

    [Fact]
    public async Task CallbacksCannotReenterTheirOwnProviderLifecycle()
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var getterCalls = 0; var observerCalls = 0;
        callback.SetValue(null, (Action)(() =>
        {
            Assert.Throws<InvalidOperationException>(() => provider.RemoveConsumerAsync(owner.Context));
            Assert.Throws<InvalidOperationException>(() => provider.LoadToolsAsync(owner.Context));
            getterCalls++;
        }));
        try
        {
            await using var lease = await provider.WatchToolsAsync(owner.Context, new Receiver(_ =>
            {
                Assert.Throws<InvalidOperationException>(() => provider.AddConsumerAsync(owner.Context));
                Assert.Throws<InvalidOperationException>(() => provider.RemoveConsumerAsync(owner.Context));
                observerCalls++;
            }));
            Assert.Equal(1, getterCalls); Assert.Equal(1, observerCalls);
        }
        finally { callback.SetValue(null, null); await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public Task CleanupFailuresDoNotReplaceAnUnobservedGetterFailure() => VerifyLateGetterCleanup();

    [Fact]
    public Task CleanupPreservesExistingEnumerableDiagnostics() => VerifyLateGetterCleanup([new IOException("earlier cleanup")]);

    private static async Task VerifyLateGetterCleanup(Exception[]? existing = null)
    {
        using var workspace = new Workspace();
        var cleanup = new IOException("watcher cleanup");
        var primary = new InvalidDataException("late getter primary");
        if (existing is not null) primary.Data["Strands.CleanupErrors"] = existing;
        var loader = new HarnessAssemblyLoader(workspace.Root) { ToolProviderWatcherFactory = () => new ManualWatcher { DisposalFailure = cleanup } };
        var callback = PrepareGetter(loader);
        var entered = Signal(); var release = Signal();
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        using var caller = new CancellationTokenSource();
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); throw primary; }));
        Task? removal = null;
        try
        {
            var watch = provider.WatchToolsAsync(owner.Context, new Receiver(), caller.Token).AsTask();
            await Wait(entered.Task);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch);
            removal = provider.RemoveConsumerAsync(owner.Context).AsTask();
            Assert.False(removal.IsCompleted);
            release.TrySetResult(true);
            var error = await Record.ExceptionAsync(async () => await removal);
            Assert.Same(primary, error);
            var errors = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
            if (existing is null) Assert.Same(cleanup, Assert.Single(errors));
            else
            {
                Assert.Equal(2, errors.Count);
                Assert.Same(existing[0], errors[0]); Assert.Same(cleanup, errors[1]);
            }
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            if (removal is null) await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Fact]
    public async Task OneTimeProviderConsumersDoNotAllocateAListener()
    {
        using var workspace = new Workspace();
        var allocations = 0;
        var loader = new HarnessAssemblyLoader(workspace.Root) { ToolProviderWatcherFactory = () => { allocations++; return new(); } };
        var provider = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        await using var agent = new Agent(new() { Model = new UncalledModel(), ToolProviders = [provider], BackgroundTasks = null });
        await agent.InitializeAsync();
        Assert.Single(agent.Tools);
        Assert.Equal(0, allocations);
        await agent.DisposeAsync();
        Assert.Equal(0, allocations);
    }

    [Fact]
    public async Task DeclaredDirectoryCreationRenameAndDeletionAreRealInvalidations()
    {
        using var workspace = new Workspace();
        var selected = Path.Combine(workspace.Root, "selected");
        Directory.CreateDirectory(selected);
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["selected"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var observer = new Receiver();
        try
        {
            await using var lease = await provider.WatchToolsAsync(owner.Context, observer);
            var initial = Assert.Single(await observer.WaitForCount(1));
            var original = Path.Combine(selected, "first.txt");
            var renamed = Path.Combine(selected, "renamed.txt");
            var staged = Path.Combine(workspace.Root, "staged.txt");
            File.WriteAllText(staged, "create");
            File.Move(staged, original);
            var created = Assert.Single(await observer.WaitForCount(2));
            Assert.NotSame(initial, created);
            File.Move(original, renamed);
            var moved = Assert.Single(await observer.WaitForCount(3));
            Assert.NotSame(created, moved);
            File.Delete(renamed);
            Assert.Same(initial, Assert.Single(await observer.WaitForCount(4)));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("null")]
    [InlineData("invalidTools")]
    public async Task ACompleteToolBatchMustValidateBeforeTheFirstObserverSeesAnything(string invalid)
    {
        using var workspace = new Workspace();
        var config = HarnessAgentConfig.Parse(JsonSerializer.Serialize(new
        {
            tools = new[] { new { module = Module, export = "generation", files = new[] { "version.txt" } }, new { module = Module, export = invalid, files = Array.Empty<string>() } },
        }));
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(config);
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var calls = 0;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.WatchToolsAsync(owner.Context, new Receiver(_ => calls++)));
            Assert.Equal(0, calls);
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task DuplicateConsumerDeclarationsCloseEveryOldLeaseWithoutRetiringAReadmittedConsumer()
    {
        using var workspace = new Workspace();
        var allocations = 0;
        var loader = new HarnessAssemblyLoader(workspace.Root) { ToolProviderWatcherFactory = () => { allocations++; return new(); } };
        var provider = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        await using var first = new ContextOwner(); await using var second = new ContextOwner();
        await first.Agent.InitializeAsync(); await second.Agent.InitializeAsync();
        await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(second.Context);
        var entered = Signal(); var release = Signal(); var deliveries = 0;
        var fast = new Receiver(); var readmitted = new Receiver();
        var firstLease = await provider.WatchToolsAsync(first.Context, new Receiver());
        var slowLease = await provider.WatchToolsAsync(first.Context, new Receiver(_ =>
        {
            if (Interlocked.Increment(ref deliveries) == 2) { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }
        }));
        var otherLease = await provider.WatchToolsAsync(second.Context, fast);
        IAsyncDisposable? newLease = null;
        Task? removing = null;
        try
        {
            File.WriteAllText(workspace.Side, "second");
            await Wait(entered.Task); await fast.WaitForCount(2);
            removing = provider.RemoveConsumerAsync(first.Context).AsTask();
            Assert.False(removing.IsCompleted);
            await provider.AddConsumerAsync(first.Context);
            newLease = await provider.WatchToolsAsync(first.Context, readmitted);
            release.TrySetResult(true); await Wait(removing);
            File.WriteAllText(workspace.Side, "third");
            Assert.Same(Assert.Single(await fast.WaitForCount(3)), Assert.Single(await readmitted.WaitForCount(2)));
            Assert.Equal(1, allocations);
        }
        finally
        {
            release.TrySetResult(true);
            if (removing is not null) await Wait(removing);
            await firstLease.DisposeAsync(); await slowLease.DisposeAsync(); await otherLease.DisposeAsync();
            if (newLease is not null) await newLease.DisposeAsync();
            await provider.RemoveConsumerAsync(first.Context); await provider.RemoveConsumerAsync(second.Context);
        }
    }

    [Fact]
    public async Task FailedEstablishmentAndRemovalJoinAnAlreadyEnteredInitialObserver()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var first = new ContextOwner(); await using var second = new ContextOwner();
        await first.Agent.InitializeAsync(); await second.Agent.InitializeAsync();
        await provider.AddConsumerAsync(first.Context); await provider.AddConsumerAsync(second.Context);
        var entered = Signal(); var release = Signal();
        using var caller = new CancellationTokenSource();
        var pending = Task.Run(async () => await provider.WatchToolsAsync(first.Context, new Receiver(_ =>
        { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }), caller.Token));
        Task? removing = null;
        try
        {
            await Wait(entered.Task);
            caller.Cancel(); removing = provider.RemoveConsumerAsync(first.Context).AsTask();
            Assert.False(pending.IsCompleted); Assert.False(removing.IsCompleted);
            release.TrySetResult(true);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(caller.Token, error.CancellationToken);
            await Wait(removing);
            await using var lease = await provider.WatchToolsAsync(second.Context, new Receiver());
        }
        finally
        {
            release.TrySetResult(true);
            if (removing is not null) await Wait(removing); else await provider.RemoveConsumerAsync(first.Context);
            await provider.RemoveConsumerAsync(second.Context);
        }
    }

    [Fact]
    public async Task InputChangesWhileAGetterIsRunningDiscardThatCandidateBeforeInitialDelivery()
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var callback = PrepareGetter(loader);
        var original = Assert.Single(loader.ResolveOptions(Config("generation", ["version.txt"])).Tools!);
        var oldIdentity = await Invoke(original);
        var entered = Signal(); var release = Signal();
        callback.SetValue(null, (Action)(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); }));
        var provider = loader.CreateToolProvider(Config("reentrant", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        IAsyncDisposable? lease = null;
        try
        {
            var observer = new Receiver();
            var pending = provider.WatchToolsAsync(owner.Context, observer).AsTask();
            await Wait(entered.Task);
            File.WriteAllText(workspace.Side, "changed during getter");
            release.TrySetResult(true);
            lease = await Wait(pending);
            Assert.NotEqual(oldIdentity, await Invoke(Assert.Single(await observer.WaitForCount(1))));
            Assert.Equal(oldIdentity, await Invoke(original));
        }
        finally
        {
            release.TrySetResult(true); callback.SetValue(null, null);
            if (lease is not null) await lease.DisposeAsync();
            await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompatibleOpenWriterDoesNotPreventCapturingRootImagesOrDeclaredFiles(bool declaredFile)
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        using var writer = new FileStream(declaredFile ? workspace.Side : Path.Combine(workspace.Root, Module),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var tools = await provider.LoadToolsAsync(owner.Context);
            Assert.Equal("generation", Assert.Single(tools).Spec.Name);
            Assert.True(writer.CanWrite);
            Assert.Same(tools, await provider.LoadToolsAsync(owner.Context));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCaptureStopsAtTheOpenedLengthWhenTheFileGrows(bool image)
    {
        var prefix = new byte[] { 1, 2, 3 };
        using var stream = new FixedLengthSource(new byte[] { 1, 2, 3, 4, 5, 6 }, prefix.Length);
        if (image) Assert.Equal(prefix, HarnessAssemblyLoader.ReadSourceImage(stream));
        else
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            var actual = HarnessAssemblyLoader.HashSourceStream(hash, stream);
            using var expected = System.Security.Cryptography.SHA256.Create();
            Assert.Equal(expected.ComputeHash(prefix), actual);
        }
        Assert.Equal(prefix.Length, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCaptureRejectsPrematureEndInsteadOfAcceptingAShrunkenImageOrHash(bool image)
    {
        using var stream = new FixedLengthSource(new byte[] { 1, 2, 3 }, 6);
        if (image) Assert.Throws<EndOfStreamException>(() => HarnessAssemblyLoader.ReadSourceImage(stream));
        else
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            Assert.Throws<EndOfStreamException>(() => HarnessAssemblyLoader.HashSourceStream(hash, stream));
        }
    }

    [Fact]
    public async Task APartialLiveImageRetainsTheAcceptedBatchAndRecoversDespiteAThrowingDiagnostic()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, Module); var image = File.ReadAllBytes(path);
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var previous = HarnessLogging.Logger;
        var warning = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        HarnessLogging.ConfigureLogging(new WarningLogger(error => { warning.TrySetResult(error); throw new IOException("host diagnostic"); }));
        try
        {
            var observer = new Receiver();
            await using var lease = await provider.WatchToolsAsync(owner.Context, observer);
            var initial = await observer.WaitForCount(1);
            File.WriteAllText(path, "partial managed image");
            Assert.IsType<BadImageFormatException>(await Wait(warning.Task));
            Assert.Same(initial, await provider.LoadToolsAsync(owner.Context));
            Assert.False(observer.Error.Task.IsCompleted);
            File.WriteAllBytes(path, image); File.WriteAllText(workspace.Side, "repaired");
            Assert.NotSame(Assert.Single(initial), Assert.Single(await observer.WaitForCount(2)));
        }
        finally { HarnessLogging.ConfigureLogging(previous); await provider.RemoveConsumerAsync(owner.Context); }
    }

    [Fact]
    public async Task ParentAndChildShareSourceButKeepIndependentPhysicalConsumerLifetimes()
    {
        using var workspace = new Workspace();
        var allocations = 0;
        var loader = new HarnessAssemblyLoader(workspace.Root) { ToolProviderWatcherFactory = () => { allocations++; return new(); } };
        var provider = loader.CreateToolProvider(Config("generationStream", ["version.txt"]));
        await using var parent = new Agent(new() { Model = new UncalledModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, BackgroundTasks = null });
        await parent.InitializeAsync();
        await using var child = parent.CreateChild(options => options with { Id = parent.Id });
        await child.InitializeAsync();
        var original = Assert.Single(parent.Tools);
        Assert.Same(original, Assert.Single(child.Tools));
        Assert.IsAssignableFrom<IStreamingTool>(original); Assert.IsAssignableFrom<IRichTool>(original);
        await parent.DisposeAsync();
        Assert.Equal(1, allocations);
        var result = await child.InvokeToolAsync(new("after-parent-close", "generation_stream", JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains(result.ContentBlocks, block => block is JsonBlock);
        Assert.Same(original, Assert.Single(child.Tools));
        await child.DisposeAsync();
        await using var fresh = new Agent(new() { Model = new UncalledModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, BackgroundTasks = null });
        await fresh.InitializeAsync();
        Assert.Equal(2, allocations);
    }

    [Fact]
    public async Task LastRemovalJoinsListenerCleanupAndRefusesItsLifecycleReentry()
    {
        using var workspace = new Workspace();
        var entered = Signal(); var release = Signal();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        IToolProviderUpdates? provider = null;
        loader.ToolProviderWatcherFactory = () => new ManualWatcher
        {
            Disposing = () =>
        {
            Assert.Throws<InvalidOperationException>(() => provider!.RemoveConsumerAsync(owner.Context));
            entered.TrySetResult(true); release.Task.GetAwaiter().GetResult();
        }
        };
        provider = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        await provider.AddConsumerAsync(owner.Context);
        await using var lease = await provider.WatchToolsAsync(owner.Context, new Receiver());
        var removing = Task.Run(async () => await provider.RemoveConsumerAsync(owner.Context));
        try
        {
            await Wait(entered.Task);
            Assert.False(removing.IsCompleted);
        }
        finally { release.TrySetResult(true); await Wait(removing); }
    }

    [Fact]
    public async Task SynchronousOwnerChainsPermitAnotherProviderAndRestoreTheOuterOwner()
    {
        using var workspace = new Workspace();
        var loader = new HarnessAssemblyLoader(workspace.Root);
        var first = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        var second = loader.CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await first.AddConsumerAsync(owner.Context); await second.AddConsumerAsync(owner.Context);
        IAsyncDisposable? nested = null;
        try
        {
            await using var warm = await second.WatchToolsAsync(owner.Context, new Receiver());
            var calls = 0;
            await using var lease = await first.WatchToolsAsync(owner.Context, new Receiver(_ =>
            {
                var establishment = second.WatchToolsAsync(owner.Context, new Receiver(_ =>
                {
                    Assert.Throws<InvalidOperationException>(() => first.RemoveConsumerAsync(owner.Context));
                    Assert.Throws<InvalidOperationException>(() => second.RemoveConsumerAsync(owner.Context));
                    calls++;
                }));
                Assert.True(establishment.IsCompletedSuccessfully);
                nested = establishment.Result;
                Assert.Throws<InvalidOperationException>(() => first.RemoveConsumerAsync(owner.Context));
            }));
            Assert.Equal(1, calls);
            await nested!.DisposeAsync(); nested = null;
        }
        finally
        {
            if (nested is not null) await nested.DisposeAsync();
            await first.RemoveConsumerAsync(owner.Context); await second.RemoveConsumerAsync(owner.Context);
        }
    }

    [Fact]
    public async Task CapturedContextsAndTasksExpireWithoutBlockingIndependentLifecycle()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        ExecutionContext? captured = null; Task? escaped = null;
        var outside = Signal();
        try
        {
            await using var lease = await provider.WatchToolsAsync(owner.Context, new Receiver(_ =>
            {
                captured = ExecutionContext.Capture();
                escaped = Task.Run(async () =>
                {
                    await outside.Task;
                    Assert.Single(await provider.LoadToolsAsync(owner.Context));
                    await provider.RemoveConsumerAsync(owner.Context);
                });
            }));
            Task<IReadOnlyList<ITool>>? independent = null;
            ExecutionContext.Run(captured!, _ => independent = provider.LoadToolsAsync(owner.Context).AsTask(), null);
            Assert.Single(await Wait(independent!));
            outside.TrySetResult(true);
            await Wait(escaped!);
        }
        finally
        {
            outside.TrySetResult(true);
            if (escaped is not null) await Wait(escaped);
            captured?.Dispose();
            await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Fact]
    public async Task ExpiredExecutionContextCannotHideTheCurrentSynchronousObserverFromLeaseReentry()
        => await VerifyExpiredExecutionContextReentry(remove: false);

    [Fact]
    public async Task ExpiredExecutionContextCannotHideTheCurrentSynchronousObserverFromRemovalReentry()
        => await VerifyExpiredExecutionContextReentry(remove: true);

    private static async Task VerifyExpiredExecutionContextReentry(bool remove)
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        using var application = new CancellationTokenSource();
        CancellationTokenRegistration registration = default;
        IAsyncDisposable? lease = null;
        Task? nested = null;
        Exception? refusal = null;
        var invoked = Signal(); var deliveries = 0; var pendingInsideCallback = false;
        var observer = new Receiver(_ =>
        {
            if (Interlocked.Increment(ref deliveries) == 1)
            {
                registration = application.Token.Register(() =>
                {
                    try
                    {
                        nested = remove ? provider.RemoveConsumerAsync(owner.Context).AsTask() : lease!.DisposeAsync().AsTask();
                        pendingInsideCallback = !nested.IsCompleted;
                    }
                    catch (Exception error) { refusal = error; }
                });
            }
            else { application.Cancel(); invoked.TrySetResult(true); }
        });
        try
        {
            lease = await provider.WatchToolsAsync(owner.Context, observer);
            File.WriteAllText(workspace.Side, "second");
            await Wait(invoked.Task);
            if (nested is not null) await Wait(nested);
            Assert.True(refusal?.GetType() == typeof(InvalidOperationException),
                $"Reentry refusal: {refusal?.GetType().FullName ?? "none"}; returned lifecycle task: {nested is not null}; pending inside outer callback: {pendingInsideCallback}.");
            Assert.False(pendingInsideCallback);
        }
        finally
        {
            registration.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            await provider.RemoveConsumerAsync(owner.Context);
        }
    }

    [Fact]
    public async Task RemovingTheLastConsumerDoesNotDisposeBorrowedModuleTools()
    {
        using var workspace = new Workspace();
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("borrowed", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        var tool = Assert.Single(await provider.LoadToolsAsync(owner.Context));
        Assert.IsAssignableFrom<IDisposable>(tool);
        await provider.RemoveConsumerAsync(owner.Context);
        Assert.Equal("alive", await Invoke(tool));
    }

    [Fact]
    public async Task AFailedInitialImageCanBeRepairedWithoutReplacingTheProvider()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, Module);
        var original = File.ReadAllBytes(path);
        File.WriteAllText(path, "partial image");
        var provider = new HarnessAssemblyLoader(workspace.Root).CreateToolProvider(Config("generation", ["version.txt"]));
        await using var owner = new ContextOwner(); await owner.Agent.InitializeAsync();
        await provider.AddConsumerAsync(owner.Context);
        try
        {
            await Assert.ThrowsAsync<BadImageFormatException>(async () => await provider.LoadToolsAsync(owner.Context));
            File.WriteAllBytes(path, original);
            Assert.Single(await provider.LoadToolsAsync(owner.Context));
        }
        finally { await provider.RemoveConsumerAsync(owner.Context); }
    }

    private static PropertyInfo PrepareGetter(HarnessAssemblyLoader loader)
    {
        var config = HarnessAgentConfig.Parse(JsonSerializer.Serialize(new
        {
            tools = new[] { new { module = Module, export = "generation", files = new[] { "version.txt" } } },
            modelModule = new { module = Module, export = "model" },
            builtinTools = Array.Empty<string>(),
            builtinPlugins = Array.Empty<string>(),
            session = false,
            skills = false,
            memory = false,
            contextManager = false,
        }));
        var options = loader.ResolveOptions(config);
        return options.AgentOptions!.Model.GetType().Assembly.GetType("Strands.Harness.ModuleFixture.Exports", true)!.GetProperty("OnRead")!;
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Wait(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }

    private static HarnessAgentConfig Config(string export, string[] files, bool unrelated = false)
        => HarnessAgentConfig.Parse(JsonSerializer.Serialize(new
        {
            tools = new[] { new { module = Module, export, files } },
            modelModule = unrelated ? new { module = "do-not-resolve.dll", export = "model" } : null,
        }));

    private static async Task<string> Invoke(ITool tool)
    {
        var input = JsonSerializer.SerializeToElement(new { });
        return await tool.InvokeAsync(input, new(null!, new("call", tool.Spec.Name, input), new Dictionary<string, object?>()));
    }

    private static async Task<T> Wait<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        return await task;
    }

    private sealed class Observer : IObserver<IReadOnlyList<ITool>>
    {
        public IReadOnlyList<ITool>? Initial;
        public TaskCompletionSource<IReadOnlyList<ITool>> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnNext(IReadOnlyList<ITool> value) { if (Initial is null) Initial = value; else Next.TrySetResult(value); }
        public void OnError(Exception error) => Next.TrySetException(error);
        public void OnCompleted() => Next.TrySetException(new InvalidOperationException("Unexpected end"));
    }

    private sealed class Receiver(Action<IReadOnlyList<ITool>>? action = null) : IObserver<IReadOnlyList<ITool>>
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyList<ITool>> _snapshots = [];
        private TaskCompletionSource<bool> _changed = Signal();
        internal TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnNext(IReadOnlyList<ITool> tools)
        {
            lock (_gate) { _snapshots.Add(tools); _changed.TrySetResult(true); _changed = Signal(); }
            action?.Invoke(tools);
        }
        public void OnError(Exception error) => Error.TrySetResult(error);
        public void OnCompleted() => Error.TrySetResult(new InvalidOperationException("Unexpected completion"));
        internal async Task<IReadOnlyList<ITool>> WaitForCount(int count)
        {
            while (true)
            {
                Task pending;
                lock (_gate) { if (_snapshots.Count >= count) return _snapshots[count - 1]; pending = _changed.Task; }
                await Wait(pending);
            }
        }
    }

    private sealed class ManualWatcher : FileSystemWatcher
    {
        internal Exception? DisposalFailure;
        internal Action? Disposing;
        internal void Fail(Exception error) => OnError(new(error));
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposing?.Invoke();
            base.Dispose(disposing);
            if (disposing && DisposalFailure is { } error) throw error;
        }
    }

    private sealed class WarningLogger(Action<Exception> action) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) action(exception); }
    }

    private sealed class ContextOwner : IToolProvider, IAsyncDisposable
    {
        public Agent Agent { get; }
        public ToolProviderContext Context { get; private set; } = null!;
        public ContextOwner() => Agent = new(new() { Model = new UncalledModel(), ToolProviders = [this], BackgroundTasks = null });
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Context = context; return default; }
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(Array.Empty<ITool>());
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask DisposeAsync() => Agent.DisposeAsync();
    }

    private sealed class UncalledModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A tool declaration must not invoke a model.");
    }

    private sealed class FixedLengthSource(byte[] data, long openedLength) : Stream
    {
        private readonly MemoryStream _data = new(data, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => openedLength;
        public override long Position { get => _data.Position; set => _data.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _data.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _data.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _data.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "managed-provider-" + Guid.NewGuid().ToString("N"));
        public string Side => Path.Combine(Root, "version.txt");
        public Workspace()
        {
            Directory.CreateDirectory(Root);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "ModuleFixture", Module), Path.Combine(Root, Module));
            File.WriteAllText(Side, "initial");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
