using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Harness.Models;

internal static class AuxiliaryModels
{
    internal static IModel Resolve(HarnessOptions options, AgentOptions core, ICollection<IAsyncDisposable> owned)
    {
        if ((core.AuxModel ?? options.AuxModel) is { } explicitModel) return Concrete(explicitModel);
        var identifier = options.ConfiguredModel is { } configured && ReferenceEquals(configured.Instance, core.Model)
            ? configured.Identifier : options.AgentOptions is null && options.Model is null ? options.ModelId ?? Harness.DefaultModel : null;
        if (identifier is null) return Concrete(core.Model);
        var slash = identifier.IndexOf('/');
        var provider = slash < 0 ? "bedrock" : identifier.Substring(0, slash);
        var name = slash < 0 ? identifier : identifier.Substring(slash + 1);
        string? small;
        if (provider == "bedrock") small = BedrockSmallModel(name);
        else if (provider is "ollama" or "litellm") small = null;
        else if ((provider == "anthropic" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"))) ||
            (provider == "openai" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_BASE_URL")))) small = null;
        else small = provider switch
        {
            "anthropic" => "claude-haiku-4-5-20251001",
            "openai" => "gpt-5.6-luna",
            "bedrock-mantle" => "openai.gpt-5.6-luna",
            "google" => "gemini-3.5-flash",
            _ => throw new NotSupportedException("No default auxiliary model is configured for provider '" + provider + "'. Supply AuxModel explicitly."),
        };
        if (small is null)
        {
            HarnessLogging.Warning("model=<{Model}> | no mapped auxiliary model is available; rebuilding the same model with reasoning and caching off. Supply AuxModel to override it.", identifier);
        }
        var resolved = ModelResolution.Identifier(options, small is null ? identifier : provider + "/" + small, HarnessEffort.Off, false, false, false, owned);
        ModelResolution.OwnAuxiliary(resolved, owned);
        return Concrete(resolved.Model);
    }

    private static IModel Concrete(IModel model) => model is RoutingModel router ? router.DefaultModel : model;
    private static string? BedrockSmallModel(string name)
    {
        var prefix = new[] { "global.", "apac.", "us.", "eu.", "au.", "jp." }.FirstOrDefault(candidate => name.StartsWith(candidate, StringComparison.Ordinal)) ?? "";
        var family = name.Substring(prefix.Length);
        if (family.StartsWith("anthropic.", StringComparison.Ordinal)) return "global.anthropic.claude-haiku-4-5-20251001-v1:0";
        if (family.StartsWith("openai.", StringComparison.Ordinal)) return prefix + "openai.gpt-5.6-luna";
        if (family.StartsWith("amazon.", StringComparison.Ordinal)) return "amazon.nova-lite-v1:0";
        if (family.StartsWith("meta.", StringComparison.Ordinal)) return "meta.llama3-2-3b-instruct-v1:0";
        if (family.StartsWith("mistral.", StringComparison.Ordinal)) return "mistral.mistral-small-2402-v1:0";
        return null;
    }
}
