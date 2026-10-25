using System.Text;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.S3.Model;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseS3WriteLifetimeTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CallerCancellationPhysicallyJoinsEachUploadAndRetainsExactCallerCause(int step, bool matching)
    {
        using var caller = new CancellationTokenSource(); using var s3 = new S3TestClient(); using var agent = new AgentClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal(); var uploads = 0;
        var primary = new OperationCanceledException("backend canceled", matching ? caller.Token : CancellationToken.None);
        s3.OnPut = async (_, _) => { if (++uploads == step) { entered.TrySetResult(true); await release.Task; throw primary; } return new(); };
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteTests.Options(s3, agent) with { Scope = "tenant" }); var write = store.AddAsync("fact", cancellationToken: caller.Token).AsTask();
        try { await TestSupport.Ready(entered.Task); caller.Cancel(); Assert.False(write.IsCompleted); }
        finally { release.TrySetResult(true); try { await write; } catch (OperationCanceledException) { } }
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write); Assert.Equal(caller.Token, actual.CancellationToken); if (matching) Assert.Same(primary, actual); else Assert.Same(primary, actual.InnerException); Assert.Equal(step, s3.Calls); Assert.Equal(0, agent.IngestCalls); Assert.Equal(0, s3.Deletes);
    }

    [Fact]
    public async Task StoreDisposalPhysicallyJoinsUploadBeforeOwnedClientCleanup()
    {
        using var agent = new AgentClient(); var s3 = new S3TestClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        s3.OnPut = async (_, token) => { entered.TrySetResult(true); await release.Task; token.ThrowIfCancellationRequested(); return new(); };
        var options = KnowledgeBaseS3WriteTests.Options(s3, agent) with { S3 = new() { Bucket = "fixture-bucket", Prefix = "memory" } }; var store = new BedrockKnowledgeBaseStore(options, null, null, () => s3); var write = store.AddAsync("fact").AsTask(); Task? close = null;
        try { await TestSupport.Ready(entered.Task); close = store.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); Assert.False(write.IsCompleted); Assert.Equal(0, s3.Disposals); await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.AddAsync("late")); }
        finally { release.TrySetResult(true); try { try { await write; } catch (OperationCanceledException) { } } finally { await (close ?? store.DisposeAsync().AsTask()); } }
        Assert.Equal(1, s3.Disposals); Assert.Equal(0, agent.IngestCalls); Assert.Equal(0, agent.Disposals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulUploadThenLateCallerCancellationDoesNotIngestOrCompensate(int step)
    {
        using var caller = new CancellationTokenSource(); using var s3 = new S3TestClient(); using var agent = new AgentClient(); var uploads = 0;
        s3.OnPut = (_, _) => { if (++uploads == step) caller.Cancel(); return Task.FromResult(new PutObjectResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteTests.Options(s3, agent) with { Scope = "tenant" });
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AddAsync("possibly committed", cancellationToken: caller.Token)); Assert.Equal(caller.Token, actual.CancellationToken); Assert.Equal(step, s3.Calls); Assert.Equal(0, agent.IngestCalls); Assert.Equal(0, s3.Deletes);
    }

    [Fact]
    public async Task IndependentUploadFailureWinsOverRacingCallerCancellation()
    {
        using var caller = new CancellationTokenSource(); using var s3 = new S3TestClient(); using var agent = new AgentClient(); var primary = new IOException("upload failed independently");
        s3.OnPut = (_, _) => { caller.Cancel(); throw primary; }; await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteTests.Options(s3, agent));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await store.AddAsync("fact", cancellationToken: caller.Token))); Assert.Equal(0, agent.IngestCalls);
    }

    [Fact]
    public async Task ThreeOwnedClientsAreLazyAndAllCleanupIsAttemptedWithUniqueCausalErrors()
    {
        var runtime = new RuntimeClient(); var agent = new AgentClient(); var s3 = new S3TestClient(); var created = new int[3]; var first = new IOException("runtime cleanup"); var second = new IOException("agent cleanup"); var third = new IOException("S3 cleanup");
        var options = KnowledgeBaseS3WriteTests.Options(s3, agent) with { RuntimeClient = null, AgentClient = null, KnownKnowledgeBaseType = new("MANAGED"), S3 = new() { Bucket = "fixture-bucket", Prefix = "memory" } };
        var store = new BedrockKnowledgeBaseStore(options, () => { created[0]++; return runtime; }, () => { created[1]++; return agent; }, () => { created[2]++; return s3; });
        try
        {
            Assert.Equal(new[] { 0, 0, 0 }, created); await store.SearchAsync("fact"); await store.AddAsync("fact"); Assert.Equal(new[] { 1, 1, 1 }, created);
            runtime.OnDispose = () => throw first; agent.OnDispose = () => throw second; s3.OnDispose = () => throw third;
            Assert.Same(first, await Assert.ThrowsAsync<IOException>(async () => await store.DisposeAsync())); Assert.Same(first, await Assert.ThrowsAsync<IOException>(async () => await store.DisposeAsync())); Assert.Equal(new[] { second, third }, Assert.IsType<List<Exception>>(first.Data["Strands.CleanupErrors"])); Assert.Equal(1, runtime.Disposals); Assert.Equal(1, agent.Disposals); Assert.Equal(1, s3.Disposals);
        }
        finally { runtime.OnDispose = null; agent.OnDispose = null; s3.OnDispose = null; runtime.Dispose(); agent.Dispose(); s3.Dispose(); }
    }

    [Fact]
    public async Task PrivateUploadStreamsStayOpenUntilPhysicalCompletionThenCloseExactlyAtOwnerBoundary()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var streams = new List<Stream>(); var payloads = new List<string>();
        s3.OnPut = async (request, _) => { streams.Add(request.InputStream); Assert.False(request.AutoCloseStream); await Task.Yield(); Assert.True(request.InputStream.CanRead); using var reader = new StreamReader(request.InputStream, Encoding.UTF8, false, 1024, true); payloads.Add(reader.ReadToEnd()); return new(); };
        var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteTests.Options(s3, agent) with { Scope = "tenant" }); await store.AddAsync("雪"); Assert.Equal(2, streams.Count); Assert.All(streams, stream => Assert.False(stream.CanRead)); Assert.Equal("雪", payloads[0]); await store.DisposeAsync();
        Assert.Equal(0, s3.Disposals); Assert.Equal(0, agent.Disposals); using var borrowedBody = new MemoryStream([1]); await s3.PutObjectAsync(new() { BucketName = "borrowed", Key = "alive", InputStream = borrowedBody, AutoCloseStream = false }); Assert.True(borrowedBody.CanRead);
    }

    [Fact]
    public async Task PrecancelledS3WriteCreatesNoLazyClient()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel(); using var s3 = new S3TestClient(); using var agent = new AgentClient(); var created = 0;
        var options = KnowledgeBaseS3WriteTests.Options(s3, agent) with { AgentClient = null, S3 = new() { Bucket = "fixture-bucket", Prefix = "memory" } };
        await using var store = new BedrockKnowledgeBaseStore(options, null, () => { created++; return agent; }, () => { created++; return s3; });
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AddAsync("fact", cancellationToken: caller.Token)); Assert.Equal(caller.Token, actual.CancellationToken); Assert.Equal(0, created);
    }

    [Fact]
    public async Task SynchronousUploadCallbackCannotWaitForItsOwningStoreDisposal()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); BedrockKnowledgeBaseStore? store = null;
        s3.OnPut = (_, _) => { Assert.Throws<InvalidOperationException>(() => store!.Dispose()); Assert.Throws<InvalidOperationException>(() => store!.DisposeAsync()); return Task.FromResult(new PutObjectResponse()); };
        store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteTests.Options(s3, agent)); try { await store.AddAsync("fact"); } finally { await store.DisposeAsync(); }
        Assert.Equal(1, agent.IngestCalls); Assert.Equal(0, s3.Disposals);
    }
}
