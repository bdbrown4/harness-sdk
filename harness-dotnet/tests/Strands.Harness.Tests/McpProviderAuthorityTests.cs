using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents;
using Strands.Agents.Mcp;
using Strands.Harness.Testing;
using Xunit;

namespace Strands.Harness.Tests;

public sealed class McpProviderAuthorityTests
{
    [Fact]
    public async Task ChildAndGrandchildKeepOnlySelectedProvidersAndUseTheirOwnDelegationBindings()
    {
        await using var selected = new McpTestServer(); await using var excluded = new McpTestServer();
        using var httpA = new HttpClient(); using var httpB = new HttpClient();
        var a = McpProviderHarnessTests.Provider(selected, httpA, "a", "a"); var b = McpProviderHarnessTests.Provider(excluded, httpB, "b", "b");
        var local = new RichBorrowedTool(); var generic = new RecordingProvider(local); var initialized = new List<Agent>();
        var hooks = new HookRegistry(); hooks.Register<InitializedEvent>((value, _) => { initialized.Add(value.Agent); return default; });
        var model = new GatedModel(); Agent? parent = null; Task<ToolResultBlock>? childCall = null;
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            {
                BuiltinTools = ["subagent"],
                Subagent = new() { MaxDepth = 3 },
                AgentOptions = new() { Model = model, ToolProviders = [a, generic, b], Hooks = hooks }
            });
            childCall = Delegate(parent, new { task = "child", tools = new[] { "subagent", "local" }, mcp_servers = new[] { "a" } }, safety.Token).AsTask();
            await Bounded(model.Entered.Task); var child = initialized[1];
            Assert.Equal(new[] { "a_echo", "local", "subagent" }, child.Tools.Select(tool => tool.Spec.Name).OrderBy(value => value, StringComparer.Ordinal));
            Assert.Same(local, Assert.Single(child.Tools, tool => tool.Spec.Name == "local"));
            Assert.Same(Assert.Single(parent.Tools, tool => tool.Spec.Name == "a_echo"), Assert.Single(child.Tools, tool => tool.Spec.Name == "a_echo"));
            Assert.Equal(new[] { "a" }, ServerNames(child));
            Assert.Equal(2, generic.Added.Count); Assert.NotEqual(generic.Added[0].ConsumerId, generic.Added[1].ConsumerId);
            Assert.Same(child, generic.Added[1].Agent);
            await parent.DisposeAsync(); Assert.Equal(1, excluded.Deletions); Assert.Equal(0, selected.Deletions);
            var rich = await child.InvokeToolAsync(new("local-call", "local", JsonSerializer.SerializeToElement(new { })));
            Assert.Equal(ToolResultStatus.Success, rich.Status); Assert.IsType<ImageBlock>(rich.ContentBlocks[0]); Assert.Equal(JsonValueKind.False, rich.StructuredContent!.Value.ValueKind);
            Assert.Equal("local", rich.Metadata!.Value.GetProperty("source").GetString()); Assert.Equal(1, local.StreamCalls); Assert.False(local.Disposed);
            var grandchildResult = await Delegate(child, new { task = "grandchild", tools = new[] { "local" }, mcp_servers = new[] { "b" } }, safety.Token);
            Assert.Equal(ToolResultStatus.Success, grandchildResult.Status);
            Assert.Equal("local", Assert.Single(model.Requests[1].Tools!).Name);
            Assert.Equal(3, generic.Added.Count); Assert.Equal(2, generic.Removed.Count); // parent and completed grandchild
            Assert.Equal(1, selected.Initializations); Assert.Equal(1, excluded.Initializations);
            Assert.Equal(0, selected.Deletions);
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await childCall).Status);
            Assert.Equal(1, selected.Deletions); Assert.Equal(3, generic.Removed.Count); Assert.False(generic.Disposed); Assert.False(local.Disposed);
        }
        finally
        {
            model.Release.TrySetResult(true);
            if (childCall is not null) await Record.ExceptionAsync(async () => await childCall);
            if (parent is not null) await parent.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyServerSelectionCannotRegainMcpToolsThroughOrdinaryNamesOrInheritedDeclarations(bool unknownName)
    {
        await using var server = new McpTestServer(); using var http = new HttpClient();
        var provider = McpProviderHarnessTests.Provider(server, http, "docs", "docs"); var generic = new RecordingProvider(new RichBorrowedTool());
        var model = new GatedModel(); Agent? parent = null; Task<ToolResultBlock>? pending = null;
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [provider, generic] } });
            pending = Delegate(parent, new { task = "child", tools = new[] { "docs_echo", "local" }, mcp_servers = unknownName ? new[] { "ungranted" } : Array.Empty<string>() }).AsTask();
            await Bounded(model.Entered.Task); Assert.Equal("local", Assert.Single(model.Requests[0].Tools!).Name);
            await parent.DisposeAsync(); Assert.Equal(1, server.Deletions); Assert.Equal(2, generic.Added.Count);
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await pending).Status); Assert.Equal(2, generic.Removed.Count);
        }
        finally { model.Release.TrySetResult(true); if (pending is not null) await Record.ExceptionAsync(async () => await pending); if (parent is not null) await parent.DisposeAsync(); }
    }

    [Fact]
    public async Task NamelessAndDuplicateProviderDeclarationsAreNeverGrantedThroughAnotherServerName()
    {
        await using var first = new McpTestServer(); await using var duplicate = new McpTestServer(); await using var nameless = new McpTestServer();
        using var h1 = new HttpClient(); using var h2 = new HttpClient(); using var h3 = new HttpClient();
        var firstProvider = McpProviderHarnessTests.Provider(first, h1, "shared", "first");
        var duplicateProvider = McpProviderHarnessTests.Provider(duplicate, h2, "shared", "duplicate");
        var unnamedProvider = McpProviderHarnessTests.Provider(nameless, h3, null, "unnamed");
        var model = new GatedModel(); Agent? parent = null; Task<ToolResultBlock>? pending = null;
        try
        {
            parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
            { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [firstProvider, duplicateProvider, unnamedProvider] } });
            Assert.Equal(new[] { "shared" }, ServerNames(parent));
            pending = Delegate(parent, new { task = "child", tools = new[] { "duplicate_echo", "unnamed_echo" }, mcp_servers = new[] { "shared" } }).AsTask();
            await Bounded(model.Entered.Task); Assert.Equal("first_echo", Assert.Single(model.Requests[0].Tools!).Name);
            await parent.DisposeAsync(); Assert.Equal(1, duplicate.Deletions); Assert.Equal(1, nameless.Deletions); Assert.Equal(0, first.Deletions);
            model.Release.TrySetResult(true); Assert.Equal(ToolResultStatus.Success, (await pending).Status); Assert.Equal(1, first.Deletions);
        }
        finally { model.Release.TrySetResult(true); if (pending is not null) await Record.ExceptionAsync(async () => await pending); if (parent is not null) await parent.DisposeAsync(); }
    }

    [Fact]
    public async Task RenamedProviderMetadataIsRejectedBeforeAnyChildPublicationOrInitializedObserver()
    {
        var tool = new MutableNameTool(); var provider = new RecordingProvider(tool); var publishedForbidden = false; var model = new GatedModel();
        var hooks = new HookRegistry(); hooks.Register<InitializedEvent>((value, _) =>
        { if (value.Agent.Tools.Any(item => item.Spec.Name == "forbidden")) publishedForbidden = true; return default; });
        await using var parent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
        { BuiltinTools = ["subagent"], AgentOptions = new() { Model = model, ToolProviders = [provider], Hooks = hooks } });
        tool.Name = "forbidden";
        var result = await Delegate(parent, new { task = "child", tools = new[] { "allowed" }, mcp_servers = Array.Empty<string>() });
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("outside its selected scope", result.Content); Assert.False(publishedForbidden);
        Assert.Empty(model.Requests); Assert.Equal(0, tool.BodyCalls); Assert.Single(provider.Removed);
    }

    private sealed class MutableNameTool : ITool
    {
        internal string Name { get; set; } = "allowed";
        internal int BodyCalls { get; private set; }
        public ToolSpec Spec => new(Name, "Mutable provider metadata", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { BodyCalls++; return new("body"); }
    }

    internal static string?[] ServerNames(Agent agent) => Assert.Single(agent.Tools, tool => tool.Spec.Name == "subagent").Spec.InputSchema.GetProperty("properties")
        .GetProperty("mcp_servers").GetProperty("items").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
    internal static ValueTask<ToolResultBlock> Delegate(Agent agent, object arguments, CancellationToken cancellationToken = default)
    {
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools, value => value.Spec.Name == "subagent")); var input = JsonSerializer.SerializeToElement(arguments);
        return tool.InvokeResultAsync(input, new(agent, new("delegate-call", "subagent", input), new Dictionary<string, object?>()), cancellationToken);
    }
    internal static async Task Bounded(Task task)
    { if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))))) throw new TimeoutException("Expected provider boundary did not become ready."); await task; }
    internal sealed class RecordingProvider(ITool tool) : IToolProvider, IDisposable
    {
        internal List<ToolProviderContext> Added { get; } = [];
        internal List<ToolProviderContext> Removed { get; } = [];
        internal bool Disposed { get; private set; }
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Added.Add(context); return default; }
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(new[] { tool });
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removed.Add(context); return default; }
        public void Dispose() => Disposed = true;
    }
    internal sealed class RichBorrowedTool : IStreamingTool, IRichTool, IDisposable
    {
        public ToolSpec Spec { get; } = new("local", "A borrowed rich stream", JsonSerializer.SerializeToElement(new { type = "object" }));
        internal int StreamCalls { get; private set; }
        internal bool Disposed { get; private set; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("unused text path");
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The stream capability should remain selected.");
        public async IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); StreamCalls++; yield return new ToolStreamEvent("progress");
            yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, new ContentBlock[] { new ImageBlock("image/png", Convert.ToBase64String(new byte[] { 0, 255 })) })
            { StructuredContent = JsonSerializer.SerializeToElement(false), Metadata = JsonSerializer.SerializeToElement(new { source = "local" }) });
        }
        public void Dispose() => Disposed = true;
    }
    internal sealed class GatedModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1) { Entered.TrySetResult(true); await Release.Task; }
            cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "complete"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
