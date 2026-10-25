using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.GoogleLiveTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveRestartTests
{
    [Fact]
    public async Task SetupAcknowledgementUsageRemainsObservable()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var starting = model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await peer.SendAsync("""{"setupComplete":{},"usageMetadata":{"promptTokenCount":2,"responseTokenCount":0,"totalTokenCount":2}}"""); await starting;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using (var reader = model.ReceiveAsync(deadline.Token).GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            var next = ReadAsync();
            // A following native usage event makes an omitted setup usage observable without a hanging RED fixture.
            await peer.SendAsync("""{"usageMetadata":{"promptTokenCount":9}}""");
            Assert.True(await next); Assert.Equal(2, Assert.IsType<BidiUsageEvent>(reader.Current).Usage.InputTokens);
            async Task<bool> ReadAsync() => await reader.MoveNextAsync();
        }
    }

    [Fact]
    public async Task RestartUsesTheTrackedHandleWithoutReplayingHistoryAndKeepsPendingToolNames()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var peer = await StartAsync(model, server);
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"resume-secret"}}""");
        await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"call","name":"calculate","args":{}}]}}""");
        while (await reader.MoveNextAsync()) if (reader.Current is BidiToolUseBlocksEvent) break;
        var nextConnection = server.AcceptNextAsync();
        var restarting = model.RestartAsync(new() { Messages = [Message.User("not replayed")] });
        var next = await Within(nextConnection); var setup = (await Within(next.ReceiveAsync())).GetProperty("setup");
        Assert.Equal("resume-secret", setup.GetProperty("sessionResumption").GetProperty("handle").GetString());
        Assert.False(setup.TryGetProperty("historyConfig", out _));
        await next.SendAsync("""{"setupComplete":{}}"""); await restarting;
        await model.SendAsync(new BidiMessage([new ToolResultBlock("call", "5")]));
        var result = (await Within(next.ReceiveAsync())).GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.Equal("calculate", result.GetProperty("name").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task RejectedResumptionFallsBackToFreshAcknowledgedHistorySeed()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var peer = await StartAsync(model, server);
        await using (var reader = model.ReceiveAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            await peer.SendAsync("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"old"},"usageMetadata":{}}""");
            Assert.True(await reader.MoveNextAsync()); Assert.IsType<BidiUsageEvent>(reader.Current);
        }
        var resumedConnection = server.AcceptNextAsync(); var restarting = model.RestartAsync(new() { Messages = [Message.User("history")] });
        var resumed = await Within(resumedConnection); await Within(resumed.ReceiveAsync());
        var freshConnection = server.AcceptNextAsync(); await resumed.SendAsync("""{"error":{"code":400,"message":"handle expired"}}""");
        var fresh = await Within(freshConnection); var setup = (await Within(fresh.ReceiveAsync())).GetProperty("setup");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, setup.GetProperty("sessionResumption").GetProperty("handle").ValueKind);
        Assert.True(setup.GetProperty("historyConfig").GetProperty("initialHistoryInClientContent").GetBoolean());
        await fresh.SendAsync("""{"setupComplete":{}}"""); await restarting;
        Assert.Equal("history", (await Within(fresh.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ANewStartClearsTheOldHandleAndGoAwayTriggersTheNativeRestartSignal()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var peer = await StartAsync(model, server);
        await using (var reader = model.ReceiveAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync()); await peer.SendAsync("""{"goAway":{"timeLeft":"20s"}}""");
            await Assert.ThrowsAsync<BidiConnectionTimeoutException>(async () => await reader.MoveNextAsync());
        }
        await model.StopAsync(); var nextConnection = server.AcceptNextAsync(); var starting = model.StartAsync();
        var next = await Within(nextConnection); var setup = (await Within(next.ReceiveAsync())).GetProperty("setup");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, setup.GetProperty("sessionResumption").GetProperty("handle").ValueKind);
        await next.SendAsync("""{"setupComplete":{}}"""); await starting;
    }
}
