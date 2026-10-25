using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

public sealed partial class GoogleModel
{
    private readonly Uri? _structuredEndpoint;
    /// <summary>Requests one bounded, schema-validated JSON response through GenerateContent.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
            return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        var validator = new StructuredOutputTool<T>(options);
        var body = GoogleFormatting.Request(request with { Tools = null, ToolChoice = null }, _parameters, _generation, _builtIns, _options.UseVertexAi);
        var generation = GoogleFormatting.Generation(_parameters, _generation);
        generation.Remove("responseSchema");
        generation.Remove("responseFormat");
        generation["responseMimeType"] = "application/json";
        generation["responseJsonSchema"] = validator.Spec.InputSchema;
        body["generationConfig"] = generation;
        using var outgoing = CreateRequest(ResolveStructuredEndpoint(), body, "application/json");
        var response = await SendAsync(outgoing, "application/json", cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(stream, "Google", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _)) throw CreateError(root);
            var text = StructuredText(root);
            try
            {
                using var output = JsonDocument.Parse(text.Trim());
                return validator.ParseOutput(output.RootElement, cancellationToken);
            }
            catch (JsonException error) { throw new StructuredOutputException("Google structured output contains invalid JSON.", error); }
        }
        catch (Exception error) { primary = error; throw; }
        finally { DisposeResponse(response, primary); }
    }

    private Uri ResolveStructuredEndpoint()
    {
        if (_structuredEndpoint is { } endpoint) return endpoint;
        const string suffix = ":streamGenerateContent";
        if (!_endpoint.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal))
            throw new NotSupportedException("A custom streaming URL requires StructuredOutputEndpoint for native structured output.");
        var query = _endpoint.Query.TrimStart('?').Split('&')
            .Where(part => !string.Equals(Uri.UnescapeDataString(part.Split('=')[0]), "alt", StringComparison.OrdinalIgnoreCase));
        return new UriBuilder(_endpoint)
        {
            Path = _endpoint.AbsolutePath.Substring(0, _endpoint.AbsolutePath.Length - suffix.Length) + ":generateContent",
            Query = string.Join("&", query)
        }.Uri;
    }

    private static string StructuredText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != 1)
            throw new StructuredOutputException("Google structured output requires exactly one candidate.");
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object) throw new StructuredOutputException("Google structured candidate must be an object.");
        if (candidate.TryGetProperty("finishReason", out var reason) && (reason.ValueKind != JsonValueKind.String || reason.GetString() != "STOP"))
            throw new StructuredOutputException("Google structured output did not finish successfully.");
        if (!candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new StructuredOutputException("Google structured output requires content parts.");
        if (content.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "model"))
            throw new StructuredOutputException("Google structured output role must be model.");
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || part.TryGetProperty("functionCall", out _))
                throw new StructuredOutputException("Google structured output contains an unexpected content part or function call.");
            if (part.TryGetProperty("thought", out var thought))
            {
                if (thought.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new StructuredOutputException("Google thought markers must be boolean.");
                if (thought.ValueKind == JsonValueKind.True) continue;
            }
            if (!part.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                throw new StructuredOutputException("Google structured output contains a non-text response part.");
            text.Append(value.GetString());
        }
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new StructuredOutputException("Google structured output requires JSON text.");
        return text.ToString();
    }
}
