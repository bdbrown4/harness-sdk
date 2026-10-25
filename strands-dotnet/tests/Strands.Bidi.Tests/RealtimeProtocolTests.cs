using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class RealtimeProtocolTests
{
    [Theory]
    [InlineData("{\"type\":\"unknown\",\"type\":\"other\"}")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"status\":\"completed\",\"call_id\":\"c\",\"name\":\"lookup\",\"arguments\":\"{broken\"}}")]
    public async Task MalformedServerProtocolIsATypedFailure(string json)
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var stream = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync());
        await peer.SendAsync(json);
        await Assert.ThrowsAsync<ModelException>(async () => await Within(stream.MoveNextAsync().AsTask()));
    }

    [Fact]
    public async Task IncomingBoundsApplyAcrossWebSocketFragments()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server, 1024);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var stream = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync());
        await peer.SendAsync("{\"type\":\"unknown\",\"payload\":\"" + new string('a', 1500) + "\"}", 800);
        var error = await Assert.ThrowsAsync<ModelException>(async () => await Within(stream.MoveNextAsync().AsTask()));
        Assert.Contains("MaximumEventBytes", error.Message);
    }

    [Fact]
    public async Task CancellationOfABlockedNativeReadRetainsTheCallerToken()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        using var cancellation = new CancellationTokenSource();
        await using var stream = model.ReceiveAsync(cancellation.Token).GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync());
        var pending = ReadNextAsync(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(pending));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        async Task<bool> ReadNextAsync() => await stream.MoveNextAsync();
    }

    [Fact]
    public async Task ExpiredConnectionIsReportedAsAConnectionTimeout()
    {
        await using var server = new WebSocketServer(); await using var model = new OpenAIRealtimeModel(Options(server) with { Timeout = TimeSpan.FromMilliseconds(50) });
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var stream = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync());
        await Assert.ThrowsAsync<BidiConnectionTimeoutException>(async () => await Within(stream.MoveNextAsync().AsTask()));
    }

    [Fact]
    public async Task OneReaderOwnsTheConnectionUntilItsEnumeratorIsDisposed()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        var first = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await first.MoveNextAsync());
        await using var second = model.ReceiveAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        await first.DisposeAsync();
        await using var third = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await third.MoveNextAsync());
        Assert.IsType<BidiConnectionStartEvent>(third.Current);
    }

    [Fact]
    public async Task InvalidHistoryIsRejectedBeforeOpeningTheTransport()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await Assert.ThrowsAsync<ArgumentException>(() => model.StartAsync(new() { Messages = [new(Role.User, [new ImageBlock("image/png", new Uri("https://example.org/image"))])] }));
        Assert.False(server.Connection.IsCompleted);
    }

    [Fact]
    public void UsageEventOwnsCallerSuppliedDetailMaps()
    {
        var details = new Dictionary<string, long> { ["audio"] = 4 };
        var output = new BidiUsageEvent(new Usage(4, 0), details);
        details["audio"] = 99;
        Assert.Equal(4, output.InputDetails!["audio"]);
        var mutation = Assert.IsAssignableFrom<IDictionary<string, long>>(output.InputDetails);
        Assert.Throws<NotSupportedException>(() => mutation["audio"] = 100);
    }

    [Fact]
    public async Task InvalidRichResultBatchCannotTransmitAPartialInput()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => model.SendAsync(new BidiMessage([new ToolResultBlock("first", "ok"), new ToolResultBlock("second", [new ImageBlock("image/png", "AQID")])])));
        await model.SendAsync(new BidiMessage([new TextBlock("valid")]));
        var item = await Within(peer.ReceiveAsync());
        Assert.Equal("message", item.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("valid", item.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }

    internal static OpenAIRealtimeModelOptions Options(WebSocketServer server) => new()
    {
        ModelId = "fixture-model",
        TranscriptionModelId = null,
        ApiKey = "fixture-key",
        Endpoint = server.Endpoint
    };
    internal static OpenAIRealtimeModel Model(WebSocketServer server, int maxBytes = 16 * 1024 * 1024) => new(Options(server) with { MaximumEventBytes = maxBytes });
}
