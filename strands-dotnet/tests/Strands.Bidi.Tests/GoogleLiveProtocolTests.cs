using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.GoogleLiveTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveProtocolTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"serverContent\":1}")]
    [InlineData("{\"serverContent\":{\"turnComplete\":\"yes\"}}")]
    [InlineData("{\"usageMetadata\":{\"promptTokenCount\":-1}}")]
    [InlineData("{\"usageMetadata\":{\"totalTokenCount\":1.5}}")]
    [InlineData("{\"serverContent\":{},\"serverContent\":{}}")]
    [InlineData("{\"toolCall\":{\"functionCalls\":[{\"id\":\"c\",\"name\":\"n\",\"args\":[]}]}}")]
    [InlineData("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm\",\"data\":\"!\"}}]}}}")]
    public async Task MalformedNativeMessagesFailExplicitly(string native)
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync(native); await Assert.ThrowsAsync<ModelException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task CancellationOfABlockedNativeReceiveKeepsTheExactCallerToken()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); await StartAsync(model, server);
        using var cancellation = new CancellationTokenSource(); await using var reader = model.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync()); var pending = ReadErrorAsync(); cancellation.Cancel();
        var error = await Within(pending); Assert.Equal(cancellation.Token, error.CancellationToken);
        async Task<OperationCanceledException> ReadErrorAsync()
        {
            try { await reader.MoveNextAsync(); throw new InvalidOperationException("The native read must be cancelled."); }
            catch (OperationCanceledException canceled) { return canceled; }
        }
    }

    [Fact]
    public async Task SetupCancellationAndTimeoutDisposeTheHalfStartedSocketAndAllowANewConnection()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        using var cancellation = new CancellationTokenSource(); var starting = model.StartAsync(cancellationToken: cancellation.Token);
        var peer = await Within(server.Connection); await Within(peer.ReceiveAsync()); cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting); Assert.Equal(cancellation.Token, canceled.CancellationToken);
        await AssertSetupSocketClosedAsync(peer, allowQueuedSetup: false);
        model.UpdateConfig(options => options with { SetupTimeout = TimeSpan.FromMilliseconds(80) });
        var nextConnection = server.AcceptNextAsync(); starting = model.StartAsync(); var next = await Within(nextConnection);
        Assert.Contains("timed out", (await Assert.ThrowsAsync<ModelException>(() => starting)).Message);
        await AssertSetupSocketClosedAsync(next, allowQueuedSetup: true);
        model.UpdateConfig(options => options with { SetupTimeout = TimeSpan.FromSeconds(5) });
        var finalConnection = server.AcceptNextAsync(); starting = model.StartAsync(); var final = await Within(finalConnection); await Within(final.ReceiveAsync());
        await final.SendAsync("""{"setupComplete":{}}"""); await starting;
    }

    [Fact]
    public async Task DisconnectDoesNotInventResponseCompletionAndASecondReceiverIsRejected()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await using var first = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await first.MoveNextAsync());
        await using var second = model.ReceiveAsync().GetAsyncEnumerator(); await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        await peer.SendAsync("""{"serverContent":{"modelTurn":{"parts":[{"text":"unfinished"}]}}}""");
        Assert.True(await first.MoveNextAsync()); Assert.IsType<BidiResponseStartEvent>(first.Current);
        Assert.True(await first.MoveNextAsync()); Assert.True(await first.MoveNextAsync()); await peer.CloseAsync();
        Assert.False(await first.MoveNextAsync());
    }

    [Fact]
    public void ConfigurationIsDetachedAndNestedParamsOverrideNativeSessionDefaults()
    {
        using var parameters = JsonDocument.Parse("""{"generationConfig":{"responseModalities":["TEXT"],"speechConfig":{"languageCode":"en-US"}},"inputAudioTranscription":null}""");
        using var modelHolder = new ModelHolder(new(new() { ModelId = "m", ApiKey = "secret", Voice = "Aoede", Params = parameters.RootElement }));
        var model = modelHolder.Model; var config = model.GetConfig();
        Assert.Equal(540, model.Connection.RestartAfter!.Value.TotalSeconds); Assert.Equal(16000, model.Audio.Input.SampleRate);
        Assert.DoesNotContain("secret", config.ToString()); parameters.Dispose();
        Assert.Equal("TEXT", config.Params!.Value.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { InputSampleRate = 0 })); Assert.Equal(16000, model.GetConfig().InputSampleRate);
    }
    private static async Task AssertSetupSocketClosedAsync(Peer peer, bool allowQueuedSetup)
    {
        // The setup deadline also covers connect/send. A closed socket may have
        // zero frames, or its one setup frame may already be queued at the peer.
        try
        {
            var message = await Within(peer.ReceiveAsync());
            Assert.True(allowQueuedSetup, "The setup frame was already consumed before caller cancellation.");
            Assert.True(message.TryGetProperty("setup", out _));
            await Within(peer.ReceiveAsync());
            Assert.Fail("The half-started WebSocket must close after its optional setup frame.");
        }
        catch (EndOfStreamException) { }
        catch (IOException error) when (error.InnerException is System.Net.Sockets.SocketException
            { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted }) { }
    }

    private sealed class ModelHolder(GoogleGeminiLiveModel model) : IDisposable { internal GoogleGeminiLiveModel Model { get; } = model; public void Dispose() => Model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
