using System.Net.Http.Headers;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private JsonElement PrepareCard(JsonElement card)
    {
        A2AJson.RequireObject(card);
        if (System.Text.Encoding.UTF8.GetByteCount(card.GetRawText()) > _options.MaxCardBytes) throw new A2AProtocolException("The A2A card exceeds MaxCardBytes.");
        byte[] encoded;
        try { encoded = JsonSerializer.SerializeToUtf8Bytes(card); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new A2AProtocolException("The A2A card exceeds its supported JSON representation.", error); }
        if (encoded.Length > _options.MaxCardBytes) throw new A2AProtocolException("The serialized A2A card exceeds MaxCardBytes.");
        using var parsed = JsonDocument.Parse(encoded); card = parsed.RootElement;
        A2AJson.ValidateUniqueProperties(card);
        foreach (var property in new[] { "name", "description", "version" }) _ = A2AJson.RequiredString(card, property);
        RequireCardUri(card, "url", http: true);
        var protocol = A2AJson.RequiredString(card, "protocolVersion");
        if (!protocol.StartsWith("0.2.", StringComparison.Ordinal) && !protocol.StartsWith("0.3.", StringComparison.Ordinal)) throw new A2AProtocolException("Only A2A 0.2 and 0.3 cards are supported.");
        if (A2AJson.OptionalString(card, "preferredTransport") is { } transport && transport != "JSONRPC") throw new A2AProtocolException("This handler supports the JSONRPC transport.");
        if (!card.TryGetProperty("defaultInputModes", out var defaultsIn) || !card.TryGetProperty("defaultOutputModes", out var defaultsOut))
            throw new A2AProtocolException("A card requires its default input and output modes.");
        ValidateCardModes(defaultsIn, _options.InputModes, nonempty: true);
        ValidateCardModes(defaultsOut, _options.OutputModes, nonempty: true);
        var capabilities = A2AJson.RequiredObject(card, "capabilities");
        foreach (var property in new[] { "streaming", "pushNotifications", "stateTransitionHistory" })
            if (capabilities.TryGetProperty(property, out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new A2AProtocolException("A2A capabilities must be booleans.");
                if (flag.GetBoolean() && (property == "stateTransitionHistory" || property == "pushNotifications" && _options.PushNotifications is null)) throw new A2AProtocolException("The configured handler does not support the advertised capability '" + property + "'.");
            }
        if (capabilities.TryGetProperty("extensions", out var extensions)) ValidateCardExtensions(extensions);
        if (card.TryGetProperty("supportsAuthenticatedExtendedCard", out var extended) && (extended.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || extended.GetBoolean() && _options.AuthenticatedExtendedCard is null))
            throw new A2AProtocolException("An authenticated extended card is not configured.");
        if (!card.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("An A2A card requires a skills array.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in skills.EnumerateArray())
        {
            var skill = new A2AAgentSkill(value);
            if (!identifiers.Add(A2AJson.RequiredString(skill.Json, "id"))) throw new A2AProtocolException("Agent skill identifiers must be unique.");
            if (value.TryGetProperty("inputModes", out var inputModes)) ValidateCardModes(inputModes, _options.InputModes, nonempty: false);
            if (value.TryGetProperty("outputModes", out var outputModes)) ValidateCardModes(outputModes, _options.OutputModes, nonempty: false);
        }
        if (card.TryGetProperty("provider", out var provider))
        { A2AJson.RequireObject(provider); _ = A2AJson.RequiredString(provider, "organization"); RequireCardUri(provider, "url", http: false); }
        foreach (var property in new[] { "documentationUrl", "iconUrl" }) if (card.TryGetProperty(property, out _)) RequireCardUri(card, property, http: false);
        if (card.TryGetProperty("security", out var security)) A2ACardJson.Security(security);
        if (card.TryGetProperty("securitySchemes", out var schemes))
        {
            A2AJson.RequireObject(schemes);
            foreach (var scheme in schemes.EnumerateObject()) ValidateCardSecurityScheme(scheme.Value);
        }
        if (card.TryGetProperty("signatures", out var signatures)) ValidateCardSignatures(signatures);
        if (card.TryGetProperty("additionalInterfaces", out var interfaces))
        {
            if (interfaces.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Additional A2A interfaces must be an array.");
            foreach (var endpoint in interfaces.EnumerateArray())
            {
                if (A2AJson.RequiredString(endpoint, "transport") != "JSONRPC") throw new A2AProtocolException("This handler cannot advertise an unimplemented transport.");
                RequireCardUri(endpoint, "url", http: true);
            }
        }
        return card.Clone();
    }
    private void ValidateCardModes(JsonElement modes, IReadOnlyList<string> configured, bool nonempty)
    {
        A2ACardJson.Strings(modes);
        if (nonempty && modes.GetArrayLength() == 0) throw new A2AProtocolException("A card must advertise at least one input and output mode.");
        foreach (var mode in modes.EnumerateArray())
        {
            var mime = mode.GetString()!;
            if (_options.CardFormat == A2ACardFormat.Python && mime == "text") mime = "text/plain";
            if (!MediaTypeHeaderValue.TryParse(mime, out var parsed) || parsed.Parameters.Count != 0) throw new A2AProtocolException("Card modes must be MIME types without parameters.");
            if (!configured.Any(allowed => MatchesMime(allowed, mime))) throw new A2AProtocolException("The card advertises an unconfigured MIME mode.");
        }
    }
    private static void RequireCardUri(JsonElement value, string property, bool http)
    {
        if (!Uri.TryCreate(A2AJson.RequiredString(value, property), UriKind.Absolute, out var uri) || http && uri.Scheme is not ("http" or "https"))
            throw new A2AProtocolException("An absolute" + (http ? " HTTP" : "") + " URI is required for card member '" + property + "'.");
    }
}
