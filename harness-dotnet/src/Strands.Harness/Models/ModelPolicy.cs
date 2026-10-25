using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents.Models;

namespace Strands.Harness.Models;

internal static class ModelPolicy
{
    internal static HarnessModelConfiguration Configure(string identifier, HarnessEffort effort, bool caching, bool cachingExplicit, bool webSearch)
    {
        if (!Enum.IsDefined(typeof(HarnessEffort), effort)) throw new ArgumentOutOfRangeException(nameof(effort));
        var (provider, name) = Split(identifier);
        if (!new[] { "bedrock", "bedrock-mantle", "anthropic", "openai", "google", "ollama", "litellm" }.Contains(provider, StringComparer.Ordinal))
            throw new ArgumentException("Unknown model provider '" + provider + "'. Supply a configured model instance or ModelResolver for another provider.", nameof(identifier));
        var family = Family(name);
        var levels = Levels(provider, name);
        var level = effort switch
        {
            HarnessEffort.Auto => levels.Length > 0 ? "high" : null,
            HarnessEffort.Off => levels.Contains("none", StringComparer.Ordinal) ? "none" : null,
            _ => Spelling(effort),
        };
        if (level is not null && !levels.Contains(level, StringComparer.Ordinal))
            throw new ArgumentException("Effort '" + Spelling(effort) + "' is not supported by " + identifier + ". Supported: " + string.Join(", ", levels.Where(value => value != "none")) + " (or auto, off).", nameof(effort));
        var cacheUnsupported = provider == "ollama" || provider == "bedrock" && family.StartsWith("anthropic.claude-3-haiku-", StringComparison.Ordinal);
        if (caching && cacheUnsupported)
        {
            if (cachingExplicit) throw new ArgumentException("Model " + identifier + " does not support prompt caching. Set Caching=false.", nameof(caching));
            HarnessLogging.Warning("model=<{Model}> | prompt caching is unsupported; continuing without it", identifier);
            caching = false;
        }
        var cache = caching && (provider == "anthropic" || provider == "bedrock" && family.StartsWith("anthropic.", StringComparison.Ordinal)) ? new CacheConfig() : null;
        var maxTokens = provider is "bedrock" or "anthropic" ? ClaudeMaxTokens(name) : null;
        if (provider == "anthropic") maxTokens ??= 32000;
        var fields = new Dictionary<string, object?>();
        if (level is not null)
        {
            if (provider == "anthropic" || provider == "bedrock" && family.StartsWith("anthropic.", StringComparison.Ordinal))
                AddClaudeThinking(fields, name, level, maxTokens ?? 32000);
            else if (provider == "google") fields["generationConfig"] = new { thinkingConfig = new { thinkingLevel = level } };
            else if (provider is "openai" or "bedrock-mantle" || provider == "bedrock" && (family.StartsWith("openai.gpt-5.6-", StringComparison.Ordinal) || family == "openai.gpt-6-astra")) fields["reasoning"] = new { effort = level };
            else if (provider == "bedrock" && family.StartsWith("amazon.nova-2", StringComparison.Ordinal)) fields["reasoningConfig"] = new { type = "enabled", maxReasoningEffort = level };
            else fields["reasoning_effort"] = level;
        }
        var search = webSearch && SupportsSearch(identifier);
        if (search)
            fields["tools"] = provider switch
            {
                "anthropic" => new object[] { new { type = "web_search_20260318", name = "web_search", allowed_callers = new[] { "direct" } } },
                "google" => new object[] { new { googleSearch = new { } } },
                "bedrock-mantle" => new object[] { new { type = "web_search", external_web_access = true } },
                _ => new object[] { new { type = "web_search" } },
            };
        return new(identifier, provider, name, level, maxTokens, fields.Count == 0 ? null : JsonSerializer.SerializeToElement(fields), caching, cache, search);
    }

    internal static HarnessEffort ParseEffort(string value) => value switch
    {
        "auto" => HarnessEffort.Auto,
        "off" => HarnessEffort.Off,
        "minimal" => HarnessEffort.Minimal,
        "low" => HarnessEffort.Low,
        "medium" => HarnessEffort.Medium,
        "high" => HarnessEffort.High,
        "xhigh" => HarnessEffort.ExtraHigh,
        "max" => HarnessEffort.Max,
        _ => throw new ArgumentException("Unknown reasoning effort '" + value + "'.", nameof(value)),
    };
    internal static string Spelling(HarnessEffort effort) => effort == HarnessEffort.ExtraHigh ? "xhigh" : effort.ToString().ToLowerInvariant();
    internal static (string Provider, string Name) Split(string identifier)
    {
        Guard.NotBlank(identifier, nameof(identifier));
        var slash = identifier.IndexOf('/');
        var provider = slash < 0 ? "bedrock" : identifier.Substring(0, slash);
        var name = slash < 0 ? identifier : identifier.Substring(slash + 1);
        Guard.NotBlank(provider, nameof(identifier)); Guard.NotBlank(name, nameof(identifier));
        return (provider, name);
    }
    internal static bool SupportsSearch(string identifier)
    {
        var (provider, name) = Split(identifier);
        return SupportsSearch(provider, name);
    }
    internal static bool SupportsSearch(string provider, string name) => provider is "anthropic" or "openai" or "google" || provider == "bedrock-mantle" && (name.StartsWith("openai.gpt-5.", StringComparison.Ordinal) || name.StartsWith("openai.gpt-6-", StringComparison.Ordinal) || name.StartsWith("openai.gpt-6.", StringComparison.Ordinal));
    internal static bool SupportsThinking(string provider, string name) => Levels(provider, name).Length > 0;
    private static string[] Levels(string provider, string name) => provider switch
    {
        "bedrock" => BedrockLevels(Family(name)),
        "anthropic" => ThinkingMode(name) is null ? [] : AnthropicLevels,
        "openai" or "bedrock-mantle" => OpenAiLevels,
        "google" => GoogleLevels,
        _ => Array.Empty<string>(),
    };
    private static readonly string[] AnthropicLevels = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] OpenAiLevels = ["minimal", "low", "medium", "high", "xhigh", "none"];
    private static readonly string[] GoogleLevels = ["minimal", "low", "medium", "high"];
    private static string Family(string name)
    {
        var prefix = new[] { "global.", "apac.", "us.", "eu.", "au.", "jp." }.FirstOrDefault(value => name.StartsWith(value, StringComparison.Ordinal));
        return prefix is null ? name : name.Substring(prefix.Length);
    }
    private static string[] BedrockLevels(string family)
    {
        if (family.StartsWith("anthropic.", StringComparison.Ordinal)) return ThinkingMode(family) is null ? [] : AnthropicLevels;
        if (family.StartsWith("amazon.nova-2", StringComparison.Ordinal)) return ["low", "medium", "high"];
        if (family.StartsWith("openai.gpt-5.6-", StringComparison.Ordinal) || family == "openai.gpt-6-astra") return ["none", "low", "medium", "high", "xhigh", "max"];
        if (family.StartsWith("openai.gpt-oss-", StringComparison.Ordinal)) return ["low", "medium", "high"];
        if (family.StartsWith("qwen.", StringComparison.Ordinal)) return ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
        if (family.StartsWith("xai.", StringComparison.Ordinal)) return ["low", "medium", "high", "xhigh"];
        return [];
    }
    private static int? ClaudeMaxTokens(string name)
    {
        if (new[] { "claude-opus-4-5", "claude-opus-4.5", "claude-sonnet-4-5", "claude-sonnet-4.5", "claude-haiku-4-5", "claude-haiku-4.5" }.Any(value => name.IndexOf(value, StringComparison.Ordinal) >= 0)) return 64000;
        return new[] { "claude-opus-", "claude-sonnet-", "claude-haiku-", "claude-fable-" }.Any(value => name.IndexOf(value, StringComparison.Ordinal) >= 0) ? 128000 : null;
    }
    private static string? ThinkingMode(string name)
    {
        var match = Regex.Match(name, @"claude-(?:(\d{1,2})(?:[-.](\d{1,2}))?-)?(opus|sonnet|haiku|fable|mythos)(?:[-.](\d{1,2}))?(?:[-.](\d{1,2}))?(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return "adaptive";
        var family = match.Groups[3].Value;
        if (family is "fable" or "mythos") return "adaptive";
        var major = match.Groups[4].Success ? match.Groups[4].Value : match.Groups[1].Value;
        var minor = match.Groups[4].Success ? match.Groups[5].Value : match.Groups[2].Value;
        if (major.Length == 0) return "adaptive";
        var version = new Version(int.Parse(major, System.Globalization.CultureInfo.InvariantCulture), minor.Length == 0 ? 0 : int.Parse(minor, System.Globalization.CultureInfo.InvariantCulture));
        if (version >= (family == "haiku" ? new Version(5, 5) : new Version(4, 6))) return "adaptive";
        return version >= new Version(4, 5) ? "extended" : null;
    }
    private static void AddClaudeThinking(Dictionary<string, object?> fields, string name, string effort, int maxTokens)
    {
        if (ThinkingMode(name) == "extended")
        {
            var budget = effort switch { "low" => 2048, "medium" => 8192, "high" => 16384, "xhigh" => 32768, "max" => 49152, _ => throw new ArgumentException("Unsupported Claude reasoning effort.", nameof(effort)) };
            fields["thinking"] = new { type = "enabled", budget_tokens = Math.Min(budget, maxTokens - 1) };
        }
        else
        {
            fields["thinking"] = new { type = "adaptive", display = "summarized" };
            fields["output_config"] = new { effort };
        }
    }
}
