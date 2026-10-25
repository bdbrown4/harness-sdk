using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Strands.Agents.Models;

public sealed partial class OpenAIResponsesModel
{
    /// <summary>Requests a standalone schema-validated JSON response without advancing managed provider state.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        Guard.NotNull(options, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
            return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        if (request.ModelState is { Count: > 0 })
            throw new ArgumentException("The direct Responses structured helper does not resume managed provider state.", nameof(request));
        if (_format is not null)
            throw new ArgumentException("Cannot mix Responses text.format with a direct structured-output schema.", nameof(options));
        var validator = new StructuredOutputTool<T>(options);
        var body = FormatRequest(request with { Tools = null, ToolChoice = null });
        body.Remove("stream");
        var text = body.TryGetValue("text", out var value) && value is JsonElement element && !IsPythonFalseyTextConfiguration(element)
            ? ProviderConfiguration.Parameters(ProviderConfiguration.Snapshot(element)) : new Dictionary<string, object?>();
        if (text.ContainsKey("format"))
            throw new ArgumentException("Cannot mix Responses text.format with a direct structured-output schema.", nameof(options));
        JsonElement? nativeTools = _parameters is { } parameters && parameters.TryGetProperty("tools", out var tools) ? tools : null;
        if (nativeTools is { } configuredTools)
            foreach (var tool in configuredTools.EnumerateArray())
                if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "function" && tool.TryGetProperty("function", out _))
                    throw new ArgumentException("Direct structured output requires native Responses function tools, not JSON Chat/Pydantic wrappers.", nameof(options));
        text["format"] = new { type = "json_schema", name = validator.Spec.Name, schema = validator.Spec.InputSchema, strict = true };
        body["text"] = text;
        var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(body) };
        HttpResponseMessage? response = null;
        Exception? primary = null;
        T result;
        try
        {
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ProviderHttp.ResolveApiKeyAsync(_options.ApiKey, _options.ApiKeyProvider, cancellationToken).ConfigureAwait(false));
            outgoing.Headers.Accept.Add(new("application/json"));
            response = await ProviderHttp.SendAsync(_client, outgoing, "OpenAI Responses", cancellationToken, new[] { "application/json" }).ConfigureAwait(false);
            var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(stream, "OpenAI Responses", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            result = ParseStructuredResponse(document.RootElement, nativeTools, validator, cancellationToken);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            try { response?.Dispose(); }
            catch (Exception error) { cleanup.Add(error); }
            try { outgoing.Dispose(); }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count > 0)
            {
                var owner = primary ?? cleanup[0];
                var secondary = primary is null ? cleanup.Skip(1) : cleanup;
                foreach (var error in secondary)
                {
                    if (ReferenceEquals(owner, error)) continue;
                    if (owner.Data["Strands.CleanupErrors"] is not List<Exception> errors) owner.Data["Strands.CleanupErrors"] = errors = [];
                    if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error);
                }
                if (primary is null) ExceptionDispatchInfo.Capture(owner).Throw();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool IsPythonFalseyTextConfiguration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False => true,
        JsonValueKind.String => value.GetString()!.Length == 0,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number == 0,
        _ => false,
    };

    private static T ParseStructuredResponse<T>(JsonElement root, JsonElement? tools, StructuredOutputTool<T> validator, CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new StructuredOutputException("Responses structured output requires an output array.");
        T first = default!;
        var found = false;
        foreach (var item in output.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = ProviderHttp.RequiredString(item, "type");
            if (type == "function_call")
            {
                ParseStrictFunctionArguments(item, tools);
                continue;
            }
            if (type != "message") continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new StructuredOutputException("Responses output messages require a content array.");
            foreach (var part in content.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ProviderHttp.RequiredString(part, "type") != "output_text") continue;
                T parsed;
                try
                {
                    if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                        throw new StructuredOutputException("Responses output_text requires a JSON text string.");
                    using var value = JsonDocument.Parse(text.GetString()!);
                    parsed = validator.ParseOutput(value.RootElement, cancellationToken);
                }
                catch (JsonException error) { throw new StructuredOutputException("Responses structured output contains invalid JSON text.", error); }
                if (!found) { first = parsed; found = true; }
            }
        }
        if (!found) throw new StructuredOutputException("No valid parsed output found in the Responses response.");
        return first;
    }

    private static void ParseStrictFunctionArguments(JsonElement call, JsonElement? tools)
    {
        if (tools is null) return;
        var name = ProviderHttp.RequiredString(call, "name");
        foreach (var tool in tools.Value.EnumerateArray())
        {
            if (ProviderHttp.OptionalString(tool, "type") != "function" || ProviderHttp.OptionalString(tool, "name") != name) continue;
            if (!tool.TryGetProperty("strict", out var strict) || strict.ValueKind != JsonValueKind.True) return;
            if (!call.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
                throw new StructuredOutputException("Responses strict function arguments require a JSON text string.");
            try { using var parsed = JsonDocument.Parse(arguments.GetString()!); }
            catch (JsonException error) { throw new StructuredOutputException("Responses strict function arguments contain invalid JSON.", error); }
            return;
        }
    }

    internal static bool IsApiThrottling(string code, string message) =>
        code.Equals("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
        new[] { "rate_limit_exceeded", "rate limit", "too many requests" }.Any(pattern => message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

    internal static bool IsApiContextOverflow(string code, string message) =>
        code.Equals("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
        new[] { "maximum context length", "context_length_exceeded", "too many tokens", "context length", "input is too long for requested model", "input length and `max_tokens` exceed context limit", "too many total text bytes", "exceeds the max_model_len" }.Any(pattern => message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) ||
        Regex.IsMatch(message, "exceed(?:s|ed)?(?: customer)? model maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
