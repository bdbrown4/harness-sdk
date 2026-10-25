namespace Strands.Agents.Mcp;

/// <summary>Pre-registered OAuth credentials for the machine-to-machine client_credentials grant.</summary>
public sealed record McpClientCredentials
{
    /// <summary>The pre-registered client identifier.</summary>
    public required string ClientId { get; init; }
    /// <summary>The client secret, sent only to the discovered token endpoint.</summary>
    public required string ClientSecret { get; init; }
    /// <summary>Advisory scopes, used when the protected resource supplies no required scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
    /// <summary>Returns a credential-safe description without identifiers or secrets.</summary>
    public override string ToString() => nameof(McpClientCredentials) + " { credentials redacted }";
}

/// <summary>Portable caching and byte limits for a client-credentials HTTP handler.</summary>
public sealed record McpClientCredentialsHandlerOptions
{
    /// <summary>Whether to renew expired tokens proactively, matching Python. False retains TypeScript's reactive 401 behavior.</summary>
    public bool RefreshOnExpiry { get; init; }
    /// <summary>The maximum buffered resource request body, required for an exact authentication replay. Defaults to 16 MiB.</summary>
    public int MaximumRequestBodyBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>The maximum protected-resource or authorization-server metadata response. Defaults to 256 KiB.</summary>
    public int MaximumMetadataBytes { get; init; } = 256 * 1024;
    /// <summary>The maximum token response. Defaults to 64 KiB.</summary>
    public int MaximumTokenBytes { get; init; } = 64 * 1024;
}
