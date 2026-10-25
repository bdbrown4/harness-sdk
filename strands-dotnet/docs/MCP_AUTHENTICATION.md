# MCP client credentials

`McpClientCredentialsHandler` implements the machine-to-machine `client_credentials` grant with `client_secret_basic`. It is a terminal `HttpMessageHandler` with an owned, redirect-disabled HTTP transport. It does not map credentials into the official .NET SDK's interactive OAuth provider, which implements a different grant.

```csharp
using var http = new HttpClient(new McpClientCredentialsHandler(
    new Uri("https://tools.example.com/mcp"),
    new McpClientCredentials
    {
        ClientId = clientId,
        ClientSecret = clientSecret,
        Scopes = ["tools:read"],
    }));

await using var client = await McpClient.CreateHttpAsync(
    http,
    new HttpClientTransportOptions
    {
        Endpoint = new Uri("https://tools.example.com/mcp"),
    });
```

The core MCP client borrows `http`. Dispose the protocol client before disposing the HTTP client. The handler never owns the original request or its content; the enclosing `HttpClient` retains its framework-specific ownership behavior. In particular, Framework's `HttpClient` can dispose outgoing content after a send. Standard outer handlers remain application-owned composition; the terminal authentication handler has no replaceable inner transport. Use the HTTP client's timeout or a caller cancellation token to bound the complete operation.

Harness construction owns its declarative authenticated connections and HTTP clients:

```csharp
McpServers = McpServerConfiguration.FromServers(
    new Dictionary<string, McpServerOptions>
    {
        ["docs"] = new()
        {
            Url = "https://tools.example.com/mcp",
            Auth = new() { ClientId = clientId, ClientSecret = clientSecret },
        },
    });
```

JSON accepts TypeScript's `clientId`/`clientSecret` or Python's `client_id`/`client_secret`, plus optional `scopes`. Combining aliases is rejected. Existing `${VAR}` and `${env:VAR}` expansion applies before the established per-server resolution policy. Unknown credential keys produce diagnostics containing key names, never their values. Malformed fields retain the existing `continueOnError` behavior. Authentication requires streamable HTTP: SSE and stdio configurations fail explicitly. Python also rejects non-streamable credentials; the pinned TypeScript stdio builder ignores them, which this native API deliberately refuses to do.

An initial 401 discovers protected-resource metadata from the Bearer challenge or the endpoint's well-known paths. Missing or empty `authorization_servers` falls back to the resource origin. The first listed authorization server is selected after validating the complete issuer list. OAuth and OIDC well-known discovery paths are supported, followed by the source-compatible `/token` fallback when metadata is unavailable. Metadata must identify the configured resource or an enclosing resource path on the same origin. Declared authorization-server issuer identity must match discovery; a single trailing separator is normalized. The token endpoint must advertise the supported grant and Basic method when those lists are present.

Credentials are sent only to that validated token endpoint. Token endpoints require HTTPS outside .NET's loopback addresses and `.localhost` development names. Discovery, token and resource requests never follow redirects. Resource requests must retain the configured scheme, host, port, escaped path and query; the handler refuses another endpoint before performing I/O. A separately hosted authorization server is valid when selected by verified metadata. Discovery metadata itself is the trust input supplied by the configured resource, rather than an independent allowlist of authorization servers.

The handler buffers a detached request body and headers for at most one authenticated replay. Ordinary 403 responses pass through. An `insufficient_scope` challenge accumulates previously requested, granted and newly required scopes. Concurrent callers share a successful acquisition; cancellation of a queued caller does not cancel the leader. A canceled acquisition does not publish an incomplete or late token. Invalid metadata or tokens are rejected before authenticated replay. Responses and original request content remain subject to their normal caller ownership.

Defaults follow TypeScript's reactive cache: expiration alone does not renew a token until a resource challenge. Set `McpClientCredentialsHandlerOptions.RefreshOnExpiry = true`, or harness JSON `authOptions.refreshOnExpiry`, to select the Python-style proactive expiration check. The handler keeps its discovered issuer/token endpoint for its lifetime. Recreate it to change credentials or discovery authority. No refresh-token or browser authorization flow is used.

Native limits are 16 MiB for buffered resource request bodies, 256 KiB for each metadata response, and 64 KiB for a token response. Both declared and chunked response bodies are bounded. Override `MaximumRequestBodyBytes`, `MaximumMetadataBytes` and `MaximumTokenBytes`, or the corresponding camel-case `authOptions` fields. Limits must be positive Int32 byte counts. Bearer values reject whitespace/control/non-ASCII characters; `expires_in` must be a bounded nonnegative integer. Basic credentials use UTF-8 with percent-encoded identifier and secret components. These checked native wire policies are explicit compatibility choices rather than a claim that all incidental JavaScript/Python coercions are reproduced.

The source baseline is commit `3bd98e618320f0f286acbfb2252f72388030421b`: TypeScript's `strands-ts/src/mcp/client.ts` credential types/construction and `strands-ts/src/mcp/config.node.ts` declarative binding; Python's `strands-py/src/strands/tools/mcp/mcp_client.py`, `mcp_types.py` and `_compat.py`. The harness lock pins `@modelcontextprotocol/client` 2.3.1. Python's SDK permits `mcp>=1.23,<2.2`; compatibility helpers select the available client-credentials extension, while harness test extras pin the older family. Scope accumulation and discovery behavior are checked against that exact TypeScript dependency archive, whose SHA-256 is `0FEBE399E355B703D4E9FA6EB1453C5FA06B06C78E2421D797DCB145FB9BB68F`.

Loopback tests exercise actual HTTP challenge/discovery/token/replay traffic and the official native MCP protocol client. They do not establish authorization against a live identity provider. Interactive authorization, private-key JWT, general custom auth-provider persistence and declarative routers remain separate parity work. This slice does not establish full SDK or harness parity.
