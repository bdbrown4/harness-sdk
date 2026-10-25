using System.Net;
using System.Net.Http;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Util;
using Strands.Agents.GoogleAuth;
using Xunit;

namespace Strands.GoogleAuth.Tests;

public sealed class GoogleCredentialHttpClientMinimumTests
{
    [Fact]
    public void NullCredentialFailsBeforeTransportCreation()
    {
        var factory = new Factory();
        Assert.Throws<ArgumentNullException>(() => GoogleCredentialHttpClient.CreateVertexClient(null!, factory));
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public void PublicExplicitFactoryIsInertAndBindsTheBorrowedCredential()
    {
        var credential = GoogleCredential.FromAccessToken("synthetic-static-token");
        using var client = GoogleCredentialHttpClient.CreateVertexClient(credential);
        Assert.Same(credential.UnderlyingCredential, client.MessageHandler.Credential);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task OfficialPipelineRetainsConfiguredQuotaAndAuthorization()
    {
        var original = GoogleCredential.FromAccessToken("synthetic-static-token");
        var credential = original.CreateWithQuotaProject("synthetic-quota");
        var factory = new Factory();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(credential, factory);
        Assert.Equal(0, factory.Handler!.Calls);
        using var response = await client.GetAsync("https://example.invalid/native-bridge");
        Assert.Equal("Bearer synthetic-static-token", factory.Handler.Authorization);
        Assert.Equal("synthetic-quota", factory.Handler.Quota);
        Assert.Null(original.QuotaProject);
        Assert.Equal("synthetic-quota", credential.QuotaProject);
    }

    [Fact]
    public async Task TwoClientsOwnTheirHandlersAndBorrowOneReusableCredential()
    {
        var credential = GoogleCredential.FromAccessToken("synthetic-static-token");
        var firstFactory = new Factory();
        using (var first = GoogleCredentialHttpClient.CreateVertexClient(credential, firstFactory))
        { using var response = await first.GetAsync("https://example.invalid/first"); }
        Assert.Equal(1, firstFactory.Handler!.Disposed);
        var secondFactory = new Factory();
        using (var second = GoogleCredentialHttpClient.CreateVertexClient(credential, secondFactory))
        { using var response = await second.GetAsync("https://example.invalid/second"); }
        Assert.Equal(1, secondFactory.Handler!.Disposed);
        Assert.Equal("Bearer synthetic-static-token", secondFactory.Handler.Authorization);
    }

    [Fact]
    public async Task PreCanceledDiscoveryInvokesNeitherResolverNorFactory()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var calls = 0;
        var factory = new Factory();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { calls++; return Task.FromResult(GoogleCredential.FromAccessToken("synthetic-static-token")); }, factory, canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task AdmittedDiscoveryAllocatesOnlyAfterTheResolverCompletes()
    {
        using var caller = new CancellationTokenSource();
        var source = new TaskCompletionSource<GoogleCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory();
        var credential = GoogleCredential.FromAccessToken("synthetic-static-token");
        CancellationToken received = default;
        var pending = GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(token => { received = token; return source.Task; }, factory, caller.Token);
        try
        {
            Assert.Equal(caller.Token, received);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, factory.Created);
        }
        finally { source.TrySetResult(credential); using var client = await Within(pending); }
        Assert.Equal(1, factory.Created);
        Assert.Equal(1, factory.Handler!.Disposed);
    }

    [Fact]
    public async Task IndependentResolverFailureRetainsIdentityWithoutCreatingTransport()
    {
        var failure = new IOException("synthetic discovery failure");
        var factory = new Factory();
        var observed = await Assert.ThrowsAsync<IOException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => Task.FromException<GoogleCredential>(failure), factory, default));
        Assert.Same(failure, observed);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task CanceledSdkWaitDoesNotAllocateOrOwnTheSharedDiscoveryWork()
    {
        using var caller = new CancellationTokenSource();
        var source = new TaskCompletionSource<GoogleCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory();
        var pending = GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(token => { entered.TrySetResult(true); return WaitOnly(source.Task, token); }, factory, caller.Token);
        try
        {
            await Within(entered.Task);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await Within(pending); });
            Assert.False(source.Task.IsCompleted);
            Assert.Equal(0, factory.Created);
        }
        finally
        {
            source.TrySetResult(GoogleCredential.FromAccessToken("synthetic-static-token"));
            await Within(source.Task);
            try { using var ignored = await Within(pending); } catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task ExistingOfficialInitializerControlNeedsNoNativeHelper()
    {
        var credential = GoogleCredential.FromAccessToken("synthetic-static-token");
        var factory = new Factory();
        var args = new CreateHttpClientArgs();
        args.Initializers.Add(credential);
        using var client = factory.CreateHttpClient(args);
        using var response = await client.GetAsync("https://example.invalid/sdk-control");
        Assert.Equal("Bearer synthetic-static-token", factory.Handler!.Authorization);
        Assert.Equal(1, factory.Handler.Calls);
    }

    private static async Task<T> WaitOnly<T>(Task<T> shared, CancellationToken cancellationToken)
    {
        var canceled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetCanceled()))
        {
            return await await Task.WhenAny(shared, canceled.Task);
        }
    }

    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(15000))); await task; }
    private static async Task<T> Within<T>(Task<T> task) { await Within((Task)task); return await task; }
    private sealed class Factory : Google.Apis.Http.HttpClientFactory
    {
        internal int Created;
        internal Handler? Handler;
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) { Created++; return Handler = new Handler(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal int Disposed;
        internal string? Authorization;
        internal string? Quota;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("example.invalid", request.RequestUri!.Host);
            Calls++; Authorization = request.Headers.Authorization?.ToString();
            Quota = request.Headers.TryGetValues("x-goog-user-project", out var values) ? string.Join(",", values) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed++; base.Dispose(disposing); }
    }
}
