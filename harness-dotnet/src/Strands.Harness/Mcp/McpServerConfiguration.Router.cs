using System.Text.Json;
using Strands.Agents.Mcp;

namespace Strands.Harness.Mcp;

public sealed partial class McpServerConfiguration
{
    /// <summary>Creates an allowlisted router from a selected-sandbox configuration snapshot without connecting any server.</summary>
    public async Task<McpRouter> CreateRouterAsync(ISandbox sandbox, int maxConnections = 10, string name = "mcp_router", string? description = null,
        McpRouterErrorPolicy errorPolicy = McpRouterErrorPolicy.Throw, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        if (maxConnections < 1) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (!Enum.IsDefined(typeof(McpRouterErrorPolicy), errorPolicy)) throw new ArgumentException("Unsupported router error policy.", nameof(errorPolicy));
        _ = new Agents.ToolSpec(name, description ?? "MCP router", JsonSerializer.SerializeToElement(new { type = "object" }));
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
        var snapshot = new McpServerConfiguration(source, BaseDirectory, ContinueOnError, _portable);
        var resolved = (await snapshot.ResolveAsync(sandbox, cancellationToken).ConfigureAwait(false)).ToDictionary(server => server.Name, StringComparer.Ordinal);
        var factories = new Dictionary<string, Func<CancellationToken, Task<Agents.Mcp.McpClient>>>(StringComparer.Ordinal);
        foreach (var entry in source.EnumerateObject())
        {
            var serverName = entry.Name;
            factories.Add(serverName, token => resolved.TryGetValue(serverName, out var server)
                ? McpConnections.ConnectServerAsync(server, token)
                : Task.FromException<Agents.Mcp.McpClient>(new McpRouterToolException("Server '" + serverName + "' failed to initialise; check the server configuration.")));
        }
        return new(new()
        {
            Servers = factories,
            Name = name,
            Description = description,
            MaxConnections = maxConnections,
            ErrorPolicy = errorPolicy,
            ToolFilter = (server, tool) => resolved.TryGetValue(server, out var selected) && selected.Includes(tool)
        });
    }
}
