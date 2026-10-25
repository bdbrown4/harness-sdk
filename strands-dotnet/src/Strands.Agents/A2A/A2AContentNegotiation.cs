using System.Net.Http.Headers;
using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private static IReadOnlyList<string> SnapshotModes(IReadOnlyList<string> modes)
    {
        Guard.NotNull(modes, nameof(modes));
        if (modes.Count == 0 || modes.Any(mode => !MediaTypeHeaderValue.TryParse(mode, out var parsed) || parsed.Parameters.Count != 0)) throw new ArgumentException("At least one valid MIME type without parameters is required.", nameof(modes));
        return Array.AsReadOnly(modes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private IReadOnlyList<string> SelectOutputModes(JsonElement? configuration)
    {
        if (configuration is not { } settings || !settings.TryGetProperty("acceptedOutputModes", out var accepted)) return _options.OutputModes;
        if (accepted.ValueKind != JsonValueKind.Array || accepted.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || !MediaTypeHeaderValue.TryParse(value.GetString(), out var parsed) || parsed.Parameters.Count != 0))
            throw new A2ARpcException(-32602, "acceptedOutputModes must contain MIME type strings.");
        var requested = accepted.EnumerateArray().Select(value => value.GetString()!).ToArray();
        var selected = _options.OutputModes.SelectMany(mode => requested.Select(value => MatchesMime(value, mode) ? mode : MatchesMime(mode, value) ? value : null))
            .Where(mode => mode is not null).Select(mode => mode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0) throw new A2ARpcException(-32005, "No accepted output mode is supported by this agent.");
        return Array.AsReadOnly(selected);
    }
    private static void ValidatePartModes(JsonElement parts, IReadOnlyList<string> allowed)
    {
        foreach (var part in parts.EnumerateArray())
        {
            var mode = A2AJson.RequiredString(part, "kind") switch
            {
                "text" => "text/plain",
                "data" => "application/json",
                "file" => A2AJson.OptionalString(A2AJson.RequiredObject(part, "file"), "mimeType") ?? "application/octet-stream",
                _ => throw new A2ARpcException(-32005, "The message contains an unsupported part type.")
            };
            if (!allowed.Any(value => MatchesMime(value, mode))) throw new A2ARpcException(-32005, "The content MIME type is not accepted by this interaction.");
        }
    }
    private static bool MatchesMime(string accepted, string actual)
        => string.Equals(accepted, actual, StringComparison.OrdinalIgnoreCase) || accepted == "*/*" ||
           accepted.EndsWith("/*", StringComparison.Ordinal) && actual.StartsWith(accepted.Substring(0, accepted.Length - 1), StringComparison.OrdinalIgnoreCase);
}
