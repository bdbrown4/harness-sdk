using System.Text.Json;

namespace Strands.Agents.Interventions.Cedar;

/// <summary>How an exception raised by the native authorization operation is handled.</summary>
public enum CedarEngineExceptionPolicy
{
    /// <summary>Propagates the original exception to OnError, matching TypeScript.</summary>
    UseHandlerPolicy,
    /// <summary>Denies before OnError can permit, matching Python. Requested cancellation still propagates.</summary>
    Deny
}

/// <summary>A typed Cedar entity identifier. The native engine handles quoting and escaping.</summary>
/// <param name="Type">The Cedar entity type, including its optional namespace.</param>
/// <param name="Id">The entity identifier.</param>
public sealed record CedarPrincipal(string Type, string Id);

/// <summary>The context available to a session-context enricher.</summary>
/// <param name="ToolName">The original registered tool name.</param>
/// <param name="ToolInput">Detached JSON arguments.</param>
/// <param name="InvocationState">The original caller-owned invocation state.</param>
public sealed record CedarContext(string ToolName, JsonElement ToolInput, IReadOnlyDictionary<string, object?> InvocationState);

/// <summary>Configures genuine native Cedar authorization. Callback and agent ownership remain with the caller.</summary>
public sealed class CedarAuthorizationOptions
{
    /// <summary>Inline Cedar policies, or a UTF-8 file path ending in .cedar.</summary>
    public required string Policies { get; init; }
    /// <summary>Treats Policies as inline text even when a final comment ends with .cedar. Defaults to source-compatible suffix-based loading.</summary>
    public bool PoliciesAreInline { get; init; }
    /// <summary>Inline Cedar schema, or a UTF-8 file path ending in .cedarschema. Takes precedence over generation from Tools.</summary>
    public string? Schema { get; init; }
    /// <summary>Treats Schema as inline text even when it ends with .cedarschema.</summary>
    public bool SchemaIsInline { get; init; }
    /// <summary>Uses the validated schema during native context/entity parsing and request validation, matching Python. False preserves TypeScript's policy-validation-only default.</summary>
    public bool UseSchemaForAuthorization { get; init; }
    /// <summary>Controls native operation exceptions; defaults to the TypeScript handler error policy. Choose Deny for Python's engine-exception behavior.</summary>
    public CedarEngineExceptionPolicy EngineExceptionPolicy { get; init; }
    /// <summary>MCP tool specifications passed to the official schema generator when no explicit schema is supplied.</summary>
    public IReadOnlyList<ToolSpec>? Tools { get; init; }
    /// <summary>Inline Cedar entity JSON array. Mutually exclusive with EntitiesPath.</summary>
    public JsonElement? Entities { get; init; }
    /// <summary>A UTF-8 JSON entity file re-read by Reload. Mutually exclusive with Entities.</summary>
    public string? EntitiesPath { get; init; }
    /// <summary>The optional Cedar namespace. A namespace changes the default resource identifier from agent to default.</summary>
    public string? Namespace { get; init; }
    /// <summary>A fixed identity, defaulting to User::"anonymous" in the configured namespace. Mutually exclusive with PrincipalResolver.</summary>
    public CedarPrincipal? Principal { get; init; }
    /// <summary>Resolves an identity from the exact caller-owned state; null or an empty identity denies. Mutually exclusive with Principal.</summary>
    public Func<IReadOnlyDictionary<string, object?>, CedarPrincipal?>? PrincipalResolver { get; init; }
    /// <summary>Returns extra context.session fields. Native hour_utc and call_count always take precedence.</summary>
    public Func<CedarContext, JsonElement>? ContextEnricher { get; init; }
    /// <summary>The callback error policy, defaulting to Throw. Engine error results deny; native operation exceptions follow EngineExceptionPolicy.</summary>
    public InterventionErrorPolicy OnError { get; init; } = InterventionErrorPolicy.Throw;
}
