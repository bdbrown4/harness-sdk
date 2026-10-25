using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using static Strands.Agents.Tests.ToolProviders.ToolProviderLifecycleTests;
using static Strands.Agents.Tests.ToolProviders.ToolProviderSnapshotTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderCapabilityTests
{
    [Fact]
    public async Task RichProviderResultKeepsItsExactBodyIdentityAndPersistsRichHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "provider-session-" + Guid.NewGuid().ToString("N"));
        var tool = new RichTool();
        try
        {
            var store = new FileSessionStore(directory);
            var model = new CallingModel("rich", JsonSerializer.SerializeToElement(new { }));
            await using (var agent = new Agent(new() { Model = model, ToolProviders = [new Provider([tool])], SessionStore = store, SessionId = "rich" }))
            {
                await agent.InvokeAsync("run");
                Assert.Same(tool, Assert.Single(agent.Tools)); Assert.Equal(1, tool.RichCalls); Assert.Equal(0, tool.PlainCalls);
                var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
                Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks));
            }
            var restored = await store.LoadAsync("rich");
            var persisted = Assert.Single(restored!.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Equal(7, Assert.IsType<JsonBlock>(Assert.Single(persisted.ContentBlocks)).Json.GetInt32());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StreamingProviderStillEmitsProgressAndTypedTerminalResult()
    {
        var tool = new StreamingFunctionTool(new("streaming", "streaming", JsonSerializer.SerializeToElement(new { type = "object" })), Stream);
        await using var agent = new Agent(new() { Model = new CallingModel("streaming", JsonSerializer.SerializeToElement(new { })), ToolProviders = [new Provider([tool])] });
        var progress = 0;
        await foreach (var item in agent.StreamAsync("run")) if (item is ToolStreamUpdateEvent) progress++;
        Assert.Equal(1, progress); Assert.Same(tool, Assert.Single(agent.Tools));
        var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("streamed", result.Content);
        static async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { yield return new ToolStreamEvent("progress"); yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "streamed")); await Task.CompletedTask; }
    }

    [Fact]
    public async Task StructuredProviderKeepsItsResultCapabilityAndBackgroundExclusion()
    {
        var structured = new StructuredOutputTool<int>(new() { Schema = JsonSerializer.SerializeToElement(new { type = "object" }), Deserializer = _ => 42 });
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [new Provider([structured])], BackgroundTasks = new() });
        var result = await agent.InvokeToolAsync(new("structured", structured.Spec.Name, JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(ToolResultStatus.Success, result.Status); Assert.True(structured.TryGetResult("structured", out var output)); Assert.Equal(42, output);
        Assert.Same(structured, Assert.Single(agent.Tools, tool => ReferenceEquals(tool, structured)));
    }

    [Fact]
    public async Task DelegatingProviderKeepsItsPrivateAdapterIdentityAndEndsParentTurn()
    {
        await using var child = new Agent(new() { Model = new Model() });
        var tool = child.AsTool(new AgentToolOptions { Name = "delegate", Delegate = true });
        var model = new CallingModel("delegate", JsonSerializer.SerializeToElement(new { input = "work" }));
        await using var parent = new Agent(new() { Model = model, ToolProviders = [new Provider([tool])] });
        var result = await parent.InvokeAsync("delegate");
        Assert.Equal("done", result.ToString()); Assert.Single(model.Requests); Assert.Same(tool, Assert.Single(parent.Tools));
    }

    [Fact]
    public async Task HookSelectedUnregisteredToolKeepsExistingSpecificationAndBodyBehavior()
    {
        var replacementCalls = 0;
        var replacement = new FunctionTool(new("replacement", "replacement", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { replacementCalls++; return new ValueTask<string>("replacement"); });
        var hooks = new HookRegistry(); hooks.Register<BeforeToolCallEvent>((before, _) => { before.SelectedTool = replacement; return default; });
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [new Provider([Tool("original")])], Hooks = hooks });
        Assert.Equal("replacement", (await agent.InvokeToolAsync(new("direct", "original", JsonSerializer.SerializeToElement(new { })))).Content);
        Assert.Equal(1, replacementCalls); Assert.Equal("original", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task ReleasingFailedInitializationRemovesOnlyProviderPublications()
    {
        var hooks = new HookRegistry(); hooks.Register<InitializedEvent>((_, _) => throw new IOException("after plugin publication"));
        var agent = new Agent(new() { Model = new Model(), Tools = [Tool("application")], ToolProviders = [new Provider([Tool("provider")])], Plugins = [new Plugin(_ => { }, [Tool("plugin")])], Hooks = hooks });
        await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync());
        Assert.Equal(["application", "plugin"], agent.Tools.Select(tool => tool.Spec.Name));
        await agent.DisposeAsync();
    }

    private sealed class RichTool : IRichTool
    {
        public int PlainCalls { get; private set; }
        public int RichCalls { get; private set; }
        public ToolSpec Spec { get; } = new("rich", "rich", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { PlainCalls++; throw new InvalidOperationException("Rich capability lost."); }
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { RichCalls++; return new(new ToolResultBlock(context.ToolUse.ToolUseId, new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(7)) })); }
    }
}
