using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Models;
namespace Strands.Harness.Models;

internal static class ReadMediaPolicy
{
    internal static bool Supports(IModel? model, string? identifier)
    {
        if (model is RoutingModel router) return router.Candidates.All(candidate => Supports(candidate.Model, null));
        if (model is BedrockModel bedrock) return SupportsIdentifier(bedrock.ModelId);
        if (identifier is null) return true;
        return !identifier.StartsWith("bedrock/", StringComparison.Ordinal) || SupportsIdentifier(identifier.Substring(8));
    }
    private static bool SupportsIdentifier(string name)
    {
        foreach (var prefix in new[] { "global.", "apac.", "us.", "eu.", "au.", "jp." })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name.Substring(prefix.Length); break; }
        return !name.StartsWith("openai.", StringComparison.Ordinal);
    }
}
