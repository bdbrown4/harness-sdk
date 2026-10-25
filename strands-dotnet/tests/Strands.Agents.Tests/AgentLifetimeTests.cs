using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentLifetimeTests
{
    [Fact]
    public async Task OwnedResourcesAreDisposedInReverseOrderBeforeTheOwnedModel()
    {
        var order = new List<string>();
        var first = new Resource("first", order);
        var second = new Resource("second", order);
        var model = new Model { OnDispose = () => order.Add("model") };
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [first, second] });

        await agent.DisposeAsync();
        await agent.DisposeAsync();

        Assert.Equal(["second", "first", "model"], order);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task DisposalAttemptsEveryResourceAndTheModelWhileRetainingTheFirstFailure()
    {
        var order = new List<string>();
        var primary = new IOException("second resource failed");
        var earlierFailure = new InvalidOperationException("first resource failed");
        var modelFailure = new ApplicationException("model failed");
        var first = new Resource("first", order) { OnDispose = () => throw earlierFailure };
        var second = new Resource("second", order) { OnDispose = () => throw primary };
        var model = new Model { OnDispose = () => { order.Add("model"); throw modelFailure; } };
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [first, second] });

        var error = await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask());

        Assert.Same(primary, error);
        Assert.Equal(["second", "first", "model"], order);
        var secondary = Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]);
        Assert.Collection(secondary, value => Assert.Same(earlierFailure, value), value => Assert.Same(modelFailure, value));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task ConstructionSnapshotsTheOwnedResourceList()
    {
        var order = new List<string>();
        var original = new Resource("original", order);
        var replacement = new Resource("replacement", order);
        var resources = new List<IAsyncDisposable> { original };
        var agent = new Agent(new() { Model = new Model(), OwnedResources = resources });
        resources[0] = replacement;

        await agent.DisposeAsync();

        Assert.Equal(["original"], order);
        Assert.Equal(1, original.Disposals);
        Assert.Equal(0, replacement.Disposals);
    }

    [Fact]
    public void DuplicateResourceReferencesAreRejectedBeforeOwnershipTransfers()
    {
        var resource = new Resource("resource", []);
        var model = new Model();

        var error = Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [resource, resource] }));

        Assert.Equal(nameof(AgentOptions.OwnedResources), error.ParamName);
        Assert.Equal(0, resource.Disposals);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task DefaultChildrenBorrowParentResourcesWhileExplicitChildResourcesRemainOwned()
    {
        var order = new List<string>();
        var parentResource = new Resource("parent", order);
        var childResource = new Resource("child", order);
        var parent = new Agent(new() { Model = new Model(), OwnedResources = [parentResource] });

        await parent.CreateChild().DisposeAsync();
        Assert.Equal(0, parentResource.Disposals);
        await parent.CreateChild(options => options with { OwnedResources = [childResource] }).DisposeAsync();
        Assert.Equal(["child"], order);
        Assert.Equal(0, parentResource.Disposals);
        await parent.DisposeAsync();
        Assert.Equal(["child", "parent"], order);
    }

    [Fact]
    public void InvalidOwnershipListsAreRejectedWithoutDisposingCallerResources()
    {
        var model = new Model();
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = model, OwnedResources = null! }));
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = model, OwnedResources = [null!] }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [model] }));
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task DistinctValueEqualResourcesAreEachDisposed()
    {
        var first = new ValueEqualResource();
        var second = new ValueEqualResource();
        Assert.Equal(first, second);
        var agent = new Agent(new() { Model = new Model(), OwnedResources = [first, second] });

        await agent.DisposeAsync();

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public async Task ConcurrentDisposalSharesOnePendingCleanupAndDoesNotCloseTheModelEarly()
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
    }

    [Fact]
    public async Task DisposalDoesNotInferOwnershipFromToolsPluginsOrBorrowedModels()
    {
        var model = new Model();
        var tool = new BorrowedTool();
        var plugin = new PendingPlugin(Task.CompletedTask);
        var agent = new Agent(new() { Model = model, Tools = [tool], Plugins = [plugin] });
        await agent.InitializeAsync();

        await agent.DisposeAsync();

        Assert.Equal(0, model.Disposals);
        Assert.Equal(0, tool.Disposals);
        Assert.Equal(0, plugin.Disposals);
    }

    [Fact]
    public async Task DisposalRejectsPendingInitializationWithoutClosingOwnedResources()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new PendingPlugin(release.Task);
        var resource = new Resource("resource", []);
        var agent = new Agent(new() { Model = new Model(), Plugins = [plugin], OwnedResources = [resource] });
        var initialization = agent.InitializeAsync();
        await plugin.Started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.DisposeAsync().AsTask());
        Assert.Equal(0, resource.Disposals);
        release.SetResult(true);
        await initialization;
        await agent.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task OwnedModelIsDisposedOnceWhileCallerOwnedModelsAndApplicationHooksRemainUsable()
    {
        var owned = new Model();
        var external = new Model();
        var agent = new Agent(new() { Model = owned, OwnsModel = true });
        await agent.DisposeAsync();
        await agent.DisposeAsync();
        Assert.Equal(1, owned.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("closed"));
        await new Agent(new() { Model = external }).DisposeAsync();
        Assert.Equal(0, external.Disposals);
    }

    [Fact]
    public async Task ChildDisposalDoesNotCloseItsParentsOwnedModel()
    {
        var model = new Model();
        var parent = new Agent(new() { Model = model, OwnsModel = true });
        await parent.CreateChild().DisposeAsync();
        Assert.Equal(0, model.Disposals);
        await parent.DisposeAsync();
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task DisposalRejectsAnActiveStreamAndCanBeRetriedAfterItsCleanup()
    {
        var model = new Model();
        var resource = new Resource("resource", []);
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [resource] });
        await using (var stream = agent.StreamAsync("question").GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.DisposeAsync().AsTask());
            Assert.Equal(0, model.Disposals);
            Assert.Equal(0, resource.Disposals);
        }
        await agent.DisposeAsync();
        Assert.Equal(1, model.Disposals);
        Assert.Equal(1, resource.Disposals);
    }

    private sealed class Model : IModel, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public Action? OnDispose { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in AgentTests.Text("answer")) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
        public ValueTask DisposeAsync() { Disposals++; OnDispose?.Invoke(); return default; }
    }

    private sealed class Resource(string name, List<string> order) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public Action? OnDispose { get; init; }
        public ValueTask DisposeAsync() { Disposals++; order.Add(name); OnDispose?.Invoke(); return default; }
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

    private sealed class BorrowedTool : ITool, IAsyncDisposable
    {
        public ToolSpec Spec { get; } = new("borrowed", "Caller-owned tool", JsonSerializer.SerializeToElement(new { type = "object" }));
        public int Disposals { get; private set; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("ok");
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class PendingPlugin(Task release) : IPlugin, IAsyncDisposable
    {
        public string Name => "pending";
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Started.SetResult(true); await release; }
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
