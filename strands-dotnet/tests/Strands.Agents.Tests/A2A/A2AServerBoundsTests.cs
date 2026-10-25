using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerBoundsTests
{
    [Fact]
    public async Task ListenerLimitIsAnExplicitRpcErrorAndDisposingAResponseReleasesItsSlot()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new WaitingModel(release.Task) })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { MaxSubscribers = 1 });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = client.StreamCallAsync("message/stream", A2AServerTests.Send("hello"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            Assert.True(await first.MoveNextAsync()); var id = first.Current.GetProperty("id").GetString()!;
            var second = client.ResubscribeAsync(id, timeout.Token).GetAsyncEnumerator(timeout.Token);
            try
            {
                var error = await Assert.ThrowsAsync<A2ARpcException>(async () => { await second.MoveNextAsync(); });
                Assert.Equal(-32000, error.Code);
            }
            finally { await second.DisposeAsync(); }
            await first.DisposeAsync();
            var replacement = client.ResubscribeAsync(id, timeout.Token).GetAsyncEnumerator(timeout.Token);
            try { Assert.True(await replacement.MoveNextAsync()); Assert.Equal(id, replacement.Current.GetProperty("id").GetString()); }
            finally { await replacement.DisposeAsync(); }
        }
        finally { release.TrySetResult(true); await first.DisposeAsync(); }
    }
    [Fact]
    public async Task ReplayEvictionFailsExplicitlyRatherThanSilentlyReturningAPartialSuccessfulStream()
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { MaxReplayEvents = 1 });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var iterator = client.StreamCallAsync("message/stream", A2AServerTests.Send("hello")).GetAsyncEnumerator();
        try { Assert.Equal(-32000, (await Assert.ThrowsAsync<A2ARpcException>(async () => { await iterator.MoveNextAsync(); })).Code); }
        finally { await iterator.DisposeAsync(); }
    }
    private sealed class WaitingModel(Task release) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent(); await release; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
