using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Util;
using Google.Apis.Util.Store;
using Strands.Agents.GoogleAuth;
using Xunit;

namespace Strands.GoogleAuth.Tests;

public sealed class GoogleCredentialHttpClientConformanceTests
{
    [Fact]
    public void NullExplicitFactoryFailsBeforeInitialization()
        => Assert.Throws<ArgumentNullException>(() => GoogleCredentialHttpClient.CreateVertexClient(Token(), null!));

    [Fact]
    public async Task NullResolverDoesNotCreateTransport()
    {
        using var factory = new Factory();
        await Assert.ThrowsAsync<ArgumentNullException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(null!, factory, default));
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task NullAsyncFactoryDoesNotInvokeResolver()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentNullException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { calls++; return Task.FromResult(Token()); }, null!, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnscopedCredentialGetsAnOfficialCloudScopeCopy()
    {
        using var fixture = new ServiceFixture();
        var original = fixture.Credential.ToGoogleCredential();
        Assert.True(original.IsCreateScopedRequired);
        using var client = GoogleCredentialHttpClient.CreateVertexClient(original, fixture.Factory);
        var scoped = Assert.IsType<ServiceAccountCredential>(client.MessageHandler.Credential);
        Assert.NotSame(fixture.Credential, scoped);
        Assert.Equal(new[] { "https://www.googleapis.com/auth/cloud-platform" }, scoped.Scopes);
        Assert.Empty(fixture.Credential.Scopes);
        Assert.True(original.IsCreateScopedRequired);
        Assert.Same(fixture.Credential, original.UnderlyingCredential);
        Assert.Equal("synthetic-quota", original.QuotaProject);
        Assert.All(fixture.Factory.Handlers, handler => Assert.Equal(0, handler.Calls));
    }

    [Fact]
    public void ExplicitScopesAndQuotaRemainOnTheSameCredential()
    {
        using var fixture = new ServiceFixture(["synthetic-scope-b", "synthetic-scope-a"]);
        var original = fixture.Credential.ToGoogleCredential();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(original, fixture.Factory);
        Assert.Same(fixture.Credential, client.MessageHandler.Credential);
        Assert.Equal(new[] { "synthetic-scope-b", "synthetic-scope-a" }, fixture.Credential.Scopes);
        Assert.Equal("synthetic-quota", original.QuotaProject);
        Assert.False(original.IsCreateScopedRequired);
    }

    [Fact]
    public void OfficialScopeCopyKeepsConfiguredCredentialSettings()
    {
        using var fixture = new ServiceFixture();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(fixture.Credential.ToGoogleCredential(), fixture.Factory);
        var scoped = Assert.IsType<ServiceAccountCredential>(client.MessageHandler.Credential);
        Assert.Equal("synthetic-project", scoped.ProjectId);
        Assert.Equal("synthetic-user", scoped.User);
        Assert.Same(fixture.Key, scoped.Key);
        Assert.Equal("synthetic-quota", scoped.QuotaProject);
        Assert.True(fixture.Factory.Clients.Count >= 3); // Original, scoped credential client, returned request client.
        Assert.All(fixture.Factory.Handlers, handler => Assert.Equal(0, handler.Calls));
    }

    [Fact]
    public async Task NullResolvedCredentialFailsBeforeTransportAllocation()
    {
        using var factory = new Factory();
        await Assert.ThrowsAsync<ArgumentNullException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => Task.FromResult<GoogleCredential>(null!), factory, default));
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public void IndependentFactoryFailureRetainsIdentityAndThrowSite()
    {
        var failure = new IOException("synthetic factory failure");
        using var factory = new Factory { CreateFailure = failure };
        var actual = Assert.Throws<IOException>(() => GoogleCredentialHttpClient.CreateVertexClient(Token(), factory));
        Assert.Same(failure, actual);
        Assert.Contains(nameof(ThrowKnown), actual.StackTrace);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public void InitializerFailureDisposesReturnedClientAndKeepsPrimary()
    {
        var failure = new IOException("synthetic initializer failure");
        using var flow = new ClosedFlow();
        using var factory = new Factory();
        var credential = new FailingUserCredential(flow, failure).ToGoogleCredential();
        var actual = Assert.Throws<IOException>(() => GoogleCredentialHttpClient.CreateVertexClient(credential, factory));
        Assert.Same(failure, actual);
        Assert.Contains(nameof(ThrowKnown), actual.StackTrace);
        Assert.Equal(1, Assert.Single(factory.Clients).DisposeCalls);
        Assert.Equal(1, Assert.Single(factory.Handlers).Disposed);
        Assert.Equal(0, flow.Disposed);
    }

    [Fact]
    public void IndependentInitializerCleanupFailureIsSecondary()
    {
        var primary = new IOException("synthetic initializer failure");
        var cleanup = new IOException("synthetic disposal failure");
        using var flow = new ClosedFlow();
        using var factory = new Factory { DisposeFailure = cleanup };
        var actual = Assert.Throws<IOException>(() => GoogleCredentialHttpClient.CreateVertexClient(new FailingUserCredential(flow, primary).ToGoogleCredential(), factory));
        Assert.Same(primary, actual);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, Assert.Single(factory.Clients).DisposeCalls);
    }

    [Fact]
    public void PrimaryIsNotAttachedAsItsOwnCleanup()
    {
        var primary = new IOException("same synthetic error");
        using var flow = new ClosedFlow();
        using var factory = new Factory { DisposeFailure = primary };
        var actual = Assert.Throws<IOException>(() => GoogleCredentialHttpClient.CreateVertexClient(new FailingUserCredential(flow, primary).ToGoogleCredential(), factory));
        Assert.Same(primary, actual);
        Assert.False(actual.Data.Contains("Strands.CleanupErrors"));
        Assert.Equal(1, Assert.Single(factory.Clients).DisposeCalls);
    }

    [Fact]
    public void ExistingCleanupReferenceIsNotDuplicated()
    {
        var primary = new IOException("synthetic initializer failure");
        var cleanup = new IOException("already recorded cleanup");
        var errors = new List<Exception> { cleanup };
        primary.Data["Strands.CleanupErrors"] = errors;
        using var flow = new ClosedFlow();
        using var factory = new Factory { DisposeFailure = cleanup };
        var actual = Assert.Throws<IOException>(() => GoogleCredentialHttpClient.CreateVertexClient(new FailingUserCredential(flow, primary).ToGoogleCredential(), factory));
        Assert.Same(primary, actual);
        Assert.Same(errors, actual.Data["Strands.CleanupErrors"]);
        Assert.Same(cleanup, Assert.Single(errors));
    }

    [Fact]
    public void SuccessfulInitializationTransfersOnlyClientDisposalToCaller()
    {
        using var factory = new Factory();
        var client = GoogleCredentialHttpClient.CreateVertexClient(Token(), factory);
        Assert.Equal(0, Assert.Single(factory.Clients).DisposeCalls);
        client.Dispose();
        Assert.Equal(1, Assert.Single(factory.Clients).DisposeCalls);
        Assert.Equal(1, Assert.Single(factory.Handlers).Disposed);
    }

    [Fact]
    public async Task CancellationOnResolverCompletionPreventsClientAllocation()
    {
        using var caller = new CancellationTokenSource();
        using var factory = new Factory();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { caller.Cancel(); return Task.FromResult(Token()); }, factory, caller.Token));
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task IndependentResolverFailureAfterCancellationRemainsPrimary()
    {
        using var caller = new CancellationTokenSource();
        using var factory = new Factory();
        var primary = new IOException("independent resolver failure");
        var actual = await Assert.ThrowsAsync<IOException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { caller.Cancel(); ThrowKnown(primary); return Task.FromResult(Token()); }, factory, caller.Token));
        Assert.Same(primary, actual);
        Assert.Contains(nameof(ThrowKnown), actual.StackTrace);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task MatchingResolverCancellationKeepsExactExceptionAndToken()
    {
        using var caller = new CancellationTokenSource();
        using var factory = new Factory();
        var primary = new OperationCanceledException("matching synthetic cancellation", caller.Token);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { caller.Cancel(); ThrowKnown(primary); return Task.FromResult(Token()); }, factory, caller.Token));
        Assert.Same(primary, actual);
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowKnown), actual.StackTrace);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task IndependentResolverCancellationKeepsExactExceptionAndToken()
    {
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        using var factory = new Factory();
        var primary = new OperationCanceledException("independent synthetic cancellation", independent.Token);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { ThrowKnown(primary); return Task.FromResult(Token()); }, factory, default));
        Assert.Same(primary, actual);
        Assert.Equal(independent.Token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowKnown), actual.StackTrace);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task SynchronousResolverFailureIsNotReclassified()
    {
        using var factory = new Factory();
        var primary = new FormatException("synthetic resolver shape failure");
        var actual = await Assert.ThrowsAsync<FormatException>(() => GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(
            _ => { ThrowKnown(primary); return Task.FromResult(Token()); }, factory, default));
        Assert.Same(primary, actual);
        Assert.Empty(factory.Clients);
    }

    [Fact]
    public async Task CancellationDuringAcceptedFactoryCreationDoesNotLoseTheClient()
    {
        using var caller = new CancellationTokenSource();
        using var factory = new Factory { BeforeCreate = caller.Cancel };
        using var client = await GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(_ => Task.FromResult(Token()), factory, caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.Same(client, Assert.Single(factory.Clients));
        Assert.Equal(0, Assert.Single(factory.Clients).DisposeCalls);
        Assert.NotNull(client.MessageHandler.Credential);
    }

    [Fact]
    public async Task CanceledAndSurvivingSyntheticWaitersSettleTheirSharedSource()
    {
        using var caller = new CancellationTokenSource();
        using var factory = new Factory();
        var source = new TaskCompletionSource<GoogleCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(token => WaitOnly(source.Task, token), factory, caller.Token);
        var second = GoogleCredentialHttpClient.CreateApplicationDefaultVertexClientAsync(_ => source.Task, factory, default);
        try
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await Within(first); });
            Assert.False(source.Task.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Empty(factory.Clients);
        }
        finally
        {
            source.TrySetResult(Token());
            await Within(source.Task);
            try { using var ignored = await Within(first); } catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            using var surviving = await Within(second);
        }
        Assert.Equal(1, Assert.Single(factory.Clients).DisposeCalls);
    }

    [Fact]
    public void FactoryReceivesNoCredentialInitializerBeforeReturningOwnedClient()
    {
        using var factory = new Factory();
        using var client = GoogleCredentialHttpClient.CreateVertexClient(Token(), factory);
        Assert.Equal(0, Assert.Single(factory.InitializerCounts));
        Assert.True(Assert.Single(factory.CredentialWasAbsentAtReturn));
        Assert.NotNull(client.MessageHandler.Credential);
    }

    [Fact]
    public void PreconfiguredCredentialSurvivesMultipleOwnedClients()
    {
        using var fixture = new ServiceFixture(["synthetic-explicit-scope"]);
        var credential = fixture.Credential.ToGoogleCredential();
        using (var first = GoogleCredentialHttpClient.CreateVertexClient(credential, fixture.Factory))
            Assert.Same(fixture.Credential, first.MessageHandler.Credential);
        using (var second = GoogleCredentialHttpClient.CreateVertexClient(credential, fixture.Factory))
            Assert.Same(fixture.Credential, second.MessageHandler.Credential);
        Assert.Equal(new[] { "synthetic-explicit-scope" }, fixture.Credential.Scopes);
        Assert.Equal("synthetic-quota", credential.QuotaProject);
        Assert.All(fixture.Factory.Handlers, handler => Assert.Equal(0, handler.Calls));
    }

    private static GoogleCredential Token() => GoogleCredential.FromAccessToken("synthetic-static-token");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowKnown(Exception error) => throw error;
    private static async Task<T> WaitOnly<T>(Task<T> shared, CancellationToken token)
    {
        var canceled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => canceled.TrySetCanceled())) return await await Task.WhenAny(shared, canceled.Task);
    }
    private static async Task<T> Within<T>(Task<T> task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(15000))); return await task; }

    private sealed class ServiceFixture : IDisposable
    {
        internal RSA Key { get; } = RSA.Create();
        internal Factory Factory { get; } = new();
        internal ServiceAccountCredential Credential { get; }
        internal ServiceFixture(IEnumerable<string>? scopes = null)
        {
            Credential = new(new("synthetic@example.invalid")
            {
                Key = Key,
                Scopes = scopes ?? [],
                QuotaProject = "synthetic-quota",
                ProjectId = "synthetic-project",
                User = "synthetic-user",
                HttpClientFactory = Factory
            });
        }
        public void Dispose() { Factory.Dispose(); Key.Dispose(); }
    }

    private sealed class FailingUserCredential(ClosedFlow flow, Exception failure) : UserCredential(flow, "synthetic-user", new TokenResponse()), IConfigurableHttpClientInitializer
    {
        void IConfigurableHttpClientInitializer.Initialize(ConfigurableHttpClient client) => ThrowKnown(failure);
    }

    private sealed class Factory : IHttpClientFactory, IDisposable
    {
        internal List<OwnedClient> Clients { get; } = [];
        internal List<Handler> Handlers { get; } = [];
        internal List<int> InitializerCounts { get; } = [];
        internal List<bool> CredentialWasAbsentAtReturn { get; } = [];
        internal Action? BeforeCreate { get; init; }
        internal Exception? CreateFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            BeforeCreate?.Invoke();
            if (CreateFailure is { } failure) ThrowKnown(failure);
            var handler = new Handler();
            var client = new OwnedClient(new ConfigurableMessageHandler(handler), DisposeFailure);
            Handlers.Add(handler); Clients.Add(client); InitializerCounts.Add(args.Initializers.Count);
            foreach (var initializer in args.Initializers) initializer.Initialize(client);
            CredentialWasAbsentAtReturn.Add(client.MessageHandler.Credential is null);
            return client;
        }
        public void Dispose() { foreach (var client in Clients) if (client.DisposeCalls == 0) client.Dispose(); }
    }

    private sealed class OwnedClient(ConfigurableMessageHandler handler, Exception? failure) : ConfigurableHttpClient(handler)
    {
        internal int DisposeCalls;
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
            if (disposing && failure is not null) ThrowKnown(failure);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal int Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No credential or service requests are permitted in this fixture."); }
        protected override void Dispose(bool disposing) { if (disposing) Disposed++; base.Dispose(disposing); }
    }
    private sealed class ClosedFlow : IAuthorizationCodeFlow
    {
        internal int Disposed;
        public IAccessMethod AccessMethod { get; } = new BearerToken.AuthorizationHeaderAccessMethod();
        public IClock Clock => SystemClock.Default;
        public IDataStore DataStore => null!;
        public bool ShouldForceTokenRetrieval() => false;
        public void Dispose() => Disposed++;
        public Task<TokenResponse> RefreshTokenAsync(string userId, string refreshToken, CancellationToken token) => throw new InvalidOperationException("No refresh calls permitted");
        public Task<TokenResponse> LoadTokenAsync(string userId, CancellationToken token) => throw new InvalidOperationException("No user files permitted");
        public Task DeleteTokenAsync(string userId, CancellationToken token) => throw new InvalidOperationException("No user files permitted");
        public AuthorizationCodeRequestUrl CreateAuthorizationCodeRequest(string redirectUri) => throw new InvalidOperationException("No authorization redirect permitted");
        public Task<TokenResponse> ExchangeCodeForTokenAsync(string userId, string code, string redirectUri, CancellationToken token) => throw new InvalidOperationException("No code exchange permitted");
        public Task RevokeTokenAsync(string userId, string token, CancellationToken cancellationToken) => throw new InvalidOperationException("No revocation permitted");
    }
}
