using System.Runtime.CompilerServices;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class OwnedResourcesCompatibilityTests
{
    [Fact]
    public async Task ReverseCleanupRetainsTheFirstFailureAndAttemptsTheOwnedModel()
    {
        var order = new List<string>();
        var primary = new IOException("second");
        var secondary = new InvalidOperationException("first");
        var modelFailure = new ApplicationException("model");
        var first = new Resource(() => { order.Add("first"); throw secondary; });
        var second = new Resource(() => { order.Add("second"); throw primary; });
        var model = new Model(() => { order.Add("model"); throw modelFailure; });
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [first, second] });

        var error = await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask());

        Assert.Same(primary, error);
        Assert.Equal(["second", "first", "model"], order);
        Assert.Collection(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]),
            value => Assert.Same(secondary, value), value => Assert.Same(modelFailure, value));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task ChildrenBorrowParentResourcesAndConstructionCopiesTheCallerList()
    {
        var original = new Resource();
        var replacement = new Resource();
        var childResource = new Resource();
        var resources = new List<IAsyncDisposable> { original };
        var model = new Model();
        var parent = new Agent(new() { Model = model, OwnedResources = resources });
        resources[0] = replacement;

        await parent.CreateChild().DisposeAsync();
        await parent.CreateChild(options => options with { OwnedResources = [childResource] }).DisposeAsync();
        Assert.Equal(0, original.Disposals);
        Assert.Equal(1, childResource.Disposals);
        await parent.DisposeAsync();

        Assert.Equal(1, original.Disposals);
        Assert.Equal(0, replacement.Disposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task OwnershipValidationUsesIdentityAndDoesNotDisposeRejectedInputs()
    {
        var first = new ValueEqualResource();
        var second = new ValueEqualResource();
        var model = new Model();
        Assert.Equal(first, second);
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = model, OwnedResources = null! }));
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = model, OwnedResources = [null!] }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, OwnedResources = [first, first] }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [model] }));
        Assert.Equal(0, first.Disposals);
        Assert.Equal(0, model.Disposals);

        var valid = new Agent(new() { Model = model, OwnedResources = [first, second] });
        await valid.DisposeAsync();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public async Task ConcurrentDisposalSharesOneAsynchronousResourceCleanup()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new PendingResource(release.Task);
        var model = new Model();
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [resource] });

        var first = agent.DisposeAsync().AsTask();
        var second = agent.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(0, model.Disposals);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, model.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("closed"));
    }

    private sealed class Resource(Action? onDispose = null) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; onDispose?.Invoke(); return default; }
    }

    private sealed record ValueEqualResource : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class PendingResource(Task release) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public async ValueTask DisposeAsync() { Disposals++; await release; }
    }

    private sealed class Model(Action? onDispose = null) : IModel, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; onDispose?.Invoke(); return default; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
