using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;

namespace Strands.Agents.Bedrock;

/// <summary>The Amazon Bedrock endpoint family serving OpenAI-compatible requests.</summary>
public enum BedrockOpenAIEndpoint
{
    /// <summary>The Mantle endpoint, whose base path depends on the model family.</summary>
    Mantle,
    /// <summary>The Bedrock Runtime endpoint, including cross-region inference profiles.</summary>
    Runtime
}

/// <summary>AWS credentials and routing for native OpenAI-compatible Bedrock requests.</summary>
public sealed record BedrockMantleConfig
{
    /// <summary>The AWS region, otherwise resolved from AWS environment variables and the SDK's region chain.</summary>
    public string? Region { get; init; }
    /// <summary>Caller-owned refreshable AWS credentials, otherwise the standard AWS credential chain.</summary>
    public AWSCredentials? Credentials { get; init; }
    /// <summary>The bearer token lifetime in seconds, from 1 through 43200; defaults to 43200.</summary>
    public int ExpiresInSeconds { get; init; } = 43200;
    /// <summary>The endpoint family; defaults to Mantle.</summary>
    public BedrockOpenAIEndpoint Endpoint { get; init; }
    /// <summary>Describes routing settings without rendering credentials.</summary>
    public override string ToString() => $"BedrockMantleConfig {{ Region = {Region}, Endpoint = {Endpoint}, ExpiresInSeconds = {ExpiresInSeconds} }}";
    /// <summary>Resolves the complete AWS endpoint for responses or chat/completions without acquiring credentials.</summary>
    public Uri ResolveEndpoint(string modelId, string apiPath = "responses")
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("A model identifier is required.", nameof(modelId));
        if (apiPath is not ("responses" or "chat/completions")) throw new ArgumentException("The API path must be responses or chat/completions.", nameof(apiPath));
        var region = ResolveRegion();
        ValidateOptions();
        if (Endpoint == BedrockOpenAIEndpoint.Runtime)
            return new($"https://bedrock-runtime.{region}.amazonaws.com/openai/v1/{apiPath}");
        var openAIPath = new[] { "openai.gpt-5.", "openai.gpt-6-", "openai.gpt-6.", "xai.grok-4.", "google.gemma-4-" }.Any(prefix => modelId.StartsWith(prefix, StringComparison.Ordinal));
        return new($"https://bedrock-mantle.{region}.api.aws{(openAIPath ? "/openai/v1/" : "/v1/")}{apiPath}");
    }
    /// <summary>Creates a caller-owned delegate that resolves fresh AWS credentials and signs a new bearer token for each request.</summary>
    public Func<CancellationToken, ValueTask<string>> CreateTokenProvider()
    {
        var region = ResolveRegion();
        ValidateOptions();
        var suppliedCredentials = Credentials;
        var requestedLifetime = ExpiresInSeconds;
        return async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = new AmazonBedrockRuntimeConfig { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region) };
            var credentials = suppliedCredentials ?? await AwaitAsync(DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync(config), cancellationToken).ConfigureAwait(false)
                ?? throw new AmazonClientException("The AWS credential chain did not resolve credentials for Bedrock.");
            var immutable = await AwaitAsync(credentials.GetCredentialsAsync(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var signedAt = DateTime.UtcNow;
            var lifetime = requestedLifetime;
            if (credentials is RefreshingAWSCredentials { Expiration: { } expiration })
            {
                var remaining = Math.Floor((expiration.ToUniversalTime() - signedAt).TotalSeconds);
                if (remaining < 1) throw new AmazonClientException("AWS credentials expired before a Bedrock bearer token could be signed.");
                lifetime = (int)Math.Min(lifetime, remaining);
            }
            return SignToken(immutable, region, lifetime, signedAt);
        };
    }

    private string ResolveRegion()
    {
        var region = Region;
        if (string.IsNullOrEmpty(region)) region = Environment.GetEnvironmentVariable("AWS_REGION");
        if (string.IsNullOrEmpty(region)) region = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        if (string.IsNullOrEmpty(region)) region = FallbackRegionFactory.GetRegionEndpoint()?.SystemName;
        if (region is null or { Length: 0 }) throw new ArgumentException("An AWS region is required through configuration or the AWS region chain.", nameof(Region));
        if (region.Length > 128 || !Regex.IsMatch(region, @"\A[a-z]{2}(-[a-z]+)+-[0-9]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("The AWS region identifier is invalid.", nameof(Region));
        return region;
    }

    private void ValidateOptions()
    {
        if (ExpiresInSeconds is <= 0 or > 43200) throw new ArgumentOutOfRangeException(nameof(ExpiresInSeconds), "The token lifetime must be between 1 and 43200 seconds.");
        if (!Enum.IsDefined(typeof(BedrockOpenAIEndpoint), Endpoint)) throw new ArgumentOutOfRangeException(nameof(Endpoint));
    }

    internal static string SignToken(ImmutableCredentials credentials, string region, int lifetime, DateTime signedAt)
    {
        using var request = new DefaultRequest(new TokenRequest(), "Bedrock")
        {
            Endpoint = new("https://bedrock.amazonaws.com/"),
            ResourcePath = "/",
            HttpMethod = "POST",
            UseQueryString = true
        };
        request.ParameterCollection.Add("Action", "CallWithBearerToken");
        request.ParameterCollection.Add("X-Amz-Expires", lifetime.ToString(CultureInfo.InvariantCulture));
        if (credentials.UseToken) request.ParameterCollection.Add("X-Amz-Security-Token", credentials.Token);
        // Use AWS's signer rather than maintaining a separate SigV4 implementation. The token
        // envelope follows the official aws-bedrock-token-generator protocol, version 1.
        var signature = AWS4PreSignedUrlSigner.SignRequest(request, null, null, credentials.AccessKey, credentials.SecretKey, "bedrock", region, signedAt);
        var query = "Action=CallWithBearerToken&X-Amz-Expires=" + lifetime.ToString(CultureInfo.InvariantCulture);
        if (credentials.UseToken) query += "&X-Amz-Security-Token=" + Uri.EscapeDataString(credentials.Token);
        var signed = "bedrock.amazonaws.com/?" + query + "&" + signature.ForQueryParameters + "&Version=1";
        return "bedrock-api-key-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(signed));
    }

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (!task.IsCompleted && await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAsync(task);
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The cancelled caller no longer owns a wait; observe the AWS provider's eventual result. */ }
    }

    private sealed class TokenRequest : AmazonWebServiceRequest;
}
