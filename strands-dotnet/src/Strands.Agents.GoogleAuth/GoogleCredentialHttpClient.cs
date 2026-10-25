using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;

namespace Strands.Agents.GoogleAuth;

/// <summary>Creates caller-owned Vertex authentication transports using official Google credentials.</summary>
public static class GoogleCredentialHttpClient
{
    /// <summary>Creates an owned transport that borrows the supplied credential.</summary>
    public static ConfigurableHttpClient CreateVertexClient(GoogleCredential credential) =>
        CreateVertexClient(credential, new HttpClientFactory());

    /// <summary>Explicitly discovers official application-default credentials and creates an owned transport.</summary>
    public static Task<ConfigurableHttpClient> CreateApplicationDefaultVertexClientAsync(CancellationToken cancellationToken = default) =>
        CreateApplicationDefaultVertexClientAsync(GoogleCredential.GetApplicationDefaultAsync, new HttpClientFactory(), cancellationToken);

    // Per-call official-type inputs for deterministic tests, with no process-wide hooks or public factory API.
    internal static ConfigurableHttpClient CreateVertexClient(GoogleCredential credential, IHttpClientFactory factory)
    {
        if (credential is null) throw new ArgumentNullException(nameof(credential));
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        var configured = credential.IsCreateScopedRequired
            ? credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform")
            : credential;
        ConfigurableHttpClient? client = null;
        try
        {
            // Own the returned client before invoking a credential initializer that may throw.
            client = factory.CreateHttpClient(new CreateHttpClientArgs());
            ((IConfigurableHttpClientInitializer)configured).Initialize(client);
            return client;
        }
        catch (Exception primary)
        {
            try { client?.Dispose(); }
            catch (Exception cleanup) { AddCleanup(primary, cleanup); }
            throw;
        }
    }

    internal static async Task<ConfigurableHttpClient> CreateApplicationDefaultVertexClientAsync(
        Func<CancellationToken, Task<GoogleCredential>> resolve, IHttpClientFactory factory, CancellationToken cancellationToken)
    {
        if (resolve is null) throw new ArgumentNullException(nameof(resolve));
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        cancellationToken.ThrowIfCancellationRequested();
        var credential = await resolve(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateVertexClient(credential, factory);
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors)
            primary.Data[key] = errors = new List<Exception>();
        foreach (var error in errors)
            if (ReferenceEquals(error, cleanup)) return;
        errors.Add(cleanup);
    }
}
