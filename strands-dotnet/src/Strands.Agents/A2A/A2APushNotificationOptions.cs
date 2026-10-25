using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>Opt-in push delivery with explicit host authorization and borrowed dependencies.</summary>
public sealed record A2APushNotificationOptions
{
    /// <summary>Sends a detached task snapshot to one detached, authorized configuration. The server awaits delivery and retains ownership of neither callback nor its resources.</summary>
    public required Func<JsonElement, JsonElement, CancellationToken, ValueTask> SendAsync { get; init; }
    /// <summary>Authorizes every configuration created through a message or set request, including its destination and credentials. The request remains borrowed.</summary>
    public required Func<HttpRequestMessage, JsonElement, CancellationToken, ValueTask<bool>> AuthorizeConfiguration { get; init; }
    /// <summary>A borrowed configuration store. Defaults to a bounded in-memory store.</summary>
    public IA2APushNotificationConfigStore ConfigStore { get; init; } = new InMemoryA2APushNotificationConfigStore();
    /// <summary>Observes a delivery failure without changing the persisted task outcome. Observer failures are traced. Without an observer, the failure type is traced without credentials.</summary>
    public Action<string, Exception>? DeliveryFailed { get; init; }
}
