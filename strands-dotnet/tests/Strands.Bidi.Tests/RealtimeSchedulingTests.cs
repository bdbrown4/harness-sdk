using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.RealtimeProtocolTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class RealtimeSchedulingTests
{
    [Fact]
    public async Task ParallelToolResultsRequestOneContinuationOnlyAfterEveryCallSettles()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(Created("r")); await reader.MoveNextAsync();
        foreach (var id in new[] { "c1", "c2" })
        {
            await peer.SendAsync(Call(id)); Assert.True(await reader.MoveNextAsync());
            Assert.Equal(id, Assert.Single(Assert.IsType<BidiToolUseBlocksEvent>(reader.Current).ToolUses).ToolUseId);
        }
        await peer.SendAsync(Done("r")); await reader.MoveNextAsync();
        await model.SendAsync(new BidiMessage([new ToolResultBlock("c1", "one")]));
        Assert.Equal("c1", (await Within(peer.ReceiveAsync())).GetProperty("item").GetProperty("call_id").GetString());
        await model.SendAsync(new BidiMessage([new ToolResultBlock("c2", [new JsonBlock(JsonSerializer.SerializeToElement(new { number = 2 }))])]));
        var second = await Within(peer.ReceiveAsync());
        Assert.Equal("c2", second.GetProperty("item").GetProperty("call_id").GetString());
        Assert.Contains("number", second.GetProperty("item").GetProperty("output").GetString());
        Assert.Equal("response.create", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task AcknowledgedInputsDoNotCreateAnUnnecessarySecondResponse()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await model.SendAsync(new BidiMessage([new TextBlock("first")]));
        var input = await Within(peer.ReceiveAsync()); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(JsonSerializer.Serialize(new { type = "conversation.item.added", item = new { id = input.GetProperty("item").GetProperty("id").GetString(), role = "user" } }));
        await peer.SendAsync(Created("r")); await reader.MoveNextAsync();
        await peer.SendAsync(Done("r")); await reader.MoveNextAsync();
        await model.SendAsync(new BidiMessage([new TextBlock("second")]));
        var next = await Within(peer.ReceiveAsync());
        Assert.Equal("conversation.item.create", next.GetProperty("type").GetString());
        Assert.Equal("second", next.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task LateInputDuringAnActiveResponseIsIncludedInOneDeferredContinuation()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(Created("r")); await reader.MoveNextAsync();
        await model.SendAsync(new BidiMessage([new TextBlock("late")]));
        Assert.Equal("conversation.item.create", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
        await peer.SendAsync(Done("r")); await reader.MoveNextAsync();
        Assert.Equal("response.create", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ServerReportedBargeInClosesAudioAndEmitsTheInterruption()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(Created("r")); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"response.output_audio.delta","response_id":"r","delta":"AQID"}""");
        await reader.MoveNextAsync(); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"response.done","response":{"id":"r","status":"cancelled","status_details":{"reason":"turn_detected"},"output":[]}}""");
        await reader.MoveNextAsync(); Assert.IsType<BidiBargeInEvent>(reader.Current);
        await reader.MoveNextAsync(); Assert.IsType<BidiAudioStopEvent>(reader.Current);
        await reader.MoveNextAsync(); Assert.IsType<BidiResponseStopEvent>(reader.Current);
    }

    [Fact]
    public async Task CompletedToolIdentifiersCannotExecuteAgainAfterTheirResultWasSent()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(Created("one")); await reader.MoveNextAsync();
        await peer.SendAsync(Call("call")); await reader.MoveNextAsync();
        await model.SendAsync(new BidiMessage([new ToolResultBlock("call", "complete")])); await Within(peer.ReceiveAsync());
        await peer.SendAsync(Done("one")); await reader.MoveNextAsync(); await Within(peer.ReceiveAsync());
        await peer.SendAsync(Created("two")); await reader.MoveNextAsync();
        await peer.SendAsync(Call("call"));
        await Assert.ThrowsAsync<ModelException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task RestartPreservesOutstandingToolsAndBindsEachReaderToItsOwnConnection()
    {
        await using var first = new WebSocketServer(); await using var model = Model(first);
        await model.StartAsync(); var peer = await Within(first.Connection); await Within(peer.ReceiveAsync());
        await using var oldReader = model.ReceiveAsync().GetAsyncEnumerator(); await oldReader.MoveNextAsync();
        await peer.SendAsync(Created("r")); await oldReader.MoveNextAsync();
        await peer.SendAsync(Call("call")); await oldReader.MoveNextAsync();
        await using var second = new WebSocketServer(); model.UpdateConfig(value => value with { Endpoint = second.Endpoint });
        await model.RestartAsync(new() { Messages = [new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))])] });
        var next = await Within(second.Connection);
        await Within(next.ReceiveAsync());
        Assert.Equal("function_call", (await Within(next.ReceiveAsync())).GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("system", (await Within(next.ReceiveAsync())).GetProperty("item").GetProperty("role").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await oldReader.MoveNextAsync());
        await model.SendAsync(new BidiMessage([new TextBlock("continue")])); await Within(next.ReceiveAsync());
        await model.SendAsync(new BidiMessage([new ToolResultBlock("call", "complete")]));
        var output = await Within(next.ReceiveAsync());
        Assert.Equal("conversation.item.create", output.GetProperty("type").GetString());
        Assert.Equal("function_call_output", output.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("response.create", (await Within(next.ReceiveAsync())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task FailedReconnectRetainsOutstandingToolsForTheNextRestart()
    {
        await using var first = new WebSocketServer(); await using var model = Model(first);
        await model.StartAsync(); var peer = await Within(first.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync(Created("r")); await reader.MoveNextAsync();
        await peer.SendAsync(Call("call")); await reader.MoveNextAsync();
        var session = new BidiSessionOptions { Messages = [new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))])] };
        Uri unavailable;
        await using (var stopped = new WebSocketServer()) unavailable = stopped.Endpoint;
        model.UpdateConfig(value => value with { Endpoint = unavailable });
        await Assert.ThrowsAsync<ModelException>(() => model.RestartAsync(session));
        await using var recovered = new WebSocketServer(); model.UpdateConfig(value => value with { Endpoint = recovered.Endpoint });
        await using var recoveredModelLifetime = model;
        await model.RestartAsync(session); var next = await Within(recovered.Connection);
        await Within(next.ReceiveAsync()); await Within(next.ReceiveAsync()); await Within(next.ReceiveAsync());
        await model.SendAsync(new BidiMessage([new TextBlock("continue")])); await Within(next.ReceiveAsync());
        await model.SendAsync(new BidiMessage([new ToolResultBlock("call", "complete")]));
        var output = await Within(next.ReceiveAsync());
        Assert.Equal("conversation.item.create", output.GetProperty("type").GetString());
        Assert.Equal("function_call_output", output.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("response.create", (await Within(next.ReceiveAsync())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task OversizedHistoryIsRejectedBeforeTheWebSocketHandshake()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server, 1024);
        await Assert.ThrowsAsync<ModelException>(() => model.StartAsync(new() { Messages = [Message.User(new string('x', 2000))] }));
        Assert.False(server.Connection.IsCompleted);
    }

    private static string Created(string id) => JsonSerializer.Serialize(new { type = "response.created", response = new { id } });
    private static string Done(string id) => JsonSerializer.Serialize(new { type = "response.done", response = new { id, status = "completed", output = Array.Empty<object>() } });
    private static string Call(string id) => JsonSerializer.Serialize(new { type = "response.output_item.done", item = new { type = "function_call", status = "completed", call_id = id, name = "lookup", arguments = "{}" } });
}
