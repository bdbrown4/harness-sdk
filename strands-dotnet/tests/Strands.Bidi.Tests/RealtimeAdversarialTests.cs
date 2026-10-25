using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.RealtimeProtocolTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class RealtimeAdversarialTests
{
    [Theory]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"status\":\"completed\",\"call_id\":\"c\",\"name\":\"lookup\",\"arguments\":\"{\\\"key\\\":1,\\\"key\\\":2}\"}}")]
    [InlineData("{\"type\":\"response.done\",\"response\":{\"id\":\"r\",\"output\":[],\"usage\":{\"input_tokens\":9223372036854775807,\"output_tokens\":1}}}")]
    [InlineData("{\"type\":\"response.done\",\"response\":{\"id\":\"r\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"one\",\"text\":\"two\"}]}]}}")]
    public async Task AmbiguousOrOverflowingNativeDataFailsBeforeAnOutputIsPublished(string json)
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"response.created","response":{"id":"r"}}"""); await reader.MoveNextAsync();
        await peer.SendAsync(json);
        await Assert.ThrowsAsync<ModelException>(async () => await Within(ReadAsync()));
        async Task<bool> ReadAsync() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task AQueuedSendCancellationRetainsItsCallerToken()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        var blocked = model.SendAsync(new BidiMessage([new TextBlock(new string('x', 8 * 1024 * 1024))]));
        await Within(peer.ReadFramePrefixAsync()); Assert.False(blocked.IsCompleted);
        using var cancellation = new CancellationTokenSource();
        var queued = model.SendAsync(new BidiMessage([new TextBlock("queued")]), cancellation.Token);
        cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(WaitAsync()));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            await model.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        }
        async Task<bool> WaitAsync() { await queued; return true; }
    }

    [Fact]
    public async Task FinalOnlyAndLateTranscriptsKeepTheirRolesAndContentBoundaries()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"user-audio","transcript":"late words"}""");
        await reader.MoveNextAsync(); Assert.Equal(Role.User, Assert.IsType<BidiTranscriptStartEvent>(reader.Current).Role);
        await reader.MoveNextAsync(); Assert.Equal("late words", Assert.IsType<BidiTranscriptDeltaEvent>(reader.Current).Delta);
        await reader.MoveNextAsync(); Assert.IsType<BidiTranscriptStopEvent>(reader.Current);
        await peer.SendAsync("""{"type":"response.created","response":{"id":"r"}}"""); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"response.done","response":{"id":"r","output":[{"type":"message","role":"assistant","content":[{"type":"output_audio","transcript":"first"},{"type":"output_audio","transcript":"second"},{"type":"output_text","text":"visible"}]}]}}""");
        await reader.MoveNextAsync(); Assert.Equal(Role.Assistant, Assert.IsType<BidiTranscriptStartEvent>(reader.Current).Role);
        await reader.MoveNextAsync(); Assert.Equal("first\n\nsecond", Assert.IsType<BidiTranscriptDeltaEvent>(reader.Current).Delta);
        await reader.MoveNextAsync(); Assert.IsType<BidiTranscriptStopEvent>(reader.Current);
        await reader.MoveNextAsync(); Assert.IsType<BidiTextStartEvent>(reader.Current);
        await reader.MoveNextAsync(); Assert.Equal("visible", Assert.IsType<BidiTextDeltaEvent>(reader.Current).Delta);
        await reader.MoveNextAsync(); Assert.IsType<BidiTextStopEvent>(reader.Current);
        await reader.MoveNextAsync(); Assert.IsType<BidiResponseStopEvent>(reader.Current);
    }

    [Fact]
    public async Task UnknownNativeErrorsRemainDetachedDiagnostics()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync();
        await peer.SendAsync("""{"type":"error","error":{"code":"invalid_request_error","message":"invalid tool schema"}}""");
        await reader.MoveNextAsync(); var diagnostic = Assert.IsType<BidiNativeEvent>(reader.Current);
        await peer.SendAsync("""{"type":"new.server.event","value":42}"""); await reader.MoveNextAsync();
        Assert.Equal("invalid tool schema", diagnostic.Value.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(42, Assert.IsType<BidiNativeEvent>(reader.Current).Value.GetProperty("value").GetInt32());
    }
}
