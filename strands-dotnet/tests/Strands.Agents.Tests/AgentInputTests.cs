using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentInputTests
{
    [Fact]
    public async Task TypedUserInputPreservesOrderAndCallerStateWithExistingHistory()
    {
        var model = new CaptureModel();
        var state = new Dictionary<string, object?> { ["shared"] = new object() };
        var hooks = new HookRegistry();
        IReadOnlyDictionary<string, object?>? observed = null;
        hooks.Register<BeforeInvocationEvent>((item, _) => { observed = item.InvocationState; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Messages = [Message.User("earlier"), Message.Assistant("answer")] });
        ContentBlock[] content = [new TextBlock("inspect"), new ImageBlock("image/png", "AQID"), new AudioBlock("audio/wav", "BAUG"), new JsonBlock(JsonSerializer.SerializeToElement(new { number = 42 })), new DocumentBlock("application/pdf", "BwgJ", "report")];

        var result = await agent.InvokeContentAsync(content, new() { InvocationState = state });

        var request = Assert.Single(model.Requests);
        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("earlier", request.Messages[0].Text);
        Assert.Equal(Role.User, request.Messages[2].Role);
        Assert.Equal(content, request.Messages[2].Content);
        Assert.Equal(42, Assert.IsType<JsonBlock>(request.Messages[2].Content[3]).Json.GetProperty("number").GetInt32());
        Assert.Same(state, observed);
        Assert.Same(state, result.InvocationState);
        Assert.Equal("reply", result.ToString());
    }

    [Fact]
    public async Task StreamDetachesContentBeforeEnumerationAndJsonOwnerDisposal()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Model = model });
        var document = JsonDocument.Parse("{\"nested\":{\"value\":7}}");
        ContentBlock[] content = [new TextBlock("original"), new JsonBlock(document.RootElement)];
        var stream = agent.StreamContentAsync(content);
        content[0] = new TextBlock("changed");
        content[1] = new TextBlock("replaced");
        document.Dispose();

        var events = new List<IAgentStreamEvent>();
        await foreach (var item in stream) events.Add(item);

        var message = Assert.Single(model.Requests).Messages[0];
        Assert.Equal("original", message.Text);
        Assert.Equal(7, Assert.IsType<JsonBlock>(message.Content[1]).Json.GetProperty("nested").GetProperty("value").GetInt32());
        var added = events.OfType<MessageAddedEvent>().Where(item => item.Message.Role == Role.User).ToArray();
        var committed = Assert.Single(added).Message;
        Assert.Equal(message.TrackingId, committed.TrackingId);
        Assert.Equal(JsonSerializer.Serialize(message), JsonSerializer.Serialize(committed));
        Assert.Single(events.OfType<AgentResultEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolProtocolBlocksAreRejectedBeforeSessionReadsOrModelCalls(bool toolResult)
    {
        var store = new CountingStore();
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "stored" });
        ContentBlock block = toolResult ? new ToolResultBlock("orphan", "result") : new ToolUseBlock("orphan", "tool", JsonSerializer.SerializeToElement(new { }));

        Assert.Throws<ArgumentException>(() => agent.StreamContentAsync([block]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.InvokeContentAsync([block]));

        Assert.Equal(0, store.Loads);
        Assert.Empty(model.Requests);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task NullContentOrNullBlocksAreRejectedBeforeInitialization()
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new CaptureModel(), SessionStore = store, SessionId = "stored" });

        Assert.Throws<ArgumentNullException>(() => agent.StreamContentAsync(null!));
        Assert.Throws<ArgumentException>(() => agent.StreamContentAsync([null!]));

        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task EmptyTypedInputIsAUserMessageRatherThanAContinuation()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Model = model });

        await agent.InvokeContentAsync([]);

        var message = Assert.Single(Assert.Single(model.Requests).Messages);
        Assert.Equal(Role.User, message.Role);
        Assert.Empty(message.Content);
    }

    [Fact]
    public async Task TypedInputAlsoWorksWithStructuredOutput()
    {
        var model = new CaptureModel { Structured = true };
        await using var agent = new Agent(new() { Model = model });

        var response = await agent.InvokeStructuredContentAsync([new ImageBlock("image/png", "AQID")], new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } })
        });

        Assert.Equal(9, response.Output.Value);
        Assert.IsType<ImageBlock>(Assert.Single(Assert.Single(model.Requests).Messages[0].Content));
    }

    [Fact]
    public async Task CancelledTypedInvocationUsesTheOrdinaryCancellationResult()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Model = model });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await agent.InvokeContentAsync([new TextBlock("unused")], cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
    }

    public sealed record Answer(int Value);

    private sealed class CountingStore : ISessionStore
    {
        internal int Loads;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }

    private sealed class CaptureModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal bool Structured;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0) { ContentBlock = Structured ? new ToolUseBlock("structured", StructuredOutputTool<Answer>.ToolName, JsonSerializer.SerializeToElement(new { value = 9 })) : new TextBlock("reply") };
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(Structured ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
