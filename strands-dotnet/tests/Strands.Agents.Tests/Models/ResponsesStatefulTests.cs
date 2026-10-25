using System.Net;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ResponsesStatefulTests
{
    [Fact]
    public async Task StatefulAgentForcesStorageAndChainsOnlyNewInput()
    {
        using var handler = new Handler(Response("first"), Response("second"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key")
        {
            Stateful = true,
            Store = false,
            Params = JsonSerializer.SerializeToElement(new { store = false, previous_response_id = "caller-override" })
        });
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("first input");
        await agent.InvokeAsync("second input");
        Assert.True(handler.Bodies[0].GetProperty("store").GetBoolean());
        Assert.False(handler.Bodies[0].TryGetProperty("previous_response_id", out _));
        Assert.Equal("first", handler.Bodies[1].GetProperty("previous_response_id").GetString());
        Assert.Equal("second input", handler.Bodies[1].GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, handler.Bodies[1].GetProperty("input").GetArrayLength());
        Assert.Equal("second", agent.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task DirectProviderPublishesDetachedStateOnlyAtValidatedTerminalEvent()
    {
        using var handler = new Handler(Response("accepted"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true });
        var input = new Dictionary<string, JsonElement> { ["other"] = JsonSerializer.SerializeToElement(42) };
        List<ModelStreamEvent> events = [];
        await foreach (var value in model.StreamAsync(new([Message.User("go")]) { ModelState = input })) events.Add(value);
        Assert.Single(input);
        var stop = Assert.Single(events.OfType<ModelMessageStopEvent>());
        Assert.Equal("accepted", stop.ModelState!["openaiResponses"].GetProperty("responseId").GetString());
        Assert.Equal("model", stop.ModelState["openaiResponses"].GetProperty("modelId").GetString());
        Assert.Equal(42, stop.ModelState["other"].GetInt32());
    }

    [Fact]
    public async Task RejectsStoredResponseForDifferentModelBeforeSendingRequest()
    {
        using var handler = new Handler(Response("unused"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true });
        var state = new Dictionary<string, JsonElement> { ["openaiResponses"] = JsonSerializer.SerializeToElement(new { responseId = "foreign", modelId = "different" }) };
        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([Message.User("go")]) { ModelState = state })) { }
        });
        Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task StatelessProviderKeepsNormalHistoryAndDoesNotPublishServerConversationState()
    {
        using var handler = new Handler(Response("one"), Response("two"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key"));
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("go");
        await agent.InvokeAsync("next");
        Assert.Equal(4, agent.Messages.Count);
        Assert.Empty(agent.ModelState.GetAll());
        Assert.False(handler.Bodies[1].TryGetProperty("previous_response_id", out _));
        Assert.False(handler.Bodies[1].GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task SharedProviderKeepsEachAgentsServerConversationIndependent()
    {
        using var handler = new Handler(Response("first-agent"), Response("second-agent"), Response("first-continued"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true });
        var first = new Agent(new() { Model = model });
        var second = new Agent(new() { Model = model });
        await first.InvokeAsync("first");
        await second.InvokeAsync("second");
        await first.InvokeAsync("continue first");
        Assert.False(handler.Bodies[1].TryGetProperty("previous_response_id", out _));
        Assert.Equal("first-agent", handler.Bodies[2].GetProperty("previous_response_id").GetString());
        Assert.Equal("second-agent", second.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
    }

    [Fact]
    public async Task TruncatedCreatedResponseCannotAdvanceCommittedState()
    {
        var partial = Frame("response.created", new { type = "response.created", response = new { id = "unaccepted" } });
        using var handler = new Handler(Response("accepted"), partial, Response("continued"));
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true }) });
        await agent.InvokeAsync("first");
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("retry this input"));
        Assert.Equal("accepted", agent.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
        Assert.Equal("retry this input", Assert.Single(agent.Messages).Text);
        await agent.InvokeAsync(null);
        Assert.Equal("accepted", handler.Bodies[2].GetProperty("previous_response_id").GetString());
        Assert.Equal(1, handler.Bodies[2].GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void StatefulResponseChainingCannotAlsoSelectAConversation()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesModel(client, new("model", "key")
        { Stateful = true, Params = JsonSerializer.SerializeToElement(new { conversation = "conversation" }) }));
    }

    private static string Response(string id) =>
        Frame("response.created", new { type = "response.created", response = new { id } }) +
        Frame("response.completed", new { type = "response.completed", response = new { id, status = "completed" } });
    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }
}
