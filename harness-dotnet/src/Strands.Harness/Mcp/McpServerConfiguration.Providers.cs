using Strands.Agents.Mcp;

namespace Strands.Harness.Mcp;

public sealed partial class McpServerConfiguration
{
    /// <summary>Resolves an ordered, detached server declaration snapshot through the selected sandbox without connecting servers.</summary>
    /// <remarks>The returned providers are borrowed by agents; each connector transfers a fresh owned client generation. Caller-owned clients and router leases are not adopted.</remarks>
    public async Task<IReadOnlyList<McpToolProvider>> CreateToolProvidersAsync(ISandbox sandbox, CancellationToken cancellationToken = default)
    {
        var servers = await ResolveAsync(sandbox, cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(servers.Select(server => new McpToolProvider(
            token => McpConnections.ConnectProviderServerAsync(server, token),
            new() { Name = server.Name, ContinueOnError = server.ContinueOnError, Discovery = new() { Prefix = server.Options.Prefix, ToolFilter = server.Includes } })).ToArray());
    }
}
