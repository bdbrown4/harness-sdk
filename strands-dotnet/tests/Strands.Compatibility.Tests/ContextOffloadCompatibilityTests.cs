using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents;
using Strands.Agents.Context;

namespace Strands.Compatibility.Tests;

public sealed class ContextOffloadCompatibilityTests
{
    [Fact]
    public async Task OffloadsModelRequestAndRetrievesTypedOriginalThroughStandardAsset()
    {
        var text = new string('x', 8_000);
        var model = new TextModel();
        var result = new ToolResultBlock("call", [new TextBlock(text), new ImageBlock("image/png", "AA=="), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 42 }))]);
        var agent = new Agent(new() { Model = model, Messages = History(result), Plugins = [new ToolResultOffloader()] });
        await agent.InvokeAsync(null);
        var projected = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.Contains("[Truncated:", projected.Content);
        Assert.True(projected.Content.Length < text.Length);
        Assert.IsType<ImageBlock>(projected.ContentBlocks[1]);
        var reference = Reference(model.Requests[0]);
        Assert.Equal(text, (await Retrieve(agent, reference)).Content);
        var jsonReference = reference.Substring(0, reference.LastIndexOf('_') + 1) + "2";
        Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single((await Retrieve(agent, jsonReference)).ContentBlocks)).Json.GetProperty("value").GetInt32());
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
        Assert.Empty(agent.State.GetAll());
    }

    [Fact]
    public async Task RestoredFileSessionReconstructsReferencesWithoutPrivateApplicationState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-context-compat", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            var model = new TextModel();
            var first = new Agent(new() { Model = model, Messages = History(new("call", new string('x', 8_000))), Plugins = [new ToolResultOffloader()], SessionStore = store, SessionId = "offload" });
            await first.InvokeAsync(null);
            var reference = Reference(model.Requests[0]);
            var nextModel = new TextModel();
            var next = new Agent(new() { Model = nextModel, Plugins = [new ToolResultOffloader()], SessionStore = store, SessionId = "offload" });
            await next.InvokeAsync("continue");
            Assert.Equal(reference, Reference(nextModel.Requests[0]));
            Assert.Equal(8_000, (await Retrieve(next, reference)).Content.Length);
            Assert.Empty((await store.LoadAsync("offload"))!.State!);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SharedOffloaderRejectsParentReferenceFromFreshChild()
    {
        var model = new TextModel();
        var parent = new Agent(new() { Model = model, Messages = History(new("call", new string('x', 8_000))), Plugins = [new ToolResultOffloader()] });
        await parent.InvokeAsync(null);
        var reference = Reference(model.Requests[0]);
        var child = parent.CreateChild();
        await child.InitializeAsync();
        Assert.Equal(ToolResultStatus.Error, (await Retrieve(child, reference)).Status);
        Assert.Equal(ToolResultStatus.Success, (await Retrieve(parent, reference)).Status);
    }

    [Fact]
    public async Task RetrievalHonorsCancellationAndBoundsSearchResults()
    {
        var model = new TextModel();
        var agent = new Agent(new() { Model = model, Messages = History(new("call", new string('x', 8_000))), Plugins = [new ToolResultOffloader(new() { MaxRetrievalTokens = 40 })] });
        await agent.InvokeAsync(null);
        var reference = Reference(model.Requests[0]);
        var input = JsonSerializer.SerializeToElement(new { reference, pattern = "x", context_lines = int.MaxValue });
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools));
        var context = new ToolContext(agent, new("retrieve", tool.Spec.Name, input), new Dictionary<string, object?>());
        var result = await tool.InvokeResultAsync(input, context);
        Assert.Equal(160, result.Content.Length);
        Assert.EndsWith("[output truncated, narrow your search]", result.Content);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeResultAsync(input, context, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static Message[] History(ToolResultBlock result) => [Message.User("read"), new(Role.Assistant, [new ToolUseBlock(result.ToolUseId, "large", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [result], "result-message")];
    private static string Reference(ModelRequest request) => Regex.Match(Assert.IsType<ToolResultBlock>(request.Messages[2].Content[0]).Content, @"\[(ctx_[^\]]+)\]").Groups[1].Value;
    private static ValueTask<ToolResultBlock> Retrieve(Agent agent, string reference)
    {
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools));
        var input = JsonSerializer.SerializeToElement(new { reference });
        return tool.InvokeResultAsync(input, new(agent, new("retrieve", tool.Spec.Name, input), new Dictionary<string, object?>()));
    }
    private sealed class TextModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.Yield();
        }
    }
}
