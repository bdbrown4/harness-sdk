using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.EventParity;

public sealed class CompletedBlockEventTests
{
    [Fact]
    public async Task InterleavedBlocksAreObservedInCompletionOrderWhileTheMessageKeepsIndexOrder()
    {
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(1), new ModelContentBlockDeltaEvent(1, Text: "answer"), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Reasoning: "thought"), new ModelContentBlockStopEvent(1), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
        var hooks = new HookRegistry(); var blocks = new List<ContentBlock>(); hooks.Register<ContentBlockEvent>((value, _) => { blocks.Add(value.ContentBlock); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(events), Hooks = hooks });
        var result = await agent.InvokeAsync("question");
        Assert.Equal("answer", Assert.IsType<TextBlock>(blocks[0]).Text); Assert.Equal("thought", Assert.IsType<ReasoningBlock>(blocks[1]).Text);
        Assert.IsType<ReasoningBlock>(result.Message!.Content[0]); Assert.IsType<TextBlock>(result.Message.Content[1]); Assert.NotSame(blocks[0], result.Message.Content[1]);
    }

    [Fact]
    public async Task ProviderNativeCompletedBlocksKeepTheirOpaqueFieldsAndDetachedIdentity()
    {
        var native = new GoogleContentBlock(JsonSerializer.SerializeToElement(new { text = "answer", thoughtSignature = "opaque-signature" }));
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "answer"), new ModelContentBlockStopEvent(0) { ContentBlock = native }, new ModelMessageStopEvent(StopReasons.EndTurn)];
        GoogleContentBlock? observed = null; var hooks = new HookRegistry(); hooks.Register<ContentBlockEvent>((value, _) => { observed = Assert.IsType<GoogleContentBlock>(value.ContentBlock); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(events), Hooks = hooks });
        var result = await agent.InvokeAsync("question");
        Assert.NotNull(observed); Assert.Equal("opaque-signature", observed!.Part.GetProperty("thoughtSignature").GetString());
        Assert.NotSame(native, observed); Assert.NotSame(observed, Assert.IsType<GoogleContentBlock>(Assert.Single(result.Message!.Content)));
    }

    [Fact]
    public async Task ToolBlockCompletionIncludesAllArgumentFragmentsAndPreservesProviderMetadata()
    {
        var request = new ToolUseBlock("call", "work", JsonSerializer.SerializeToElement(new { })) { ProviderMetadata = JsonSerializer.SerializeToElement(new { receipt = "provider" }) };
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, request), new ModelContentBlockDeltaEvent(0, ToolInput: "{\"n\":"), new ModelContentBlockDeltaEvent(0, ToolInput: "2}"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
        var hooks = new HookRegistry(); ToolUseBlock? observed = null; var executions = 0;
        hooks.Register<ContentBlockEvent>((value, _) => { if (value.ContentBlock is ToolUseBlock call) observed = call; return default; });
        var tool = new FunctionTool(new("work", "work", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => { Assert.Equal(2, input.GetProperty("n").GetInt32()); executions++; return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = new EventTestModel(events, EventTestModel.Text("complete")), Hooks = hooks, Tools = [tool] });
        await agent.InvokeAsync("question");
        Assert.NotNull(observed); Assert.Equal("call", observed!.ToolUseId); Assert.Equal(2, observed.Input.GetProperty("n").GetInt32());
        Assert.Equal("provider", observed.ProviderMetadata!.Value.GetProperty("receipt").GetString()); Assert.Equal(1, executions);
    }

    [Fact]
    public async Task InvalidToolArgumentsCannotProduceACompletedBlockOrMessage()
    {
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call", "work", JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockDeltaEvent(0, ToolInput: "invalid json"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
        var hooks = new HookRegistry(); var blocks = 0; var messages = 0;
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; }); hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(events), Hooks = hooks });
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("question")); Assert.Equal(0, blocks); Assert.Equal(0, messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedBlocksAreNeverReportedAsCompleted(bool transportFailure)
    {
        var hooks = new HookRegistry(); var blocks = 0; var messages = 0; var primary = new IOException("transport failed");
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; }); hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        await using var agent = new Agent(new() { Model = new PartialModel(transportFailure, primary), Hooks = hooks });
        var failure = await Record.ExceptionAsync(() => agent.InvokeAsync("question"));
        if (transportFailure) Assert.Same(primary, failure); else Assert.IsType<ModelException>(failure);
        Assert.Equal(0, blocks); Assert.Equal(0, messages);
    }

    private sealed class PartialModel(bool fail, Exception error) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in EventTestModel.Text("unfinished").Take(3)) { yield return item; await Task.Yield(); }
            if (fail) throw error;
        }
    }
}
