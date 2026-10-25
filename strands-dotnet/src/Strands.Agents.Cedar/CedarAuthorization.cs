using System.Text.Json;

namespace Strands.Agents.Interventions.Cedar;

/// <summary>Authorizes tool calls with the official Cedar policy engine and optional MCP schema generator.</summary>
public sealed class CedarAuthorization : InterventionHandler
{
    /// <summary>The application-state key containing per-tool accepted authorization counts.</summary>
    public const string CallCountsStateKey = "cedar-authorization";
    private readonly CedarAuthorizationOptions _options;
    private readonly JsonElement _entities;
    private readonly ToolSpec[]? _tools;
    private readonly Func<object, JsonElement> _authorize;
    private Configuration _configuration;

    /// <summary>Creates an authorization handler and validates its policy configuration.</summary>
    public CedarAuthorization(CedarAuthorizationOptions options) : this(options, request => NativeCedar.Execute("authorize", request)) { }

    internal CedarAuthorization(CedarAuthorizationOptions options, Func<object, JsonElement> authorize)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.Policies is null) throw new ArgumentNullException(nameof(options.Policies));
        if (options.Principal is not null && options.PrincipalResolver is not null) throw new ArgumentException("Provide Principal or PrincipalResolver, not both.", nameof(options));
        if (options.Entities is not null && options.EntitiesPath is not null) throw new ArgumentException("Provide Entities or EntitiesPath, not both.", nameof(options));
        if (!Enum.IsDefined(typeof(InterventionErrorPolicy), options.OnError)) throw new ArgumentOutOfRangeException(nameof(options.OnError));
        if (!Enum.IsDefined(typeof(CedarEngineExceptionPolicy), options.EngineExceptionPolicy)) throw new ArgumentOutOfRangeException(nameof(options.EngineExceptionPolicy));
        _options = options;
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _entities = options.Entities?.Clone() ?? JsonSerializer.SerializeToElement(Array.Empty<object>());
        _tools = options.Tools?.Select(tool => tool is null ? throw new ArgumentException("Tools cannot contain null.", nameof(options)) : new ToolSpec(tool.Name, tool.Description, tool.InputSchema)).ToArray();
        _configuration = LoadConfiguration();
    }
    /// <inheritdoc/>
    public override string Name => "cedar-authorization";
    /// <inheritdoc/>
    public override InterventionErrorPolicy OnError => _options.OnError;
    /// <summary>Evaluates synchronously in the native engine. Cancellation is checked before and after the non-preemptible native operation.</summary>
    public override ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = string.IsNullOrEmpty(_options.Namespace) ? string.Empty : _options.Namespace + "::";
        var principal = _options.Principal ?? (_options.PrincipalResolver is { } resolve ? resolve(value.InvocationState) : new(prefix + "User", "anonymous"));
        cancellationToken.ThrowIfCancellationRequested();
        if (principal is null || string.IsNullOrEmpty(principal.Type) || string.IsNullOrEmpty(principal.Id)) return new(new Deny("No principal identity found in invocation state"));
        var extra = _options.ContextEnricher?.Invoke(new(value.ToolUse.Name, value.ToolUse.Input.Clone(), value.InvocationState));
        if (extra is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("ContextEnricher must return a JSON object.");
        var configuration = Volatile.Read(ref _configuration);
        lock (value.Agent.State)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = value.Agent.State.Get<Dictionary<string, long>>(CallCountsStateKey) ?? new(StringComparer.Ordinal);
            counts.TryGetValue(value.ToolUse.Name, out var current);
            if (current < 0) throw new InvalidOperationException("Cedar call counts cannot be negative.");
            var next = checked(current + 1);
            var session = extra?.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            session["hour_utc"] = DateTime.UtcNow.Hour;
            session["call_count"] = next;
            JsonElement answer;
            try
            {
                answer = _authorize(new
                {
                    principal = new { type = principal.Type, id = principal.Id },
                    action = new { type = prefix + "Action", id = value.ToolUse.Name },
                    resource = new { type = prefix + "Resource", id = prefix.Length == 0 ? "agent" : "default" },
                    context = new { input = value.ToolUse.Input, session },
                    policies = new { staticPolicies = configuration.Policies },
                    entities = configuration.Entities,
                    schema = _options.UseSchemaForAuthorization ? configuration.Schema : null,
                    validateRequest = _options.UseSchemaForAuthorization
                });
            }
            catch (Exception error) when (_options.EngineExceptionPolicy == CedarEngineExceptionPolicy.Deny)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(new Deny("Cedar evaluation failed: " + error.Message));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (answer.GetProperty("type").GetString() == "failure") return new(new Deny("Cedar evaluation failed: " + Errors(answer.GetProperty("errors"))));
            var response = answer.GetProperty("response");
            if (response.GetProperty("decision").GetString() == "allow")
            {
                counts[value.ToolUse.Name] = next;
                value.Agent.State.Set(CallCountsStateKey, counts);
                return new(new Proceed());
            }
            var diagnostics = response.GetProperty("diagnostics");
            var reasons = diagnostics.GetProperty("reason").EnumerateArray().Select(reason => reason.GetString() ?? string.Empty)
                .Concat(diagnostics.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("error").GetProperty("message").GetString() ?? string.Empty)).Where(reason => reason.Length > 0);
            var details = string.Join(", ", reasons);
            return new(new Deny("Access denied by Cedar policy" + (details.Length == 0 ? string.Empty : ": " + details)));
        }
    }
    /// <summary>Re-reads and validates all configured source files before atomically replacing the active configuration.</summary>
    public void Reload() => Volatile.Write(ref _configuration, LoadConfiguration());
    /// <summary>Resets this agent's persisted Cedar call counters.</summary>
    public void ResetCallCounts(Agent agent)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        lock (agent.State) agent.State.Set(CallCountsStateKey, new Dictionary<string, long>(StringComparer.Ordinal));
    }

    private Configuration LoadConfiguration()
    {
        var policies = _options.PoliciesAreInline ? _options.Policies : LoadSource(_options.Policies, ".cedar");
        var entities = _options.EntitiesPath is { } path ? ReadEntities(path) : _entities.Clone();
        if (entities.ValueKind != JsonValueKind.Array) throw new ArgumentException("Cedar entities must be a JSON array.", nameof(_options.Entities));
        CheckParse("parsePolicies", new { staticPolicies = policies }, "Invalid Cedar policy");
        CheckParse("parseEntities", new { entities }, "Invalid Cedar entities");
        var schema = _options.Schema is { } explicitSchema ? (_options.SchemaIsInline ? explicitSchema : LoadSource(explicitSchema, ".cedarschema")) : _tools is { } tools ? GenerateSchema(tools, _options.Namespace) : null;
        if (schema is not null)
        {
            var validation = NativeCedar.Execute("validate", new { schema, policies = new { staticPolicies = policies } });
            if (validation.GetProperty("type").GetString() == "failure") throw new ArgumentException("Cedar policy validation failed: " + Errors(validation.GetProperty("errors")));
            var errors = validation.GetProperty("validationErrors").EnumerateArray();
            var messages = new List<string>();
            foreach (var error in errors)
            {
                var message = error.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
                // The baseline auto-schema contract only rejects unknown actions:
                // application-enriched session context is not in the tool schema.
                if (_options.Schema is not null || message.IndexOf("unrecognized action", StringComparison.Ordinal) >= 0 || message.IndexOf("unable to find an applicable action", StringComparison.Ordinal) >= 0)
                    messages.Add(error.GetProperty("policyId").GetString() + ": " + message);
            }
            if (messages.Count > 0) throw new ArgumentException("Cedar policy validation failed: " + string.Join(", ", messages));
        }
        return new(policies, entities, schema);
    }

    private static string GenerateSchema(ToolSpec[] tools, string? ns)
    {
        var name = string.IsNullOrEmpty(ns) ? "Agent" : ns;
        var stub = $"namespace {name} {{ @mcp_principal entity User; @mcp_resource entity Resource; @mcp_context(\"session\") type SessionContext = {{ hour_utc: Long, call_count: Long }}; }}";
        var schema = NativeCedar.Execute("generateSchema", new { stub, tools = tools.Select(tool => new { name = tool.Name, description = tool.Description, inputSchema = tool.InputSchema }).ToArray() }, schema: true).GetProperty("schema").GetString()!;
        if (!string.IsNullOrEmpty(ns)) return schema;
        var start = schema.IndexOf('{');
        var end = schema.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The Cedar generator returned an invalid namespace wrapper.");
        return schema.Substring(start + 1, end - start - 1).Replace("Agent::", string.Empty);
    }

    private static void CheckParse(string operation, object request, string prefix)
    {
        var result = NativeCedar.Execute(operation, request);
        if (result.GetProperty("type").GetString() == "failure") throw new ArgumentException(prefix + ": " + Errors(result.GetProperty("errors")));
    }

    private static string Errors(JsonElement errors) => string.Join(", ", errors.EnumerateArray().Select(error => error.GetProperty("message").GetString()));
    private static string LoadSource(string value, string suffix) => value.EndsWith(suffix, StringComparison.Ordinal) ? File.ReadAllText(value) : value;
    private static JsonElement ReadEntities(string path) { using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }
    private sealed record Configuration(string Policies, JsonElement Entities, string? Schema);
}
