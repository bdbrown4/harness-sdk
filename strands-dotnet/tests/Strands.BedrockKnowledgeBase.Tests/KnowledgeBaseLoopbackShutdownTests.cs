using Amazon.BedrockAgent.Model;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseLoopbackShutdownTests
{
    [Fact]
    public async Task StopBetweenRequestsAndTheNextAcceptIsExpectedAndDisposePhysicallyJoins()
    {
        var beforeNextAccept = Signal(); var release = Signal(); var stopped = Signal(); var accepts = 0;
        var server = new KnowledgeBaseLoopback(beforeAccept: async _ =>
        {
            if (Interlocked.Increment(ref accepts) == 2)
            {
                beforeNextAccept.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }
        }, afterListenerStop: () => stopped.TrySetResult(true));
        using var client = server.Agent();
        Task<Exception>? closing = null;
        try
        {
            await Within(client.GetKnowledgeBaseAsync(new GetKnowledgeBaseRequest { KnowledgeBaseId = "0123456789" }));
            await Within(beforeNextAccept.Task);
            closing = Task.Run(() => Record.Exception(server.Dispose));
            await Within(stopped.Task);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            closing ??= Task.Run(() => Record.Exception(server.Dispose));
            var error = await Within(closing);
            Assert.Null(error);
        }
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AnIndependentResponseCallbackFailureAfterStopRemainsTheExactPrimary()
    {
        var entered = Signal(); var stopped = Signal(); using var release = new ManualResetEventSlim();
        var sentinel = new InvalidOperationException("independent response callback");
        var server = new KnowledgeBaseLoopback(afterListenerStop: () => stopped.TrySetResult(true));
        server.Respond = _ =>
        {
            entered.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Response callback was not released.");
            throw sentinel;
        };
        using var client = server.Agent();
        var request = client.GetKnowledgeBaseAsync(new GetKnowledgeBaseRequest { KnowledgeBaseId = "0123456789" });
        Task<Exception>? closing = null;
        try
        {
            await Within(entered.Task);
            closing = Task.Run(() => Record.Exception(server.Dispose));
            await Within(stopped.Task);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.Set();
            closing ??= Task.Run(() => Record.Exception(server.Dispose));
            var error = await Within(closing);
            await Within(Record.ExceptionAsync(async () => await request.ConfigureAwait(false)));
            Assert.Same(sentinel, error);
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5))));
        await operation.ConfigureAwait(false);
    }
    private static async Task<T> Within<T>(Task<T> operation)
    {
        await Within((Task)operation).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }
}
