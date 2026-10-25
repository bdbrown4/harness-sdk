using System.Text.Json;

namespace Strands.Agents.A2A;

internal static class A2ACardJson
{
    internal static void String(JsonElement value, string property)
    {
        A2AJson.RequireObject(value);
        if (!value.TryGetProperty(property, out var member) || member.ValueKind != JsonValueKind.String)
            throw new A2AProtocolException("A2A discovery member '" + property + "' must be a string.");
    }
    internal static void Strings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new A2AProtocolException("An A2A discovery list must contain strings.");
    }
    internal static void Security(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("A2A security requirements must be an array.");
        foreach (var requirement in value.EnumerateArray())
        {
            A2AJson.RequireObject(requirement);
            foreach (var property in requirement.EnumerateObject()) Strings(property.Value);
        }
    }
}
