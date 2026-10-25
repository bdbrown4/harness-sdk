using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Mcp;

/// <summary>Connects, lists and calls tools on an application-defined server allowlist, with independent connections for each agent.</summary>
public sealed partial class McpRouter : IRichTool, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<CallScope?> Active = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<CancellationToken, Task<McpClient>>> _servers;
    private readonly McpRouterOptions _options;
    private readonly ConditionalWeakTable<Agent, AgentConnections> _agents = new();
    private readonly ConditionalWeakTable<McpClient, object> _usedClients = new();
    private readonly List<WeakReference<AgentConnections>> _states = [];
    private Task? _disposal;

    /// <summary>Creates a router without connecting any server and copies its named factory allowlist.</summary>
    public McpRouter(McpRouterOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Servers, nameof(options.Servers));
        if (options.Servers.Count == 0) throw new ArgumentException("The MCP router requires at least one allowlisted server.", nameof(options));
        if (options.MaxConnections < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConnections must be positive.");
        if (!Enum.IsDefined(typeof(McpRouterErrorPolicy), options.ErrorPolicy)) throw new ArgumentException("Unsupported router error policy.", nameof(options));
        _servers = new(StringComparer.Ordinal);
        foreach (var pair in options.Servers)
        {
            Guard.NotNullOrWhiteSpace(pair.Key, "server name"); Guard.NotNull(pair.Value, "server factory");
            _servers.Add(pair.Key, pair.Value);
        }
        _options = options with { Servers = _servers };
        Spec = new(options.Name, options.Description ?? "Connects to Model Context Protocol (MCP) servers at runtime to discover and invoke their tools. " +
            "'connect' opens a connection to a permitted server. 'list_connections' returns all currently open connection IDs. " +
            "'list_tools' returns the tools the connected server exposes, including their names and input schemas. " +
            "'call_tool' invokes a named tool on a connected server and returns its result. 'disconnect' closes a connection. " +
            "Multiple servers can be connected simultaneously. Use connection_id to identify which connection to use for list_tools, call_tool, and disconnect. " +
            "Permitted server names: " + string.Join(", ", _servers.Keys.OrderBy(value => value, StringComparer.Ordinal).Select(value => "'" + value + "'")) + ".",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                required = new[] { "command" },
                properties = new Dictionary<string, object>
                {
                    ["command"] = new { type = "string", @enum = new[] { "connect", "list_connections", "list_tools", "call_tool", "disconnect" } },
                    ["connection_id"] = new { type = "string" },
                    ["server_name"] = new { type = "string" },
                    ["tool_name"] = new { type = "string" },
                    ["arguments"] = new { type = "object" }
                }
            }));
    }
    /// <inheritdoc/>
    public ToolSpec Spec { get; }
    /// <inheritdoc/>
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var result = await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false);
        if (result.ContentBlocks.Any(block => block is not TextBlock and not JsonBlock)) throw new NotSupportedException("Use InvokeResultAsync to retain rich MCP router content.");
        return result.ContentBlocks.Count == 0 ? result.Content : string.Concat(result.ContentBlocks.Select(block => block is JsonBlock json ? json.Json.GetRawText() : ((TextBlock)block).Text));
    }
    /// <inheritdoc/>
    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Router input must be an object.", nameof(input));
        var command = Required(input, "command");
        if (command is not ("connect" or "list_connections" or "list_tools" or "call_tool" or "disconnect")) throw new McpRouterToolException("Unknown command: " + command);
        var id = command == "list_connections" ? null : Required(input, "connection_id");
        var serverName = command == "connect" ? Required(input, "server_name") : null;
        var toolName = command == "call_tool" ? Required(input, "tool_name") : null;
        var arguments = input.TryGetProperty("arguments", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
        if (arguments.ValueKind != JsonValueKind.Object) throw new McpRouterToolException("arguments must be an object.");
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState(context.Agent);
        using var operation = state.Enter();
        using var scope = new CallScope(this, state);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.ShutdownToken);
        try
        {
            if (command == "list_connections") return new(context.ToolUse.ToolUseId, state.List());
            if (command == "connect") return new(context.ToolUse.ToolUseId, await ConnectAsync(state, serverName!, id!, linked.Token).ConfigureAwait(false));
            if (command == "disconnect")
            {
                var removed = state.Remove(id!);
                var closing = removed.CloseAsync();
                try { await TaskCancellation.WaitAsync(closing, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (linked.IsCancellationRequested)
                {
                    var canceled = new RouterCanceledException(error, linked.Token);
                    canceled.Data["Strands.CleanupTask"] = ObserveCloseAsync(closing);
                    throw canceled;
                }
                catch { }
                state.CompleteDisconnect(removed);
                return new(context.ToolUse.ToolUseId, "Successfully disconnected");
            }
            using var lease = state.Acquire(id!);
            if (command == "list_tools")
            {
                var tools = await lease.Connection.Client.ListRouterToolSpecsAsync(name => _options.ToolFilter?.Invoke(lease.Connection.ServerName, name) ?? true, linked.Token).ConfigureAwait(false);
                return new(context.ToolUse.ToolUseId, [new JsonBlock(JsonSerializer.SerializeToElement(tools, Json))]);
            }
            ToolResultBlock result;
            try
            {
                result = McpTool.MapResult(await lease.Connection.Client.CallToolAsync(toolName!, arguments, cancellationToken: linked.Token).ConfigureAwait(false), context.ToolUse.ToolUseId);
            }
            catch (ModelContextProtocol.UrlElicitationRequiredException error) when (McpTool.CanFormatUrlElicitation(error))
            {
                result = McpTool.FormatUrlElicitation(error, context.ToolUse.ToolUseId);
            }
            if (result.Status == ToolResultStatus.Error && _options.ErrorPolicy == McpRouterErrorPolicy.Throw) throw new McpRouterToolException(string.IsNullOrEmpty(result.Content) ? "MCP tool returned an error" : result.Content, result.Error);
            return result;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
        {
            var canceled = new RouterCanceledException(error, cancellationToken);
            foreach (System.Collections.DictionaryEntry entry in error.Data) canceled.Data[entry.Key] = entry.Value;
            throw canceled;
        }
    }

    private AgentConnections GetState(Agent agent)
    {
        Guard.NotNull(agent, nameof(agent));
        lock (_gate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(McpRouter));
            if (_agents.TryGetValue(agent, out var existing)) return existing;
            var state = new AgentConnections(this, _options.MaxConnections);
            state.Subscription = agent.RegisterOwnedShutdownCallback((_, _) =>
            {
                RefuseSelfWait(state);
                return new ValueTask(state.CloseAsync());
            });
            _agents.Add(agent, state); _states.Add(new(state)); return state;
        }
    }
    private async Task<string> ConnectAsync(AgentConnections state, string serverName, string id, CancellationToken token)
    {
        if (!_servers.TryGetValue(serverName, out var connect)) throw new McpRouterToolException("Server '" + serverName + "' is not on the MCP server allowlist: " + string.Join(", ", _servers.Keys.OrderBy(value => value, StringComparer.Ordinal)));
        await state.AcquireConnectSlotAsync(token).ConfigureAwait(false);
        McpClient? candidate = null;
        var deferred = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var connecting = connect(token) ?? throw new McpRouterToolException("The server factory returned no connection task.");
            try
            {
                await TaskCancellation.WaitAsync(connecting, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                candidate = await connecting.ConfigureAwait(false) ?? throw new McpRouterToolException("The server factory returned no connection.");
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            {
                deferred = true;
                var canceled = new RouterCanceledException(error, token);
                var cleanup = CleanupLateConnectionAsync(connecting, state);
                canceled.Data["Strands.CleanupTask"] = cleanup;
                if (cleanup.IsCompleted)
                {
                    var errors = await cleanup.ConfigureAwait(false);
                    if (errors.Count > 0) canceled.Data["Strands.CleanupErrors"] = errors.ToList();
                }
                throw canceled;
            }
            lock (_gate)
            {
                if (_usedClients.TryGetValue(candidate, out _)) { candidate = null; throw new McpRouterToolException("The server factory returned a previously transferred client; each connection requires a fresh owned client."); }
                _usedClients.Add(candidate, new());
            }
            token.ThrowIfCancellationRequested();
            if (!candidate.IsConnected) throw new McpRouterToolException("Server '" + serverName + "' failed to connect; check the server configuration.");
            state.Publish(id, new(this, state, serverName, candidate)); candidate = null;
            return "Successfully connected to " + serverName + " as " + JsonSerializer.Serialize(id);
        }
        catch (Exception error)
        {
            if (candidate is not null) try { await candidate.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            throw;
        }
        finally { if (!deferred) state.ReleaseConnectSlot(); }
    }
    private async Task<IReadOnlyList<Exception>> CleanupLateConnectionAsync(Task<McpClient> connecting, AgentConnections state)
    {
        using var scope = new CallScope(this, state);
        try
        {
            var client = await connecting.ConfigureAwait(false);
            if (client is null) return Array.Empty<Exception>();
            lock (_gate)
            {
                if (_usedClients.TryGetValue(client, out _)) return Array.AsReadOnly(new Exception[] { new McpRouterToolException("The canceled factory returned a previously transferred client.") });
                _usedClients.Add(client, new());
            }
            await client.DisposeAsync().ConfigureAwait(false);
            return Array.Empty<Exception>();
        }
        catch (OperationCanceledException) { return Array.Empty<Exception>(); }
        catch (Exception error) { return Array.AsReadOnly(new[] { error }); }
        finally { state.ReleaseConnectSlot(); }
    }
    private sealed class RouterCanceledException(OperationCanceledException cause, CancellationToken token)
        : OperationCanceledException("MCP router command was cancelled.", cause, token)
    {
        public override System.Collections.IDictionary Data { get; } = new System.Collections.Hashtable();
    }
    private static async Task<IReadOnlyList<Exception>> ObserveCloseAsync(Task closing)
    {
        try { await closing.ConfigureAwait(false); return Array.Empty<Exception>(); }
        catch (Exception error) { return Array.AsReadOnly(new[] { error }); }
    }
    private static string Required(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())) throw new McpRouterToolException("`" + name + "` is required.");
        return value.GetString()!;
    }
    private static void AttachCleanup(Exception error, Exception cleanup)
    {
        var errors = error.Data["Strands.CleanupErrors"] is IEnumerable<Exception> prior ? prior.ToList() : [];
        errors.Add(cleanup); error.Data["Strands.CleanupErrors"] = errors;
    }
    /// <summary>Closes every live per-agent connection set once, attempting every cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        RefuseSelfWait(null);
        TaskCompletionSource<bool> completion;
        AgentConnections[] states;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task;
            states = _states.Select(reference => reference.TryGetTarget(out var state) ? state : null).OfType<AgentConnections>().ToArray();
        }
        _ = DisposeCoreAsync(states, completion); return new(completion.Task);
    }
    private void RefuseSelfWait(AgentConnections? state)
    {
        for (var scope = Active.Value; scope is not null; scope = scope.Parent)
            if (scope.IsActive && ReferenceEquals(scope.Router, this) && (state is null || ReferenceEquals(scope.State, state)))
                throw new InvalidOperationException("An active router command or cleanup callback cannot await its own shutdown or disposal.");
    }
    private static async Task DisposeCoreAsync(AgentConnections[] states, TaskCompletionSource<bool> completion)
    {
        Exception? failure = null;
        foreach (var state in states) try { await state.CloseAsync().ConfigureAwait(false); } catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
        if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
    }
}
