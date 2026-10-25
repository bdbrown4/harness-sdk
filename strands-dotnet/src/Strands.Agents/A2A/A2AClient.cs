using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A native A2A JSON-RPC and HTTP+JSON client borrowing its HttpClient and handlers.</summary>
public sealed partial class A2AClient
{
    private readonly HttpClient _client;
    private readonly A2AClientOptions _options;
    private readonly Uri _cardUri;
    private readonly SemaphoreSlim _cardLock = new(1, 1);
    private JsonElement? _card;
    private ClientEndpoint? _endpoint;
    private sealed record ClientEndpoint(Uri Url, bool Rest);
    internal Uri DiscoveryUrl => _options.Url;

    /// <summary>Creates a transport without performing network I/O.</summary>
    public A2AClient(HttpClient client, A2AClientOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        A2AHttp.ValidateUri(options.Url, nameof(options.Url));
        Guard.NotNullOrWhiteSpace(options.AgentCardPath, nameof(options.AgentCardPath));
        if (options.Endpoint is { } endpoint) A2AHttp.ValidateUri(endpoint, nameof(options.Endpoint));
        if (options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResponseBytes must be positive.");
        _client = client;
        _options = options;
        _cardUri = new(options.Url, options.AgentCardPath);
        A2AHttp.ValidateUri(_cardUri, nameof(options.AgentCardPath));
    }

    /// <summary>Discovers and caches the remote card after successful validation.</summary>
    public async Task<JsonElement> GetAgentCardAsync(CancellationToken cancellationToken = default)
    {
        await _cardLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_card is { } existing) return existing;
            using var request = new HttpRequestMessage(HttpMethod.Get, _cardUri);
            request.Headers.Accept.Add(new("application/json"));
            var response = await A2AHttp.SendAsync(_client, request, "application/json", cancellationToken).ConfigureAwait(false);
            Exception? primary = null;
            try
            {
                var card = await A2AHttp.ReadJsonAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                var endpoint = ValidateCard(card);
                _endpoint = endpoint with { Url = _options.Endpoint ?? endpoint.Url };
                _card = card;
                return card;
            }
            catch (Exception error) { primary = error; throw; }
            finally { A2AHttp.Dispose(response, primary); }
        }
        finally { _cardLock.Release(); }
    }

    /// <summary>Retrieves and caches an authenticated extended card using the caller's configured HTTP credentials.</summary>
    public async Task<JsonElement> GetAuthenticatedExtendedCardAsync(CancellationToken cancellationToken = default)
    {
        var card = await CallAsync("agent/getAuthenticatedExtendedCard", cancellationToken: cancellationToken).ConfigureAwait(false);
        var endpoint = ValidateCard(card);
        await _cardLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _endpoint = endpoint with { Url = _options.Endpoint ?? endpoint.Url }; _card = card; }
        finally { _cardLock.Release(); }
        return card;
    }

    private static ClientEndpoint ValidateCard(JsonElement card)
    {
        A2AJson.RequireObject(card);
        foreach (var field in new[] { "name", "description", "version" }) _ = A2AJson.RequiredString(card, field);
        var protocol = A2AJson.OptionalString(card, "protocolVersion");
        if (protocol is not null && !protocol.StartsWith("0.2.", StringComparison.Ordinal) && !protocol.StartsWith("0.3.", StringComparison.Ordinal))
            throw new NotSupportedException("Only A2A protocol versions 0.2 and 0.3 are supported.");
        _ = A2AJson.RequiredObject(card, "capabilities");
        foreach (var field in new[] { "defaultInputModes", "defaultOutputModes" })
            if (!card.TryGetProperty(field, out var modes) || modes.ValueKind != JsonValueKind.Array || modes.EnumerateArray().Any(mode => mode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(mode.GetString())))
                throw new A2AProtocolException($"Agent card {field} must contain media-type strings.");
        if (!card.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Agent card skills must be an array.");
        var url = A2AJson.RequiredString(card, "url");
        var transport = A2AJson.OptionalString(card, "preferredTransport") ?? "JSONRPC";
        if (transport is not ("JSONRPC" or "HTTP+JSON"))
        {
            if (!card.TryGetProperty("additionalInterfaces", out var interfaces) || interfaces.ValueKind != JsonValueKind.Array)
                throw new NotSupportedException("The agent does not advertise a supported JSONRPC or HTTP+JSON interface.");
            var alternative = interfaces.EnumerateArray().FirstOrDefault(value => A2AJson.OptionalString(value, "transport") is "JSONRPC" or "HTTP+JSON");
            if (alternative.ValueKind == JsonValueKind.Undefined) throw new NotSupportedException("The agent does not advertise a supported JSONRPC or HTTP+JSON interface.");
            url = A2AJson.RequiredString(alternative, "url");
            transport = A2AJson.RequiredString(alternative, "transport");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new A2AProtocolException("The agent card requires an absolute HTTP or HTTPS URL.");
        return new(endpoint, transport == "HTTP+JSON");
    }
}
