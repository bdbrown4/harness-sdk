using System.Text.Json;

namespace Strands.Agents.Models;

public sealed partial class OllamaModel
{
    /// <summary>Requests one bounded, schema-validated JSON response through Ollama's non-streaming chat protocol.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
            return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        var validator = new StructuredOutputTool<T>(options);
        var body = FormatRequest(request with { Tools = null, ToolChoice = null });
        body["stream"] = false;
        body["format"] = validator.Spec.InputSchema;
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(body) };
        outgoing.Headers.Accept.Add(new("application/json"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "Ollama", cancellationToken, ["application/json"]).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(stream, "Ollama", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ModelException("Ollama structured output requires a JSON response object.");
            if (root.TryGetProperty("error", out _)) throw ProviderHttp.CreateError("Ollama", root);
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True) throw new ModelException("Ollama non-streaming response must contain done=true.");
            if (root.TryGetProperty("done_reason", out var reason) && (reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop"))
                throw new StructuredOutputException("Ollama structured output did not finish successfully.");
            var message = ProviderHttp.RequiredObject(root, "message");
            if (message.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")) throw new ModelException("Ollama structured response role must be assistant.");
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null && (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0))
                throw new StructuredOutputException("Ollama structured output returned unexpected tool calls.");
            var text = ProviderHttp.OptionalString(message, "content");
            if (string.IsNullOrWhiteSpace(text)) throw new StructuredOutputException("Ollama structured output requires JSON text content.");
            try
            {
                using var output = JsonDocument.Parse(text!.Trim());
                return validator.ParseOutput(output.RootElement, cancellationToken);
            }
            catch (JsonException error) { throw new StructuredOutputException("Ollama structured output contains invalid JSON.", error); }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { response.Dispose(); }
            catch (Exception cleanup) { if (primary is null) throw; AddCleanup(primary, cleanup); }
        }
    }
}
