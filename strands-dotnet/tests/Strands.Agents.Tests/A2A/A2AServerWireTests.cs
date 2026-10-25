using System.Net.Http;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalHttpClientHandlerExecutesNativeJsonRpcAndSseAgainstTheHostedServer(bool streaming)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        await using var host = new A2AServerTcpFixture(uri => new(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var handler = new HttpClientHandler { UseProxy = false }; using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(host.BaseUri)); var remote = new A2AAgent(client, new() { Streaming = streaming });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await remote.InvokeAsync("Native 🌍", cancellationToken: timeout.Token);

        Assert.Equal("Native 🌍", result.Message!.Text); Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("Hosted", (await client.GetAgentCardAsync(timeout.Token)).GetProperty("name").GetString());
    }
    [Fact]
    public async Task RealHttpPausedTaskCanBeCanceledWithoutReplayingItsNativeEffects()
    {
        var effects = 0; var approvals = 0; var native = AbortInterruptTests.Create(new(), () => effects++, () => approvals++);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(native) });
        await using var host = new A2AServerTcpFixture(uri => new(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var handler = new HttpClientHandler { UseProxy = false }; using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(host.BaseUri)); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = await client.CallAsync("message/send", A2AServerTests.Send("run"), timeout.Token);

        var canceled = await client.CancelTaskAsync(task.GetProperty("id").GetString()!, timeout.Token);

        Assert.Equal("canceled", canceled.GetProperty("status").GetProperty("state").GetString());
        Assert.Null(native.InterruptState); Assert.Equal(1, effects); Assert.Equal(0, approvals);
    }
}
