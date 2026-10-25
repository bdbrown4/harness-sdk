using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.GoogleLiveTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveCancellationTests
{
    [Fact]
    public async Task CancellationBetweenNormalizedEventsPreventsFurtherBatchEmission()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        using var cancellation = new CancellationTokenSource(); await using var reader = model.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"serverContent":{"modelTurn":{"parts":[{"text":"hello"}]}}}""");
        Assert.True(await reader.MoveNextAsync()); Assert.IsType<BidiResponseStartEvent>(reader.Current); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync()); Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ActiveConnectionAudioMetadataStaysAlignedWithItsCapturedConfiguration()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        model.UpdateConfig(options => options with { InputSampleRate = 48000 });
        Assert.Equal(48000, model.GetConfig().InputSampleRate); Assert.Equal(16000, model.Audio.Input.SampleRate);
        await model.SendAsync(new BidiAudioDelta([1, 2]));
        Assert.Equal("audio/pcm;rate=16000", (await Within(peer.ReceiveAsync())).GetProperty("realtimeInput").GetProperty("audio").GetProperty("mimeType").GetString());
    }
}
