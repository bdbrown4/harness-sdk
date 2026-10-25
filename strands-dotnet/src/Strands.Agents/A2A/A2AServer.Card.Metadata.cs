using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private static void ValidateCardSecurityScheme(JsonElement scheme)
    {
        A2AJson.RequireObject(scheme);
        if (scheme.TryGetProperty("description", out _)) A2ACardJson.String(scheme, "description");
        switch (A2AJson.RequiredString(scheme, "type"))
        {
            case "http":
                _ = A2AJson.RequiredString(scheme, "scheme");
                if (scheme.TryGetProperty("bearerFormat", out _)) A2ACardJson.String(scheme, "bearerFormat");
                break;
            case "apiKey":
                _ = A2AJson.RequiredString(scheme, "name");
                if (A2AJson.RequiredString(scheme, "in") is not ("cookie" or "header" or "query")) throw new A2AProtocolException("An API key location must be cookie, header or query.");
                break;
            case "oauth2":
                var flows = A2AJson.RequiredObject(scheme, "flows");
                foreach (var flow in new[] { "authorizationCode", "implicit", "clientCredentials", "password" })
                    if (flows.TryGetProperty(flow, out var settings))
                    {
                        A2AJson.RequireObject(settings);
                        if (flow is "authorizationCode" or "implicit") RequireCardUri(settings, "authorizationUrl", http: false);
                        if (flow != "implicit") RequireCardUri(settings, "tokenUrl", http: false);
                        if (settings.TryGetProperty("refreshUrl", out _)) RequireCardUri(settings, "refreshUrl", http: false);
                        var scopes = A2AJson.RequiredObject(settings, "scopes");
                        if (scopes.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String)) throw new A2AProtocolException("OAuth scope descriptions must be strings.");
                    }
                if (scheme.TryGetProperty("oauth2MetadataUrl", out _)) RequireCardUri(scheme, "oauth2MetadataUrl", http: false);
                break;
            case "openIdConnect": RequireCardUri(scheme, "openIdConnectUrl", http: false); break;
            case "mutualTLS": break;
            default: throw new A2AProtocolException("The card uses an unsupported A2A security scheme type.");
        }
    }
    private static void ValidateCardExtensions(JsonElement extensions)
    {
        if (extensions.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Card extensions must be an array.");
        foreach (var extension in extensions.EnumerateArray())
        {
            RequireCardUri(extension, "uri", http: false);
            if (extension.TryGetProperty("description", out _)) A2ACardJson.String(extension, "description");
            if (extension.TryGetProperty("params", out _)) _ = A2AJson.RequiredObject(extension, "params");
            if (extension.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new A2AProtocolException("An extension's required flag must be a boolean.");
        }
    }
    private static void ValidateCardSignatures(JsonElement signatures)
    {
        if (signatures.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Card signatures must be an array.");
        foreach (var signature in signatures.EnumerateArray())
        {
            A2ACardJson.String(signature, "protected"); A2ACardJson.String(signature, "signature");
            if (signature.TryGetProperty("header", out _)) _ = A2AJson.RequiredObject(signature, "header");
        }
    }
}
