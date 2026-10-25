using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class StatefulResponsesCompatibilityTests
{
    [Fact]
    public async Task FileSessionRestoresServerStateWithoutReplayingAcknowledgedHistory()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSessionStore(directory.Path);
        using var handler = new Handler(Response("one"), Response("two"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key") { Stateful = true });
        await using (var first = new Agent(new() { Model = model, SessionStore = store, SessionId = "conversation" }))
            await first.InvokeAsync("first input");
        var snapshot = await store.LoadAsync("conversation");
        Assert.Empty(snapshot!.Messages);
        Assert.Equal(0, snapshot.ModelHistoryCount);
        Assert.Equal("one", snapshot.ModelState!["openaiResponses"].GetProperty("responseId").GetString());
        await using var second = new Agent(new() { Model = model, SessionStore = store, SessionId = "conversation" });
        await second.InvokeAsync("second input");
        Assert.Equal("one", handler.Bodies[1].GetProperty("previous_response_id").GetString());
        Assert.Equal(1, handler.Bodies[1].GetProperty("input").GetArrayLength());
        Assert.Equal("second input", handler.Bodies[1].GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(second.Messages);
    }

    [Fact]
    public async Task CancelledToolResultSurvivesFileRestoreAndIsSentWithoutRepeatingTool()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSessionStore(directory.Path);
        using var handler = new Handler(FunctionResponse("one"), Response("two"));
        using var client = new HttpClient(handler);
        var model = new RetryModel(new OpenAIResponsesModel(client, new("model", "key") { Stateful = true }));
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        var tool = new FunctionTool(new("save", "Save once", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => { executions++; cancellation.Cancel(); return new ValueTask<string>("saved once"); });
        await using (var first = new Agent(new() { Model = model, Tools = [tool], SessionStore = store, SessionId = "conversation" }))
            Assert.Equal(StopReasons.Cancelled, (await first.InvokeAsync("save", cancellationToken: cancellation.Token)).StopReason);
        var snapshot = await store.LoadAsync("conversation");
        Assert.Equal(3, snapshot!.Messages.Count);
        Assert.Equal(2, snapshot.ModelHistoryCount);
        await using var second = new Agent(new() { Model = model, Tools = [tool], SessionStore = store, SessionId = "conversation" });
        await second.InvokeAsync(null);
        Assert.Equal(1, executions);
        var input = handler.Bodies[1].GetProperty("input");
        Assert.Equal(1, input.GetArrayLength());
        Assert.Equal("function_call_output", input[0].GetProperty("type").GetString());
        Assert.Equal("saved once", input[0].GetProperty("output").GetString());
        Assert.Equal("call", input[0].GetProperty("call_id").GetString());
        Assert.Equal("one", handler.Bodies[1].GetProperty("previous_response_id").GetString());
        Assert.Empty(second.Messages);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsInvalidHistoryCursorBeforeWritingSnapshot(int count)
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSessionStore(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync("invalid", new(1, [Message.User("pending")]) { ModelHistoryCount = count }));
        Assert.Null(await store.LoadAsync("invalid"));
    }

    private static string Response(string id) =>
        Frame("response.created", new { type = "response.created", response = new { id } }) +
        Frame("response.completed", new { type = "response.completed", response = new { id, status = "completed" } });
    private static string FunctionResponse(string id) =>
        Frame("response.created", new { type = "response.created", response = new { id } }) +
        Frame("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item = new { id = "item", type = "function_call", call_id = "call", name = "save", arguments = "{}" } }) +
        Frame("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item = new { id = "item", type = "function_call", call_id = "call", name = "save", arguments = "{}" } }) +
        Frame("response.completed", new { type = "response.completed", response = new { id, status = "completed" } });
    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strands-stateful-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
