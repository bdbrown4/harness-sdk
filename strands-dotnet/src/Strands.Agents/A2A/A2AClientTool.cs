using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A stateless remote-agent tool with an explicit exact-match endpoint allowlist.</summary>
/// <remarks>Each send starts a fresh remote task. Discovery caching, advertised RPC URLs, HTTP redirects, and authentication follow the configured borrowed client.</remarks>
public sealed class A2AClientTool : IRichTool
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, A2AClient> _endpoints;
    /// <summary>The shared pinned description before the permitted endpoint list is appended.</summary>
    public const string DefaultDescription = "Interacts with remote A2A (Agent-to-Agent) protocol agents. Use operation='discover' to fetch an agent card from an endpoint. Use operation='send_message' to send a message and receive a response. Only the listed endpoints are permitted.";
    /// <summary>Copies endpoint bindings. Supplied clients and their HTTP transports remain caller-owned.</summary>
    public A2AClientTool(A2AClientToolOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.AllowedEndpoints, nameof(options));
        if (options.AllowedEndpoints.Count == 0) throw new ArgumentException("At least one allowed endpoint is required.", nameof(options));
        var endpoints = new Dictionary<string, A2AClient>(StringComparer.Ordinal);
        foreach (var entry in options.AllowedEndpoints)
        {
            if (!Uri.TryCreate(entry.Key, UriKind.Absolute, out var uri)) throw new ArgumentException("An absolute endpoint URL is required.", nameof(options));
            A2AHttp.ValidateUri(uri, nameof(options)); Guard.NotNull(entry.Value, nameof(options)); endpoints.Add(entry.Key, entry.Value);
        }
        _endpoints = endpoints;
        var description = options.Description ?? DefaultDescription + " Permitted endpoints: " + string.Join(", ", endpoints.Keys.OrderBy(value => value, StringComparer.Ordinal)) + ".";
        Spec = new(options.Name, description, JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{\"operation\":{\"type\":\"string\",\"enum\":[\"discover\",\"send_message\"]},\"endpoint\":{\"type\":\"string\"},\"message\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"operation\",\"endpoint\"]}"));
    }
    /// <inheritdoc />
    public ToolSpec Spec { get; }
    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        => (await ExecuteAsync(input, context, cancellationToken).ConfigureAwait(false)).GetRawText();
    /// <inheritdoc />
    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var output = await ExecuteAsync(input, context, cancellationToken).ConfigureAwait(false);
        return new(context.ToolUse.ToolUseId, [new JsonBlock(output)], structuredContent: output);
    }
    private async ValueTask<JsonElement> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        Guard.NotNull(context, nameof(context)); cancellationToken.ThrowIfCancellationRequested();
        A2AJson.ValidateUniqueProperties(input);
        var endpoint = A2AJson.RequiredString(input, "endpoint");
        if (!_endpoints.TryGetValue(endpoint, out var client)) throw new A2AClientToolException("Endpoint '" + endpoint + "' is not in the allowed endpoints list. Permitted endpoints: " + string.Join(", ", _endpoints.Keys.OrderBy(value => value, StringComparer.Ordinal)));
        var operation = A2AJson.RequiredString(input, "operation");
        if (operation == "discover")
        {
            try
            {
                var card = await client.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return card;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new A2AClientToolException("Failed to discover agent card at '" + endpoint + "'.", error); }
        }
        if (operation == "send_message")
        {
            var message = A2AJson.OptionalString(input, "message");
            if (string.IsNullOrEmpty(message)) throw new A2AClientToolException("'message' is required for send_message operation.");
            AgentResult result;
            try { result = await new A2AAgent(client).InvokeAsync(message!, new() { InvocationState = context.InvocationState }, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new A2AClientToolException("Failed to send message to '" + endpoint + "'.", error); }
            cancellationToken.ThrowIfCancellationRequested();
            var output = result.Message ?? throw new A2AProtocolException("The remote agent produced no message.");
            if (output.Metadata is { } metadata && metadata.TryGetProperty("a2a", out var remote) && A2AJson.OptionalString(remote, "taskState") is { } state && state != "completed")
                throw new A2AClientToolException("Remote agent at '" + endpoint + "' did not complete: task state is '" + state + "'. " + output.Text);
            return JsonSerializer.SerializeToElement(new { message = new { role = "assistant", content = output.Content } }, OutputJson);
        }
        throw new A2AClientToolException("Unknown operation: '" + operation + "'.");
    }
}

/// <summary>Named endpoint bindings for a stateless A2A tool.</summary>
public sealed record A2AClientToolOptions
{
    /// <summary>Exact endpoint strings mapped to borrowed configured clients. At least one binding is required. The lookup keys authorize tool selection; the configured clients control actual HTTP destinations and credentials.</summary>
    public required IReadOnlyDictionary<string, A2AClient> AllowedEndpoints { get; init; }
    /// <summary>The model-facing name. Defaults to the shared a2a_client identifier.</summary>
    public string Name { get; init; } = "a2a_client";
    /// <summary>An optional description replacing the shared description and generated permitted endpoint list.</summary>
    public string? Description { get; init; }
}

/// <summary>A denied endpoint or unsuccessful remote-agent tool operation.</summary>
public sealed class A2AClientToolException : Exception
{
    /// <summary>Creates a failure while retaining an optional underlying protocol or transport error.</summary>
    public A2AClientToolException(string message, Exception? innerException = null) : base(message, innerException) { }
}
