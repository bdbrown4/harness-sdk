using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.GoogleLiveTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveIntegrationTests
{
    [Fact]
    public async Task NativeGoogleTurnsDispatchARealCoreToolAndCommitOneCorrelatedHistoryPair()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var state = new Dictionary<string, object?> { ["principal"] = "fixture" }; var calls = 0;
        var tool = new FunctionTool(new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => { Assert.Same(state, context.InvocationState); calls++; return new ValueTask<string>("7"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var agent = new BidiAgent(new() { ToolAgent = host, Model = model });
        var starting = agent.StartAsync(state); var peer = await Within(server.Connection); var setup = (await Within(peer.ReceiveAsync())).GetProperty("setup");
        Assert.Equal("lookup", setup.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        await peer.SendAsync("""{"setupComplete":{}}"""); await starting;
        await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"call","name":"lookup","args":{}}]}}""");
        await peer.SendAsync("""{"serverContent":{"turnComplete":true}}""");
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        do { Assert.True(await reader.MoveNextAsync()); } while (reader.Current is not ToolResultEvent);
        var result = (await Within(peer.ReceiveAsync())).GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.Equal("call", result.GetProperty("id").GetString()); Assert.Equal("7", result.GetProperty("response").GetProperty("text").GetString());
        Assert.Equal(1, calls); Assert.Empty(host.Messages);
        Assert.Equal("call", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal("call", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).ToolUseId);
        await agent.StopAsync(); Assert.Single(state);
    }

    [Fact]
    public async Task DisposingDuringSetupCancelsAndJoinsTheOwnedSocketWithoutTakingCallerState()
    {
        await using var server = new WebSocketServer(); var model = Model(server);
        var starting = model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await model.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await model.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => model.StartAsync());
    }

    [Fact]
    public async Task UnsupportedSetupContentCannotBeMistakenForAnAcknowledgement()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var starting = model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await peer.SendAsync("""{"serverContent":{"turnComplete":true}}""");
        Assert.Contains("setupComplete", (await Assert.ThrowsAsync<ModelException>(() => starting)).Message);
    }

    [Fact]
    public void OpaqueNativePartMetadataIsRetainedAlongsideVisibleText()
    {
        var state = new GoogleLiveState(); using var json = JsonDocument.Parse("""{"serverContent":{"modelTurn":{"parts":[{"text":"visible","thoughtSignature":"opaque-signature"}]}}}""");
        var events = state.Process(json.RootElement); Assert.Equal("visible", Assert.Single(events.OfType<BidiTextDeltaEvent>()).Delta);
        var native = Assert.Single(events.OfType<BidiNativeEvent>()); json.Dispose();
        Assert.Equal("opaque-signature", native.Value.GetProperty("serverContent").GetProperty("modelTurn").GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
    }
}
