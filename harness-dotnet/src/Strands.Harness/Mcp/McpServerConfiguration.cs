using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents.Mcp;

namespace Strands.Harness.Mcp;

/// <summary>Serializable connection settings for one named MCP server. Strings can reference ${VAR} or ${env:VAR}.</summary>
public sealed record McpServerOptions
{
    /// <summary>The executable for a stdio server.</summary>
    public string? Command { get; init; }
    /// <summary>Arguments passed separately to the stdio executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Environment overrides added to the official MCP default environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>The stdio working directory, relative to the configuration base directory when not absolute.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>The HTTP or SSE endpoint.</summary>
    public string? Url { get; init; }
    /// <summary>HTTP request headers. Authentication headers are explicit caller configuration.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>stdio, streamable-http, or sse. Null infers stdio from Command or streamable-http from Url.</summary>
    public string? Transport { get; init; }
    /// <summary>The tool prefix. Null uses the sanitized server name; an empty string disables prefixing.</summary>
    public string? Prefix { get; init; }
    /// <summary>Patterns matched at the beginning of the unprefixed server tool name.</summary>
    public McpToolFilters? ToolFilters { get; init; }
    /// <summary>Whether the direct loader skips this server before resolving its settings.</summary>
    public bool Disabled { get; init; }
    /// <summary>Whether to skip configuration/connection failures. Null inherits the configuration default.</summary>
    public bool? ContinueOnError { get; init; }
    /// <summary>Experimental task-augmented tool execution. Null preserves ordinary MCP tool calls.</summary>
    public McpTasksConfig? TasksConfig { get; init; }
    /// <summary>Pre-registered client credentials for streamable HTTP. No interactive authorization flow is substituted.</summary>
    public McpClientCredentials? Auth { get; init; }
    /// <summary>Explicit native caching and byte limits for client credentials; null uses reactive token renewal.</summary>
    public McpClientCredentialsHandlerOptions? AuthOptions { get; init; }
    /// <summary>Explicit application identity; null retains the native server-name default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationName { get; init; }
    /// <summary>Explicit application version; null retains the native 1.0.0 default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationVersion { get; init; }
    /// <summary>Optional finite positive startup budget. Null preserves the native initialization default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? StartupTimeout { get; init; }
}

/// <summary>Serializable MCP tool selection. Rejection takes precedence over allowance.</summary>
public sealed record McpToolFilters
{
    /// <summary>Allowed patterns; null permits all tools, while an empty list permits none.</summary>
    public IReadOnlyList<string>? Allowed { get; init; }
    /// <summary>Patterns excluded even when also allowed.</summary>
    public IReadOnlyList<string>? Rejected { get; init; }
}

/// <summary>An owned declarative server map or configuration-file reference, resolved before connecting servers.</summary>
public sealed partial class McpServerConfiguration
{
    private readonly JsonElement _source;
    private readonly bool _portable;

    private McpServerConfiguration(JsonElement source, string? baseDirectory, bool continueOnError, bool portable)
    {
        _source = source.Clone();
        BaseDirectory = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
        ContinueOnError = continueOnError;
        _portable = portable;
    }

    /// <summary>The absolute directory used for configuration paths.</summary>
    public string BaseDirectory { get; }
    /// <summary>The default per-server failure policy; harness construction defaults to continuing.</summary>
    public bool ContinueOnError { get; }

    /// <summary>Parses a flat or mcpServers-wrapped JSON map using direct loader semantics.</summary>
    public static McpServerConfiguration Parse(string json, string? baseDirectory = null, bool continueOnError = true)
    {
        Guard.NotNull(json, nameof(json));
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement, baseDirectory, continueOnError, portable: false);
    }

    /// <summary>References a JSON file, read through the selected sandbox during harness construction.</summary>
    public static McpServerConfiguration FromFile(string path, string? baseDirectory = null, bool continueOnError = true)
    {
        Guard.NotBlank(path, nameof(path));
        return new(JsonSerializer.SerializeToElement(path), baseDirectory, continueOnError, portable: false);
    }

    /// <summary>Copies a typed server map using direct loader semantics.</summary>
    public static McpServerConfiguration FromServers(IReadOnlyDictionary<string, McpServerOptions> servers, string? baseDirectory = null, bool continueOnError = true)
    {
        Guard.NotNull(servers, nameof(servers));
        var values = servers.ToDictionary(pair => pair.Key, pair => (object?)new
        {
            command = pair.Value.Command,
            args = pair.Value.Arguments,
            env = pair.Value.Environment,
            cwd = pair.Value.WorkingDirectory,
            url = pair.Value.Url,
            headers = pair.Value.Headers,
            transport = pair.Value.Transport,
            prefix = pair.Value.Prefix,
            toolFilters = pair.Value.ToolFilters is { } filter ? new { allowed = filter.Allowed, rejected = filter.Rejected } : null,
            disabled = pair.Value.Disabled,
            continueOnError = pair.Value.ContinueOnError,
            applicationName = pair.Value.ApplicationName,
            applicationVersion = pair.Value.ApplicationVersion,
            startupTimeoutMs = pair.Value.StartupTimeout is { } timeout ? timeout.Ticks / (decimal)TimeSpan.TicksPerMillisecond : (decimal?)null,
            tasksConfig = SerializeTasks(pair.Value.TasksConfig),
            auth = pair.Value.Auth is { } credentials ? new { clientId = credentials.ClientId, clientSecret = credentials.ClientSecret, scopes = credentials.Scopes } : null,
            authOptions = pair.Value.AuthOptions is { } authOptions ? new
            {
                refreshOnExpiry = authOptions.RefreshOnExpiry,
                maximumRequestBodyBytes = authOptions.MaximumRequestBodyBytes,
                maximumMetadataBytes = authOptions.MaximumMetadataBytes,
                maximumTokenBytes = authOptions.MaximumTokenBytes,
            } : null,
        }, StringComparer.Ordinal);
        return new(JsonSerializer.SerializeToElement(values), baseDirectory, continueOnError, portable: false);
    }

    internal static McpServerConfiguration FromPortable(JsonElement value, string baseDirectory) => new(value, baseDirectory, continueOnError: true, portable: true);

    internal async Task<IReadOnlyList<ResolvedMcpServer>> ResolveAsync(ISandbox sandbox, CancellationToken cancellationToken)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        cancellationToken.ThrowIfCancellationRequested();
        var source = _source;
        if (source.ValueKind == JsonValueKind.String)
        {
            var path = ResolvePath(source.GetString()!);
            var text = await sandbox.ReadTextAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("MCP configuration file was not found.", path);
            using var document = JsonDocument.Parse(text);
            source = document.RootElement.Clone();
        }
        ConfigurationSchema.Object(source, "mcpServers");
        if (source.TryGetProperty("mcpServers", out var wrapped)) source = wrapped;
        ConfigurationSchema.Object(source, "mcpServers");
        if (_portable) source = Expand(ResolvePortableDirectories(source));
        var entries = source.EnumerateObject().ToArray();
        foreach (var entry in entries)
        {
            Guard.NotBlank(entry.Name, "server name");
            ConfigurationSchema.Object(entry.Value, "mcpServers." + entry.Name);
        }
        var resolved = new List<ResolvedMcpServer>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Boolean(entry.Value, "disabled") == true) continue;
            var policy = Boolean(entry.Value, "continueOnError") ?? ContinueOnError;
            try { resolved.Add(ResolveServer(entry.Name, Expand(entry.Value), policy)); }
            catch (Exception exception) when (exception is not OperationCanceledException && policy)
            { HarnessLogging.Warning(exception, "server=<{0}>, error_type=<{1}> | MCP server config failed, skipping", entry.Name, exception.GetType().Name); }
        }
        return Array.AsReadOnly(resolved.ToArray());
    }

    private ResolvedMcpServer ResolveServer(string name, JsonElement value, bool policy)
    {
        var credentials = value.TryGetProperty("auth", out var auth) && auth.ValueKind != JsonValueKind.Null ? ReadAuth(auth) : null;
        var authOptions = value.TryGetProperty("authOptions", out var authentication) && authentication.ValueKind != JsonValueKind.Null ? ReadAuthOptions(authentication) : null;
        if (authOptions is not null && credentials is null) throw new ArgumentException("MCP authOptions requires client-credentials auth.");
        var tasks = value.TryGetProperty("tasksConfig", out var taskValue) && taskValue.ValueKind != JsonValueKind.Null ? ReadTasks(taskValue) : null;
        var command = Text(value, "command");
        var url = Text(value, "url");
        var transport = Text(value, "transport");
        if (!string.IsNullOrEmpty(command) && !string.IsNullOrEmpty(url) && transport is null) throw new ArgumentException("MCP server '" + name + "' has both command and url; set transport explicitly.");
        transport ??= !string.IsNullOrEmpty(command) ? "stdio" : !string.IsNullOrEmpty(url) ? "streamable-http" : null;
        if (transport is not ("stdio" or "streamable-http" or "sse")) throw new ArgumentException("MCP server '" + name + "' requires command or url and a supported transport.");
        if (credentials is not null && transport != "streamable-http") throw new NotSupportedException("MCP client-credentials auth requires streamable-http transport; interactive and SSE flows are not substituted.");
        if (transport == "stdio") Guard.NotBlank(command, "command");
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) throw new ArgumentException("MCP HTTP transports require an absolute http(s) url.");
        var prefix = Text(value, "prefix") ?? new string(name.Select(character => Guard.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        var cwd = Text(value, "cwd");
        var filters = value.TryGetProperty("toolFilters", out var filterValue) && filterValue.ValueKind != JsonValueKind.Null ? ReadFilters(filterValue) : null;
        return new()
        {
            Name = name,
            ContinueOnError = policy,
            Transport = transport,
            Options = new()
            {
                Command = command is null ? null : ExpandHome(command),
                Arguments = Strings(value, "args") ?? [],
                Environment = Record(value, "env"),
                WorkingDirectory = cwd is null ? null : ResolvePath(cwd),
                Url = url,
                Headers = Record(value, "headers"),
                Transport = transport,
                Prefix = prefix,
                ToolFilters = filters,
                ContinueOnError = policy,
                ApplicationName = Text(value, "applicationName"),
                ApplicationVersion = Text(value, "applicationVersion"),
                StartupTimeout = ReadStartupTimeout(value),
                TasksConfig = tasks,
                Auth = credentials,
                AuthOptions = authOptions,
            },
            Allowed = Compile(filters?.Allowed),
            Rejected = Compile(filters?.Rejected),
        };
    }

    private static TimeSpan? ReadStartupTimeout(JsonElement value)
    {
        if (!value.TryGetProperty("startupTimeoutMs", out var duration) || duration.ValueKind == JsonValueKind.Null) return null;
        if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDecimal(out var milliseconds) ||
            milliseconds <= 0 || milliseconds > int.MaxValue)
            throw new ArgumentException("MCP startupTimeoutMs must be positive milliseconds no greater than Int32.MaxValue.");
        var ticks = milliseconds * TimeSpan.TicksPerMillisecond;
        if (ticks < 1 || ticks != decimal.Truncate(ticks))
            throw new ArgumentException("MCP startupTimeoutMs must be representable as a positive whole TimeSpan tick count.");
        return ValidateStartupTimeout(TimeSpan.FromTicks((long)ticks));
    }

    internal static TimeSpan ValidateStartupTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.Ticks > (long)int.MaxValue * TimeSpan.TicksPerMillisecond)
            throw new ArgumentOutOfRangeException(nameof(McpServerOptions.StartupTimeout), "MCP startup timeout must be positive and no greater than Int32.MaxValue milliseconds.");
        return timeout;
    }

    private static McpClientCredentials ReadAuth(JsonElement value)
    {
        ConfigurationSchema.Object(value, "mcpServers.auth");
        foreach (var field in value.EnumerateObject())
            if (field.Name is not ("clientId" or "client_id" or "clientSecret" or "client_secret" or "scopes"))
                try { HarnessLogging.Warning("MCP client-credentials auth ignores unknown field: {0}", field.Name); } catch (Exception) { }
        return new()
        {
            ClientId = AuthField(value, "clientId", "client_id"),
            ClientSecret = AuthField(value, "clientSecret", "client_secret"),
            Scopes = Strings(value, "scopes") ?? [],
        };
    }

    private static string AuthField(JsonElement value, string camel, string snake)
    {
        if (value.TryGetProperty(camel, out _) && value.TryGetProperty(snake, out _)) throw new ArgumentException("MCP auth cannot combine aliases " + camel + " and " + snake + ".");
        return Text(value, value.TryGetProperty(camel, out _) ? camel : snake) ?? throw new ArgumentException("MCP auth requires string field " + camel + ".");
    }

    private static McpClientCredentialsHandlerOptions ReadAuthOptions(JsonElement value)
    {
        ConfigurationSchema.Object(value, "mcpServers.authOptions", ["refreshOnExpiry", "maximumRequestBodyBytes", "maximumMetadataBytes", "maximumTokenBytes"]);
        var defaults = new McpClientCredentialsHandlerOptions();
        return new()
        {
            RefreshOnExpiry = Boolean(value, "refreshOnExpiry") ?? false,
            MaximumRequestBodyBytes = AuthLimit(value, "maximumRequestBodyBytes", defaults.MaximumRequestBodyBytes),
            MaximumMetadataBytes = AuthLimit(value, "maximumMetadataBytes", defaults.MaximumMetadataBytes),
            MaximumTokenBytes = AuthLimit(value, "maximumTokenBytes", defaults.MaximumTokenBytes),
        };
    }

    private static int AuthLimit(JsonElement value, string name, int fallback)
    {
        if (!value.TryGetProperty(name, out var field)) return fallback;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var limit) || limit <= 0) throw new ArgumentException("MCP authOptions." + name + " must be a positive Int32 byte count.");
        return limit;
    }

    private static object? SerializeTasks(McpTasksConfig? config)
    {
        config = Strands.Agents.Mcp.McpClient.ValidateTasksConfig(config);
        return config is null ? null : new
        {
            pollTimeout = config.PollTimeout.TotalMilliseconds,
            requestTimeout = config.RequestTimeout.TotalMilliseconds,
            pollInterval = config.PollInterval.TotalMilliseconds,
            protocolProfile = config.ProtocolProfile switch
            {
                McpTasksProtocolProfile.Negotiated => "negotiated",
                McpTasksProtocolProfile.Legacy20251125 => "legacy2025-11-25",
                _ => "finalized2026-07-28"
            },
            legacyTtl = config.LegacyTimeToLive?.Ticks / TimeSpan.TicksPerMillisecond
        };
    }

    private static McpTasksConfig ReadTasks(JsonElement value)
    {
        ConfigurationSchema.Object(value, "mcpServers.tasksConfig");
        var defaults = new McpTasksConfig();
        var config = new McpTasksConfig
        {
            PollTimeout = TaskDuration(value, "pollTimeout", defaults.PollTimeout),
            RequestTimeout = TaskDuration(value, value.TryGetProperty("requestTimeout", out _) ? "requestTimeout" : "ttl", defaults.RequestTimeout),
            PollInterval = TaskDuration(value, "pollInterval", defaults.PollInterval),
            ProtocolProfile = !value.TryGetProperty("protocolProfile", out var profile) ? defaults.ProtocolProfile : profile.ValueKind == JsonValueKind.String ? profile.GetString() switch
            {
                "negotiated" => McpTasksProtocolProfile.Negotiated,
                "finalized2026-07-28" => McpTasksProtocolProfile.Finalized20260728,
                "legacy2025-11-25" => McpTasksProtocolProfile.Legacy20251125,
                _ => throw new ArgumentException("Unsupported MCP tasksConfig.protocolProfile.")
            } : throw new ArgumentException("MCP tasksConfig.protocolProfile must be a string."),
            LegacyTimeToLive = ReadLegacyTtl(value)
        };
        return Strands.Agents.Mcp.McpClient.ValidateTasksConfig(config)!;
    }

    private static TimeSpan? ReadLegacyTtl(JsonElement value)
    {
        if (!value.TryGetProperty("legacyTtl", out var ttl) || ttl.ValueKind == JsonValueKind.Null) return null;
        if (ttl.ValueKind != JsonValueKind.Number || !ttl.TryGetInt64(out var milliseconds) || milliseconds <= 0 || milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            throw new ArgumentException("MCP tasksConfig.legacyTtl must be positive whole milliseconds representable as a TimeSpan.");
        return TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }

    private static TimeSpan TaskDuration(JsonElement value, string name, TimeSpan fallback)
    {
        if (!value.TryGetProperty(name, out var duration)) return fallback;
        if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var milliseconds) || double.IsNaN(milliseconds) ||
            double.IsInfinity(milliseconds) || milliseconds <= 0 || milliseconds > int.MaxValue)
            throw new ArgumentException("MCP tasksConfig." + name + " must be positive milliseconds no greater than Int32.MaxValue.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private JsonElement ResolvePortableDirectories(JsonElement source)
    {
        var servers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in source.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            {
                var fields = value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
                var cwd = Text(value, "cwd");
                fields["cwd"] = JsonSerializer.SerializeToElement(cwd is null ? BaseDirectory : Path.IsPathRooted(ExpandHome(cwd)) ? ExpandHome(cwd) : Path.Combine(BaseDirectory, ExpandHome(cwd)));
                value = JsonSerializer.SerializeToElement(fields);
            }
            servers.Add(entry.Name, value.Clone());
        }
        return JsonSerializer.SerializeToElement(servers);
    }

    private string ResolvePath(string value)
    {
        value = ExpandHome(value);
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(BaseDirectory, value));
    }
    private static string ExpandHome(string value) => value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)
        ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), value.Length == 1 ? string.Empty : value.Substring(2)) : value;
    private static string? Text(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var text) || text.ValueKind == JsonValueKind.Null) return null;
        if (text.ValueKind != JsonValueKind.String) throw new ArgumentException("MCP " + field + " must be a string.");
        return text.GetString();
    }
    private static bool? Boolean(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var boolean) || boolean.ValueKind == JsonValueKind.Null) return null;
        if (boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("MCP " + field + " must be a boolean.");
        return boolean.GetBoolean();
    }
    private static IReadOnlyList<string>? Strings(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var array) || array.ValueKind == JsonValueKind.Null) return null;
        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw new ArgumentException("MCP " + field + " must be a string array.");
        return Array.AsReadOnly(array.EnumerateArray().Select(item => item.GetString()!).ToArray());
    }
    private static IReadOnlyDictionary<string, string> Record(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var record) || record.ValueKind == JsonValueKind.Null) return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        ConfigurationSchema.Object(record, "mcpServers." + field);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in record.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("MCP " + field + " values must be strings.");
            values.Add(property.Name, property.Value.GetString()!);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values);
    }
    private static McpToolFilters ReadFilters(JsonElement value)
    {
        ConfigurationSchema.Object(value, "mcpServers.toolFilters");
        return new() { Allowed = Strings(value, "allowed"), Rejected = Strings(value, "rejected") };
    }
    private static Regex[]? Compile(IReadOnlyList<string>? patterns) => patterns?.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).ToArray();
    private static JsonElement Expand(JsonElement value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteExpanded(writer, value);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        return document.RootElement.Clone();
    }
    private static void WriteExpanded(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(Regex.Replace(value.GetString()!, @"\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)\}", match => System.Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? throw new InvalidOperationException("Environment variable '" + match.Groups[1].Value + "' is not set."), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject()) { writer.WritePropertyName(property.Name); WriteExpanded(writer, property.Value); }
            writer.WriteEndObject(); return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteExpanded(writer, item); writer.WriteEndArray(); return;
        }
        value.WriteTo(writer);
    }
}

internal sealed record ResolvedMcpServer
{
    internal required string Name { get; init; }
    internal required McpServerOptions Options { get; init; }
    internal required string Transport { get; init; }
    internal required bool ContinueOnError { get; init; }
    internal Regex[]? Allowed { get; init; }
    internal Regex[]? Rejected { get; init; }
    internal bool Includes(string name) => (Allowed is null || Allowed.Any(pattern => AtStart(pattern, name))) && !(Rejected?.Any(pattern => AtStart(pattern, name)) ?? false);
    private static bool AtStart(Regex pattern, string name) => pattern.Match(name) is { Success: true, Index: 0 };
}
