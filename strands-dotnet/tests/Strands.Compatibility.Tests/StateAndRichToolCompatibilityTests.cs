using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class StateAndRichToolCompatibilityTests
{
    [Fact]
    public async Task ApplicationStateAndRichResultsRoundTripThroughFileSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hooks = new HookRegistry();
            hooks.Register<AfterInvocationEvent>((value, _) => { value.Agent.State.Set("savedAtExit", 42); return default; });
            var first = new Agent(new() { Model = new Model(callTool: true), Tools = new ITool[] { new RichTool() }, Hooks = hooks, SessionStore = new FileSessionStore(directory), SessionId = "state" });
            await first.InvokeAsync("question");
            var second = new Agent(new() { Model = new Model(), SessionStore = new FileSessionStore(directory), SessionId = "state" });
            await second.InitializeAsync();
            Assert.Equal(42, second.State.Get<int>("savedAtExit"));
            var result = Assert.IsType<ToolResultBlock>(second.Messages[2].Content[0]);
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal(7, result.StructuredContent!.Value.GetProperty("number").GetInt32());
            Assert.Equal("image/png", Assert.IsType<ImageBlock>(result.ContentBlocks[1]).MediaType);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RuntimeToolsAreAtomicAndInheritedWithoutConversationOrStateAliasing()
    {
        var parent = new Agent(new() { Model = new Model() });
        var spec = new ToolSpec("tool", "Tool", JsonSerializer.SerializeToElement(new { type = "object" }));
        var tool = new FunctionTool(spec, (_, _, _) => new ValueTask<string>("ok"));
        Assert.Throws<ArgumentException>(() => parent.AddTools(new ITool[] { tool, tool }));
        Assert.Empty(parent.Tools);
        parent.AddTool(tool);
        parent.State.Set("value", 3);
        var child = parent.CreateChild(options => options with { Name = "child" });
        child.State.Set("value", 4);
        Assert.Equal(3, parent.State.Get<int>("value"));
        Assert.Equal("tool", Assert.Single(child.Tools).Spec.Name);
        Assert.Empty(child.Messages);
        await using (var invocation = parent.StreamAsync("start").GetAsyncEnumerator())
        {
            Assert.True(await invocation.MoveNextAsync());
            Assert.Throws<InvalidOperationException>(() => parent.AddTool(new FunctionTool(new("other", "Other", spec.InputSchema), (_, _, _) => new ValueTask<string>("ok"))));
        }
    }

    [Fact]
    public async Task ModelRequestAndToolResultTransformsKeepDurableInputsAndProviderIds()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) => { value.Request = value.Request with { SystemPrompt = "ephemeral" }; return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new("hook-id", "redacted"); return default; });
        var model = new Model(callTool: true);
        var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = new ITool[] { new RichTool() } });
        await agent.InvokeAsync("durable");
        Assert.Equal("ephemeral", model.LastRequest!.SystemPrompt);
        Assert.Null(agent.SystemPrompt);
        Assert.Equal("durable", agent.Messages[0].Text);
        var result = Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]);
        Assert.Equal("provider-id", result.ToolUseId);
        Assert.Equal("redacted", result.Content);
    }

    private sealed class RichTool : IRichTool
    {
        public ToolSpec Spec { get; } = new("rich", "Rich", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The rich path must be used.");
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse("{\"number\":7}");
            return new(new ToolResultBlock(context.ToolUse.ToolUseId, new ContentBlock[] { new TextBlock("partial"), new ImageBlock("image/png", "aGVsbG8=") }, ToolResultStatus.Error, json.RootElement));
        }
    }

    private sealed class Model(bool callTool = false) : IModel
    {
        private int _calls;
        public ModelRequest? LastRequest { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (callTool && _calls++ == 0)
            {
                yield return new ModelContentBlockStartEvent(0, new("provider-id", "rich", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.Yield();
        }
    }
}
