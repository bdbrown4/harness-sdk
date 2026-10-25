using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private JsonElement _agentCard;
    /// <summary>The detached public discovery card. Reading metadata never invokes an agent factory.</summary>
    public JsonElement AgentCard { get { lock (_gate) return _agentCard; } }
    /// <summary>Atomically replaces detached discovery metadata and skills while retaining the handler's configured runtime capabilities and MIME support. Existing returned cards remain valid.</summary>
    public void UpdateAgentCard(JsonElement card)
    {
        var detached = PrepareCard(card);
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            _agentCard = detached;
        }
    }
    private void InitializeCard()
    {
        var compatibilityModes = _options.CardFormat == A2ACardFormat.Native ? null : new[] { _options.CardFormat == A2ACardFormat.Python ? "text" : "text/plain" };
        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["protocolVersion"] = _options.CardFormat == A2ACardFormat.TypeScript ? "0.2.0" : "0.3.0",
            ["name"] = _options.Name,
            ["description"] = _options.Description,
            ["url"] = _options.Url.AbsoluteUri,
            ["version"] = _options.Version,
            ["preferredTransport"] = "JSONRPC",
            ["capabilities"] = new { streaming = true, pushNotifications = _options.PushNotifications is not null },
            ["supportsAuthenticatedExtendedCard"] = _options.AuthenticatedExtendedCard is not null,
            ["defaultInputModes"] = compatibilityModes ?? _options.InputModes,
            ["defaultOutputModes"] = compatibilityModes ?? _options.OutputModes,
            ["skills"] = _options.Skills.Select(skill => skill.Json).ToArray()
        };
        if (_options.AdditionalCardFields is { } fields)
        {
            A2AJson.RequireObject(fields); A2AJson.ValidateUniqueProperties(fields);
            foreach (var property in fields.EnumerateObject())
            {
                if (members.ContainsKey(property.Name) || property.Name == "supportsAuthenticatedExtendedCard")
                    throw new ArgumentException("Additional card fields cannot replace runtime-bound member '" + property.Name + "'.", nameof(_options));
                members.Add(property.Name, property.Value);
            }
        }
        _agentCard = PrepareCard(JsonSerializer.SerializeToElement(members));
        _authenticatedCard = _options.AuthenticatedExtendedCard is { } extended ? PrepareCard(extended) : null;
    }
}
