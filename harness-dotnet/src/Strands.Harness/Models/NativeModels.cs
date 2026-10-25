using System.Net.Http;
using System.Net.Http.Headers;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Models;

namespace Strands.Harness.Models;

internal static class NativeModels
{
    internal static IModel Create(HarnessModelConfiguration configuration, ICollection<IAsyncDisposable> owned, Func<HttpClient>? transportFactory = null)
    {
        if (configuration.Provider == "bedrock")
            return new BedrockModel(new()
            {
                ModelId = configuration.ModelId,
                Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-west-2",
                MaxTokens = configuration.MaxTokens,
                AdditionalModelRequestFields = configuration.Parameters,
                CacheSystemPrompt = configuration.CacheConfig?.SystemPrompt == true,
                CacheTools = configuration.CacheConfig?.Tools == true,
                CacheLastMessage = configuration.CacheConfig?.Messages == true,
            });
        var client = transportFactory is null ? new HttpClient() : transportFactory() ?? throw new InvalidOperationException("The native model transport factory returned null.");
        owned.Add(new HttpResource(client));
        switch (configuration.Provider)
        {
            case "bedrock-mantle":
                var mantle = new BedrockMantleConfig();
                return new OpenAIResponsesModel(client, new(configuration.ModelId, mantle.CreateTokenProvider())
                {
                    Endpoint = mantle.ResolveEndpoint(configuration.ModelId),
                    Params = configuration.Parameters,
                    MaxTokens = configuration.MaxTokens,
                });
            case "openai":
                client.BaseAddress = BaseAddress(Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1");
                return new OpenAIResponsesModel(client, new(configuration.ModelId, RequiredKey("OPENAI_API_KEY")) { Params = configuration.Parameters, MaxTokens = configuration.MaxTokens });
            case "anthropic":
                client.BaseAddress = VersionedAddress(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com");
                return new AnthropicModel(client, new(configuration.ModelId, RequiredKey("ANTHROPIC_API_KEY")) { Params = configuration.Parameters, MaxTokens = configuration.MaxTokens ?? 32000, CacheConfig = configuration.CacheConfig });
            case "google":
                var fields = configuration.Parameters;
                return new GoogleModel(client, new(configuration.ModelId, Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? RequiredKey("GEMINI_API_KEY"))
                {
                    Params = fields,
                });
            case "ollama":
                client.BaseAddress = BaseAddress(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://127.0.0.1:11434");
                if (Environment.GetEnvironmentVariable("OLLAMA_API_KEY") is { Length: > 0 } key) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return new OllamaModel(client, new(configuration.ModelId));
            case "litellm":
                client.BaseAddress = VersionedAddress(Environment.GetEnvironmentVariable("LITELLM_BASE_URL") ?? "http://127.0.0.1:4000");
                return new OpenAIModel(client, new(configuration.ModelId, Environment.GetEnvironmentVariable("LITELLM_API_KEY") ?? "litellm"));
            default: throw new ArgumentException("Unknown native model provider '" + configuration.Provider + "'.", nameof(configuration));
        }
    }

    private static string RequiredKey(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
        : throw new InvalidOperationException("Set " + variable + " or supply an explicitly configured model or ModelFactory.");
    private static Uri BaseAddress(string value)
    {
        var uri = new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("Model base URLs must use HTTP or HTTPS.", nameof(value));
        return uri;
    }
    private static Uri VersionedAddress(string value)
    {
        value = value.TrimEnd('/');
        return BaseAddress(value.EndsWith("/v1", StringComparison.Ordinal) ? value : value + "/v1");
    }
    private sealed class HttpResource(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { client.Dispose(); return default; }
    }
}
