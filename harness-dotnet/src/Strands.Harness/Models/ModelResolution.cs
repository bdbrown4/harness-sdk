using Strands.Agents;

namespace Strands.Harness.Models;

internal static class ModelResolution
{
    internal sealed record PolicySnapshot(string? Identifier, HarnessEffort Effort, bool? Caching, bool SearchRequested, bool SearchExplicit, HarnessWebSearchOptions Search,
        Func<HarnessModelConfiguration, IModel>? Factory, Func<string, IModel>? Resolver);

    internal static PolicySnapshot Capture(HarnessOptions options) => new(options.ModelId, options.Effort, options.Caching,
        (options.BuiltinTools ?? Harness.BuiltinToolNames).Contains("web_search", StringComparer.Ordinal),
        options.WebSearchExplicit ?? options.BuiltinTools is not null, options.WebSearch, options.ModelFactory, options.ModelResolver);

    internal readonly record struct Resolved(IModel Model, bool Owned, string? Identifier, bool NativeWebSearch);

    internal static Resolved Main(HarnessOptions options, ICollection<IAsyncDisposable> resources)
    {
        if (options.ConfiguredModelFeaturesApplied && (options.AgentOptions?.Model ?? options.Model) is { } configuredModel &&
            (options.ConfiguredModel is not { } original || ReferenceEquals(original.Instance, configuredModel)))
        {
            if (options.ConfiguredPolicy is { } policy && policy != Capture(options))
                throw new ArgumentException("Resolved model settings were changed after ToOptions. Rebind the portable configuration through ToOptions or construct fresh HarnessOptions; an already configured model cannot be reconfigured in place.", nameof(options));
            return new(configuredModel, options.AgentOptions?.OwnsModel == true,
                options.ConfiguredModel is { } configured && ReferenceEquals(configured.Instance, configuredModel) ? configured.Identifier : null,
                options.ConfiguredNativeWebSearch);
        }
        if ((options.AgentOptions?.Model ?? options.Model) is { } supplied)
        {
            WarnPrebuilt(options);
            _ = NativeSearch(options, null);
            return new(supplied, options.AgentOptions?.OwnsModel == true, null, false);
        }
        var identifier = options.ModelId ?? Harness.DefaultModel;
        var configurable = options.ModelFactory is not null || options.ModelResolver is null;
        var search = NativeSearch(options, configurable ? identifier : null);
        return Identifier(options, identifier, options.Effort, options.Caching ?? true, options.Caching == true, search, resources);
    }

    internal static Resolved Identifier(HarnessOptions options, string identifier, HarnessEffort effort, bool caching, bool cachingExplicit, bool nativeSearch, ICollection<IAsyncDisposable> resources, HarnessModelConfiguration? configured = null)
    {
        if (options.ModelFactory is not null)
        {
            var configuration = configured ?? ModelPolicy.Configure(identifier, effort, caching, cachingExplicit, nativeSearch);
            return new(options.ModelFactory(configuration) ?? throw new InvalidOperationException("ModelFactory returned null for '" + identifier + "'."), false, identifier, nativeSearch);
        }
        if (options.ModelResolver is not null)
        {
            WarnPrebuilt(options with { Effort = effort, Caching = cachingExplicit ? caching : null });
            return new(options.ModelResolver(identifier) ?? throw new InvalidOperationException("ModelResolver returned null for '" + identifier + "'."), false, identifier, false);
        }
        var policy = configured ?? ModelPolicy.Configure(identifier, effort, caching, cachingExplicit, nativeSearch);
        return new(NativeModels.Create(policy, resources), true, identifier, nativeSearch);
    }

    internal static bool NativeSearch(HarnessOptions options, string? configurableIdentifier)
    {
        if (!(options.BuiltinTools ?? Harness.BuiltinToolNames).Contains("web_search", StringComparer.Ordinal)) return false;
        if (!Enum.IsDefined(typeof(WebSearchProvider), options.WebSearch.Provider)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown web search provider.");
        if (options.WebSearch.Provider == WebSearchProvider.Exa)
        {
            HarnessLogging.Warning("web_search uses the explicitly selected Exa service; search queries are sent directly to its MCP endpoint.");
            return false;
        }
        if (configurableIdentifier is not null && ModelPolicy.SupportsSearch(configurableIdentifier)) return true;
        if (options.WebSearchExplicit ?? options.BuiltinTools is not null)
            throw new NotSupportedException("Native web_search is unavailable for this model. Select Exa explicitly, supply a supported model identifier with ModelFactory or native resolution, or disable web_search.");
        HarnessLogging.Information("web_search disabled: the selected model does not expose configurable native search.");
        return false;
    }

    internal static void OwnAuxiliary(Resolved resolved, ICollection<IAsyncDisposable> resources)
    {
        if (resolved.Owned && resolved.Model is IAsyncDisposable asynchronous) resources.Add(asynchronous);
        else if (resolved.Owned && resolved.Model is IDisposable disposable) resources.Add(new SynchronousResource(disposable));
    }

    private static void WarnPrebuilt(HarnessOptions options)
    {
        if (options.Effort != HarnessEffort.Auto) HarnessLogging.Warning("effort ignored for a supplied model instance; configure effort on the model or supply ModelFactory.");
        if (options.Caching == true) HarnessLogging.Warning("caching ignored for a supplied model instance; configure caching on the model or supply ModelFactory.");
    }

    private sealed class SynchronousResource(IDisposable disposable) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { disposable.Dispose(); return default; }
    }
}
