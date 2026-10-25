using Strands.Agents;
using Strands.Harness.Models;

namespace Strands.Harness;

/// <summary>Classifies the Harness reasoning and native search policy without resolving or constructing a model.</summary>
public static class HarnessModelCapabilities
{
    /// <summary>Reports whether the identifier has supported reasoning levels; null selects <see cref="Harness.DefaultModel"/>.</summary>
    public static bool SupportsThinking(string? identifier = null)
    {
        var (provider, name) = Split(identifier);
        return ModelPolicy.SupportsThinking(provider, name);
    }

    /// <summary>Reports reasoning support for a ready model without inspecting it; null selects the default identifier.</summary>
    public static bool SupportsThinking<TModel>(TModel? model) where TModel : IModel => model is null ? SupportsThinking() : true;

    /// <summary>Reports whether the identifier supports native web search; null selects <see cref="Harness.DefaultModel"/>.</summary>
    public static bool SupportsWebSearch(string? identifier = null)
    {
        var (provider, name) = Split(identifier);
        return ModelPolicy.SupportsSearch(provider, name);
    }

    /// <summary>Reports native search support for a ready model without inspecting it; null selects the default identifier.</summary>
    public static bool SupportsWebSearch<TModel>(TModel? model) where TModel : IModel => model is null && SupportsWebSearch();

    private static (string Provider, string Name) Split(string? identifier)
    {
        var value = identifier ?? Harness.DefaultModel;
        var slash = value.IndexOf('/');
        return slash < 0 ? ("bedrock", value) : (value.Substring(0, slash), value.Substring(slash + 1));
    }
}
