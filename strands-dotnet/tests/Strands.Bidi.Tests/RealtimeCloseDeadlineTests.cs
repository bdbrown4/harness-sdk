using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.RealtimeProtocolTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class RealtimeCloseDeadlineTests
{
    [Fact]
    public async Task AnInternalCloseDeadlineIsATimeoutAndReleasesThePhysicalSendBeforeReuse()
    {
        await using var server = new WebSocketServer();
        await using var model = new OpenAIRealtimeModel(Options(server) with { CloseTimeout = TimeSpan.FromMilliseconds(100) });
        await model.StartAsync();
        var peer = await Within(server.Connection);
        await Within(peer.ReceiveAsync());
        var blocked = await BlockSendAsync(model, peer);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => model.StopAsync(CancellationToken.None));
        Assert.Contains("CloseTimeout", error.Message);
        Assert.True(Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(CompleteAsync(blocked)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SendAsync(new BidiMessage([new TextBlock("closed")])));

        model.UpdateConfig(options => options with { CloseTimeout = TimeSpan.FromSeconds(5) });
        var next = server.AcceptNextAsync();
        await model.StartAsync();
        var restarted = await Within(next);
        await Within(restarted.ReceiveAsync());
        await model.SendAsync(new BidiMessage([new TextBlock("new connection")]));
        var input = await Within(restarted.ReceiveAsync());
        Assert.Equal("new connection", input.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CallerCancellationOfAGatedCloseRetainsTheExactCallerTokenAndReleasesTheSend()
    {
        await using var server = new WebSocketServer();
        await using var model = new OpenAIRealtimeModel(Options(server) with { CloseTimeout = TimeSpan.FromSeconds(5) });
        await model.StartAsync();
        var peer = await Within(server.Connection);
        await Within(peer.ReceiveAsync());
        var blocked = await BlockSendAsync(model, peer);
        using var cancellation = new CancellationTokenSource();
        var stopping = model.StopAsync(cancellation.Token);
        Assert.False(stopping.IsCompleted);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotNull(error.InnerException);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(CompleteAsync(blocked)));
        await model.StopAsync();
    }

    private static async Task<Task> BlockSendAsync(OpenAIRealtimeModel model, Peer peer)
    {
        var sending = model.SendAsync(new BidiMessage([new TextBlock(new string('x', 8 * 1024 * 1024))]));
        await Within(peer.ReadFramePrefixAsync());
        Assert.False(sending.IsCompleted);
        return sending;
    }

    private static async Task<bool> CompleteAsync(Task task)
    {
        await task;
        return true;
    }
}
