using System.Text;
using Amazon.Runtime;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockMantleTests
{
    [Theory]
    [InlineData("openai.gpt-5.6-luna", "/openai/v1/")]
    [InlineData("openai.gpt-6-astra", "/openai/v1/")]
    [InlineData("openai.gpt-6.1-sol", "/openai/v1/")]
    [InlineData("xai.grok-4.3", "/openai/v1/")]
    [InlineData("google.gemma-4-31b", "/openai/v1/")]
    [InlineData("google.gemma-3-27b-it", "/v1/")]
    [InlineData("openai.gpt-oss-120b", "/v1/")]
    [InlineData("openai.gpt-6oss-20b", "/v1/")]
    [InlineData("gpt-5.4", "/v1/")]
    [InlineData("unrecognized.future-model", "/v1/")]
    public void RoutesBothApiSurfacesByThePinnedModelFamily(string modelId, string basePath)
    {
        var config = new BedrockMantleConfig { Region = "us-west-2" };

        Assert.Equal("https://bedrock-mantle.us-west-2.api.aws" + basePath + "responses", config.ResolveEndpoint(modelId).AbsoluteUri);
        Assert.Equal("https://bedrock-mantle.us-west-2.api.aws" + basePath + "chat/completions", config.ResolveEndpoint(modelId, "chat/completions").AbsoluteUri);
    }

    [Fact]
    public void RuntimeRoutingSupportsProfileIdsAndNeverUsesTheMantleBasePath()
    {
        var config = new BedrockMantleConfig { Region = "us-east-1", Endpoint = BedrockOpenAIEndpoint.Runtime };

        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/responses", config.ResolveEndpoint("global.openai.gpt-oss-120b").AbsoluteUri);
    }

    [Theory]
    [InlineData("us-east-1@attacker.invalid")]
    [InlineData("us-east-1/redirect")]
    [InlineData("us-east-1\n")]
    [InlineData("US-EAST-1")]
    [InlineData("us-east-١")]
    public void RejectsInvalidRegionsBeforeResolvingCredentials(string region)
    {
        var credentials = new CountingCredentials();
        var config = new BedrockMantleConfig { Region = region, Credentials = credentials };

        Assert.Throws<ArgumentException>(() => config.ResolveEndpoint("model"));
        Assert.Throws<ArgumentException>(() => config.CreateTokenProvider());
        Assert.Equal(0, credentials.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(43201)]
    public void RejectsInvalidTokenLifetimesBeforeResolvingCredentials(int expiry)
    {
        var credentials = new CountingCredentials();
        var config = new BedrockMantleConfig { Region = "us-east-1", Credentials = credentials, ExpiresInSeconds = expiry };

        Assert.Throws<ArgumentOutOfRangeException>(() => config.CreateTokenProvider());
        Assert.Equal(0, credentials.Calls);
    }

    [Fact]
    public async Task SignsRealBedrockBearerTokensWithFreshBorrowedCredentialsOnEveryCall()
    {
        var credentials = new CountingCredentials();
        var config = new BedrockMantleConfig { Region = "us-east-1", Credentials = credentials, ExpiresInSeconds = 900 };
        var provider = config.CreateTokenProvider();

        var first = Decode(await provider(default));
        var second = Decode(await provider(default));

        Assert.Contains("bedrock.amazonaws.com/?", first);
        Assert.EndsWith("&Version=1", first);
        Assert.Contains("Action=CallWithBearerToken", first);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", first);
        Assert.Contains("X-Amz-Expires=900", first);
        Assert.Contains("%2Fus-east-1%2Fbedrock%2Faws4_request", first);
        Assert.Contains("X-Amz-Security-Token=session%2Btoken%2F1", first);
        Assert.Contains("X-Amz-Security-Token=session%2Btoken%2F2", second);
        Assert.Contains("X-Amz-Signature=", first);
        Assert.DoesNotContain("secret", first);
        Assert.Equal(2, credentials.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledTokenDoesNotResolveAwsCredentials()
    {
        var credentials = new CountingCredentials();
        var provider = new BedrockMantleConfig { Region = "us-east-1", Credentials = credentials }.CreateTokenProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await provider(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, credentials.Calls);
    }

    [Fact]
    public void ConfigurationDiagnosticsDoNotRenderTheBorrowedCredentialObject()
    {
        var config = new BedrockMantleConfig { Region = "us-east-1", Credentials = new CountingCredentials() };

        Assert.DoesNotContain("secret", config.ToString());
        Assert.Contains("us-east-1", config.ToString());
    }

    [Fact]
    public async Task DirectTokenProviderCancellationStopsWaitingForUncooperativeAwsCredentials()
    {
        var credentials = new DelayedCredentials();
        var provider = new BedrockMantleConfig { Region = "us-east-1", Credentials = credentials }.CreateTokenProvider();
        using var cancellation = new CancellationTokenSource();
        var resolving = provider(cancellation.Token).AsTask();
        await credentials.Entered.Task;
        cancellation.Cancel();
        try
        {
            Assert.Same(resolving, await Task.WhenAny(resolving, Task.Delay(TimeSpan.FromSeconds(2))));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            credentials.Released.TrySetResult(new("access", "secret", null));
            try { await resolving; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("", "412e5f6b0c16d623ad4bbf2c379b5fc8bc774fbb6ef07b7e12ec77ed97b89d6e")]
    [InlineData("session+token/with=padding", "e6cb66f65e8327b857eb5f77f483c6cc5f8f91fd55eb97f0d7827e9819230b26")]
    public void SignatureMatchesAnIndependentSigV4CanonicalRequestVector(string session, string expected)
    {
        // AWS documentation's example credentials, independently signed with Python's
        // hashlib/hmac at this fixed instant. These are not live AWS credentials.
        var credentials = new ImmutableCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", session);
        var token = BedrockMantleConfig.SignToken(credentials, "us-west-2", 3600, new(2025, 7, 15, 5, 0, 0, DateTimeKind.Utc));

        var decoded = Decode(token);
        var fields = decoded.Substring(decoded.IndexOf('?') + 1).Split('&')
            .Select(field => field.Split(new[] { '=' }, 2)).ToDictionary(field => field[0], field => Uri.UnescapeDataString(field[1]), StringComparer.Ordinal);
        Assert.Equal(expected, fields["X-Amz-Signature"]);
        Assert.Equal("20250715T050000Z", fields["X-Amz-Date"]);
        Assert.Equal("host", fields["X-Amz-SignedHeaders"]);
        Assert.Equal("1", fields["Version"]);
        Assert.Equal(session.Length == 0 ? 8 : 9, fields.Count);
        if (session.Length > 0) Assert.Equal(session, fields["X-Amz-Security-Token"]);
    }

    private static string Decode(string token)
    {
        Assert.StartsWith("bedrock-api-key-", token);
        return Encoding.UTF8.GetString(Convert.FromBase64String(token.Substring("bedrock-api-key-".Length)));
    }
    private sealed class CountingCredentials : AWSCredentials
    {
        public int Calls { get; private set; }
        public override ImmutableCredentials GetCredentials() => new("access-key-" + ++Calls, "secret", "session+token/" + Calls);
        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(GetCredentials());
        public override string ToString() => "secret";
    }
    private sealed class DelayedCredentials : AWSCredentials
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImmutableCredentials> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("Only asynchronous credential resolution is permitted.");
        public override Task<ImmutableCredentials> GetCredentialsAsync() { Entered.TrySetResult(true); return Released.Task; }
    }
}
