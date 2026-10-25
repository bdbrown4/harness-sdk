using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxVendedToolRegistrationTests
{
    [Fact]
    public async Task DefaultLocalVendingIsEmptyWithoutExecutingAnything()
    {
        await using var agent = new Agent(new() { Model = new NeverModel() });
        await agent.InitializeAsync();
        Assert.Empty(agent.Tools);
        Assert.Empty(Assert.IsType<Core.NotASandboxLocalEnvironment>(agent.Sandbox).GetTools());
    }

    [Fact]
    public async Task InitializationPublishesTheBorrowedBoundToolOnceWithExactOperationToken()
    {
        var selected = new VendingSandbox();
        var tool = selected.Tool("vended", "owner"); selected.Tools = [tool];
        await using var agent = Make(selected);
        await agent.InitializeAsync(); await agent.InitializeAsync();
        Assert.Same(tool, Assert.Single(agent.Tools));
        using var caller = new CancellationTokenSource();
        var input = JsonSerializer.SerializeToElement(new { });
        Assert.Equal("owner", await tool.InvokeAsync(input, new(agent, new("call", "vended", input), new Dictionary<string, object?>()), caller.Token));
        Assert.Equal(caller.Token, selected.BodyToken);
        Assert.Same(agent, selected.BodyAgent);
        Assert.Equal(1, selected.GetToolsCalls);
        Assert.Equal(1, selected.BodyCalls);
        Assert.Equal(0, selected.EnvironmentCalls);
        await agent.DisposeAsync(); Assert.Equal(0, selected.DisposeCalls);
    }

    [Fact]
    public async Task UserExactNameWinsWhileOnlyRemainingVendedToolsAreAppended()
    {
        var selected = new VendingSandbox();
        var user = selected.Tool("shared", "user"); var keep = selected.Tool("keep", "keep");
        var skipped = selected.Tool("shared", "sandbox"); var added = selected.Tool("added", "sandbox");
        selected.Tools = [skipped, added];
        await using var agent = Make(selected, [user, keep]);
        var original = agent.Tools;
        await agent.InitializeAsync();
        Assert.Collection(agent.Tools, item => Assert.Same(user, item), item => Assert.Same(keep, item), item => Assert.Same(added, item));
        Assert.Collection(original, item => Assert.Same(user, item), item => Assert.Same(keep, item));
        Assert.Equal(0, selected.BodyCalls);
    }

    [Fact]
    public async Task PluginsAndInitializedObserverSeeTheSameCompletedVendedSnapshot()
    {
        var selected = new VendingSandbox(); var tool = selected.Tool("vended", "owner"); selected.Tools = [tool];
        var plugin = new ObservingPlugin(tool); var hooks = new HookRegistry(); IReadOnlyList<ITool>? initialized = null;
        hooks.Register<InitializedEvent>((e, _) => { Assert.Same(tool, Assert.Single(e.Agent.Tools)); initialized = e.Agent.Tools; return default; });
        await using var agent = new Agent(new() { Model = new NeverModel(), Sandbox = selected, Plugins = [plugin], Hooks = hooks });
        await agent.InitializeAsync();
        Assert.Same(tool, Assert.Single(plugin.Snapshot!));
        Assert.Same(tool, Assert.Single(initialized!));
        Assert.Same(agent.Tools, initialized);
    }

    [Fact]
    public async Task InvalidLaterSpecRetainsTheOriginalSnapshotAndFailureWithoutPartialPublication()
    {
        var selected = new VendingSandbox(); var user = selected.Tool("user", "user");
        var failure = new IOException("later specification"); selected.Tools = [selected.Tool("first", "first"), new BrokenTool(failure)];
        await using var agent = Make(selected, [user]); var original = agent.Tools;
        Assert.Same(failure, await Record.ExceptionAsync(async () => await agent.InitializeAsync()));
        Assert.Same(user, Assert.Single(original)); Assert.Same(user, Assert.Single(agent.Tools));
        Assert.Same(user.Spec, Assert.Single(agent.Tools).Spec);
        Assert.Equal(0, selected.BodyCalls); Assert.Equal(0, selected.EnvironmentCalls);
    }

    [Fact]
    public async Task LaterFilterFailureCannotPublishEarlierVendedEntries()
    {
        var selected = new VendingSandbox(); var user = selected.Tool("user", "user");
        selected.Tools = [selected.Tool("first", "first"), selected.Tool("second", "second")]; var failure = new IOException("filter");
        await using var agent = new Agent(new() { Model = new NeverModel(), Sandbox = selected, Tools = [user], ToolFilter = tool => tool.Spec.Name == "second" ? throw failure : true });
        var original = agent.Tools;
        Assert.Same(failure, await Record.ExceptionAsync(async () => await agent.InitializeAsync()));
        Assert.Same(user, Assert.Single(original)); Assert.Same(user, Assert.Single(agent.Tools));
        Assert.Same(user.Spec, Assert.Single(agent.Tools).Spec);
        Assert.Equal(0, selected.BodyCalls);
    }

    [Fact]
    public async Task CallerCancellationDuringVendingPreventsPublicationAndKeepsCallerToken()
    {
        using var caller = new CancellationTokenSource(); var selected = new VendingSandbox();
        selected.Tools = [selected.Tool("vended", "owner")]; selected.OnGetTools = caller.Cancel;
        await using var agent = Make(selected); var original = agent.Tools;
        var caught = await Record.ExceptionAsync(async () => await agent.InitializeAsync(caller.Token));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        Assert.Empty(original); Assert.Empty(agent.Tools); Assert.Equal(0, selected.BodyCalls);
        caller.Cancel();
    }

    [Fact]
    public async Task DuplicateVendedNamesKeepTheFirstAcceptedObjectAndSourceOrder()
    {
        var selected = new VendingSandbox(); var first = selected.Tool("first", "first"); var last = selected.Tool("last", "last");
        selected.Tools = [first, selected.Tool("first", "duplicate"), last];
        await using var agent = Make(selected); await agent.InitializeAsync();
        Assert.Collection(agent.Tools, item => Assert.Same(first, item), item => Assert.Same(last, item));
        Assert.Equal(0, selected.BodyCalls); Assert.Equal(0, selected.EnvironmentCalls);
    }

    private static Agent Make(VendingSandbox selected, IReadOnlyList<ITool>? tools = null) => new(new() { Model = new NeverModel(), Sandbox = selected, Tools = tools ?? [] });
    private sealed class NeverModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fake vending tests never invoke a model or process.");
    }
    private sealed class ObservingPlugin(ITool expected) : IPlugin
    {
        public string Name => "vending-observer";
        internal IReadOnlyList<ITool>? Snapshot;
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { Assert.Same(expected, Assert.Single(agent.Tools)); Snapshot = agent.Tools; return default; }
    }
    private sealed class BrokenTool(Exception failure) : ITool
    {
        public ToolSpec Spec => throw failure;
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Never execute broken metadata.");
    }
    private sealed class VendingSandbox : Core.Sandbox, IDisposable
    {
        internal IReadOnlyList<ITool> Tools = [];
        internal Action? OnGetTools;
        internal int GetToolsCalls, BodyCalls, EnvironmentCalls, DisposeCalls;
        internal CancellationToken BodyToken;
        internal Agent? BodyAgent;
        internal ITool Tool(string name, string value) => new FunctionTool(new(name, "Fake selected environment tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) =>
        { BodyCalls++; BodyToken = token; BodyAgent = context.Agent; return new(value); });
        public override IReadOnlyList<ITool> GetTools() { GetToolsCalls++; OnGetTools?.Invoke(); return Tools; }
        private Exception NoEnvironment() { EnvironmentCalls++; return new InvalidOperationException("No filesystem, command, interpreter or other process allowed."); }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<IReadOnlyList<Strands.Agents.Files.FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public void Dispose() => DisposeCalls++;
    }
}
