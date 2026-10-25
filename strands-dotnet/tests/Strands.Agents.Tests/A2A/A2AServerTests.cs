using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerTests
{
    [Fact]
    public async Task DiscoveryAndSynchronousJsonRpcExecuteNativeAgentsAndRetainAddressableTasks()
    {
        var factories = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; return new(new Agent(new() { Model = new EchoModel() })); } });
        await using var server = new A2AServer(executor, Options());
        using var http = new HttpClient(new Handler(server));
        var client = new A2AClient(http, new(new Uri("http://local/")));
        var card = await client.GetAgentCardAsync();
        Assert.Equal("Hosted", card.GetProperty("name").GetString()); Assert.Equal(0, factories);

        var task = await client.CallAsync("message/send", Send("hello"));
        var fetched = await client.GetTaskAsync(task.GetProperty("id").GetString()!, historyLength: 1);

        Assert.Equal("completed", task.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("hello", task.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(task.GetProperty("id").GetString(), fetched.GetProperty("id").GetString());
        Assert.Single(fetched.GetProperty("history").EnumerateArray()); Assert.Equal(1, factories);
    }
    [Fact]
    public async Task StreamingDeliversBeforeCompletionAndDisposingTheReaderDoesNotCancelTheTask()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new GatedModel(release.Task, finished) })) });
        await using var server = new A2AServer(executor, Options());
        using var http = new HttpClient(new Handler(server));
        var client = new A2AClient(http, new(new Uri("http://local/")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iterator = client.StreamCallAsync("message/stream", Send("hello"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        string? taskId = null;
        try
        {
            Assert.True(await iterator.MoveNextAsync()); taskId = iterator.Current.GetProperty("id").GetString();
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("artifact-update", iterator.Current.GetProperty("kind").GetString());
            Assert.False(finished.Task.IsCompleted);
        }
        finally { await iterator.DisposeAsync(); release.TrySetResult(true); }
        await finished.Task;
        var task = await client.GetTaskAsync(taskId!, cancellationToken: timeout.Token);
        while (task.GetProperty("status").GetProperty("state").GetString() == "working")
        { await Task.Delay(10, timeout.Token); task = await client.GetTaskAsync(taskId!, cancellationToken: timeout.Token); }
        Assert.Equal("completed", task.GetProperty("status").GetProperty("state").GetString());
    }
    [Fact]
    public async Task ResubscriptionStartsAtAnAtomicTaskSnapshotAndReceivesSubsequentUpdates()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new GatedModel(release.Task, finished) })) });
        await using var server = new A2AServer(executor, Options());
        using var http = new HttpClient(new Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var original = client.StreamCallAsync("message/stream", Send("hello"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            Assert.True(await original.MoveNextAsync()); var id = original.Current.GetProperty("id").GetString()!;
            Assert.True(await original.MoveNextAsync());
            var resumed = client.ResubscribeAsync(id, timeout.Token).GetAsyncEnumerator(timeout.Token);
            try
            {
                Assert.True(await resumed.MoveNextAsync());
                Assert.Equal("task", resumed.Current.GetProperty("kind").GetString());
                Assert.Equal("started", resumed.Current.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
                release.TrySetResult(true);
                JsonElement last = default; while (await resumed.MoveNextAsync()) last = resumed.Current;
                Assert.Equal("completed", last.GetProperty("status").GetProperty("state").GetString());
            }
            finally { await resumed.DisposeAsync(); }
        }
        finally { release.TrySetResult(true); await original.DisposeAsync(); }
    }
    [Fact]
    public async Task ExplicitRemoteCancellationDrainsTheActiveAgentAndRetainsTheCanceledTask()
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new CancelModel(closed) })) });
        await using var server = new A2AServer(executor, Options());
        using var http = new HttpClient(new Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var iterator = client.StreamCallAsync("message/stream", Send("hello"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            Assert.True(await iterator.MoveNextAsync()); var id = iterator.Current.GetProperty("id").GetString()!;
            Assert.True(await iterator.MoveNextAsync());

            var canceled = await client.CancelTaskAsync(id, timeout.Token);

            Assert.True(closed.Task.IsCompleted);
            Assert.Equal("canceled", canceled.GetProperty("status").GetProperty("state").GetString());
            Assert.Equal("canceled", (await client.GetTaskAsync(id, cancellationToken: timeout.Token)).GetProperty("status").GetProperty("state").GetString());
            Assert.Equal(-32002, (await Assert.ThrowsAsync<A2ARpcException>(() => client.CancelTaskAsync(id, timeout.Token))).Code);
        }
        finally { await iterator.DisposeAsync(); }
    }
    [Fact]
    public async Task NonblockingSendReturnsAnAddressableWorkingTaskBeforeItsBodyCompletes()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new GatedModel(release.Task, finished) })) });
        await using var server = new A2AServer(executor, Options());
        using var http = new HttpClient(new Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var parameters = JsonSerializer.SerializeToElement(new { message = Send("hello").GetProperty("message"), configuration = new { blocking = false } });

            var task = await client.CallAsync("message/send", parameters, timeout.Token);

            Assert.Equal("working", task.GetProperty("status").GetProperty("state").GetString());
            Assert.False(finished.Task.IsCompleted);
            Assert.Equal(task.GetProperty("id").GetString(), (await client.GetTaskAsync(task.GetProperty("id").GetString()!, cancellationToken: timeout.Token)).GetProperty("id").GetString());
        }
        finally { release.TrySetResult(true); }
    }
    internal static A2AServerOptions Options() => new() { Name = "Hosted", Description = "Native", Url = new("http://local/rpc") };
    internal static JsonElement Send(string text, string? taskId = null, string? contextId = null) => JsonSerializer.SerializeToElement(new { message = new { kind = "message", messageId = Guid.NewGuid().ToString("N"), role = "user", taskId, contextId, parts = new[] { new { kind = "text", text } } } });
    internal sealed class Handler(A2AServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => server.HandleAsync(request, cancellationToken);
    }
    internal sealed class EchoModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: request.Messages.Last().Text); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
    private sealed class GatedModel(Task release, TaskCompletionSource<bool> finished) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "started");
            try { await release; cancellationToken.ThrowIfCancellationRequested(); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
            finally { finished.TrySetResult(true); }
        }
    }
    private sealed class CancelModel(TaskCompletionSource<bool> closed) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "started");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { closed.TrySetResult(true); }
        }
    }
}
