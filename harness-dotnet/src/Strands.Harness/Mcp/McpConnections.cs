using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Harness.Mcp;

internal static class McpConnections
{
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<ITool>>> ConnectAsync(McpServerConfiguration? configuration, ISandbox sandbox, ICollection<IAsyncDisposable> acquired, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResolvedMcpServer> servers = configuration is null ? [] : await configuration.ResolveAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var groups = new Dictionary<string, IReadOnlyList<ITool>>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Client client;
            try
            {
                client = await ConnectServerAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (exception is OperationCanceledException cancelled && cancelled.CancellationToken == cancellationToken) throw;
                    throw new OperationCanceledException("MCP server connection was cancelled.", exception, cancellationToken);
                }
                if (!server.ContinueOnError) throw;
                HarnessLogging.Warning(exception, "server=<{0}>, error_type=<{1}> | MCP server failed to connect, skipping", server.Name, exception.GetType().Name);
                continue;
            }
            acquired.Add(client);
            var tools = await client.ListToolsAsync(new() { Prefix = server.Options.Prefix, ToolFilter = server.Includes, ContinueOnError = server.ContinueOnError }, cancellationToken).ConfigureAwait(false);
            groups.Add(server.Name, Array.AsReadOnly(tools.Cast<ITool>().ToArray()));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<ITool>>(groups);
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(cleanup);
    }

    internal static async Task<Client> ConnectProviderServerAsync(ResolvedMcpServer server, CancellationToken cancellationToken)
    {
        try { return await ConnectServerAsync(server, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (server.ContinueOnError && error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            HarnessLogging.Warning(error, "server=<{0}>, error_type=<{1}> | MCP server failed to connect, skipping", server.Name, error.GetType().Name);
            throw;
        }
    }

    internal static async Task<Client> ConnectServerAsync(ResolvedMcpServer server, CancellationToken cancellationToken,
        Func<McpClientOptions, CancellationToken, Task<Client>>? connector = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientOptions = new McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = server.Options.ApplicationName ?? server.Name,
                Version = server.Options.ApplicationVersion ?? "1.0.0"
            }
        };
        if (server.Options.StartupTimeout is not { } timeout)
            return await ConnectServerCoreAsync(server, clientOptions, cancellationToken, connector).ConfigureAwait(false);

        clientOptions.InitializationTimeout = McpServerConfiguration.ValidateStartupTimeout(timeout);
        using var deadline = new CancellationTokenSource();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        deadline.CancelAfter(timeout);
        Client? candidate = null;
        try
        {
            candidate = await ConnectServerCoreAsync(server, clientOptions, operation.Token, connector).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("MCP server startup exceeded its configured deadline.");
            return candidate;
        }
        catch (Exception error)
        {
            Exception? cleanupFailure = null;
            if (candidate is not null)
                try { await candidate.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }

            Exception primary = error;
            if (cancellationToken.IsCancellationRequested && (error is not OperationCanceledException cancelled || cancelled.CancellationToken != cancellationToken))
                primary = new OperationCanceledException("MCP server startup was cancelled.", error, cancellationToken);
            else if (deadline.IsCancellationRequested && error is OperationCanceledException expired && expired.CancellationToken == operation.Token)
                primary = new TimeoutException("MCP server startup exceeded its configured deadline.", error);
            if (!ReferenceEquals(primary, error))
                foreach (System.Collections.DictionaryEntry entry in error.Data) primary.Data[entry.Key] = entry.Value;
            if (cleanupFailure is not null) AttachCleanup(primary, cleanupFailure);
            ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }

    private static async Task<Client> ConnectServerCoreAsync(ResolvedMcpServer server, McpClientOptions clientOptions,
        CancellationToken cancellationToken, Func<McpClientOptions, CancellationToken, Task<Client>>? connector)
    {
        if (connector is not null) return await connector(clientOptions, cancellationToken).ConfigureAwait(false);
        var settings = new Strands.Agents.Mcp.McpClientSettings { TasksConfig = server.Options.TasksConfig, OwnsTransport = true };
        if (server.Transport == "stdio")
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in server.Options.Environment) environment[pair.Key] = pair.Value;
            return await Client.CreateStdioAsync(settings, new()
            {
                Command = server.Options.Command!,
                Arguments = server.Options.Arguments.ToArray(),
                EnvironmentVariables = environment,
                InheritEnvironmentVariables = false,
                WorkingDirectory = server.Options.WorkingDirectory,
            }, clientOptions, cancellationToken).ConfigureAwait(false);
        }
        var http = server.Options.Auth is { } credentials
            ? new HttpClient(new Strands.Agents.Mcp.McpClientCredentialsHandler(new Uri(server.Options.Url!, UriKind.Absolute), credentials, server.Options.AuthOptions))
            : new HttpClient();
        IClientTransport? transport = null;
        try
        {
            transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(server.Options.Url!, UriKind.Absolute),
                TransportMode = server.Transport == "sse" ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                AdditionalHeaders = server.Options.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            }, http, ownsHttpClient: true);
            return await Client.CreateAsync(settings, transport, clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (transport is null) try { http.Dispose(); } catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            throw;
        }
    }
}
