using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.SchemaImplementation;
using ProtocolClient = ModelContextProtocol.Client.McpClient;
using ProtocolTool = ModelContextProtocol.Protocol.Tool;

namespace Strands.Agents.Mcp;

/// <summary>Connects Strands tools to an MCP server using the official MCP .NET client.</summary>
/// <remarks>The connection owns its protocol session. Caller-supplied HTTP clients remain caller-owned.</remarks>
public sealed partial class McpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [ThreadStatic] private static SynchronousCleanupScope? _synchronousCleanup;
    private readonly ProtocolClient _protocol;
    private readonly IClientTransport? _ownedTransport;
    private readonly JsonElement? _defaultToolCallMetadata;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonSchema?> _outputSchemas = new(StringComparer.Ordinal);
    private Task? _disposal;

    private McpClient(ProtocolClient protocol, ToolChangeWireState toolChanges, McpTasksConfig? tasksConfig = null, LegacyTaskCapabilities? legacyCapabilities = null, IClientTransport? ownedTransport = null, JsonElement? defaultToolCallMetadata = null)
    { _protocol = protocol; _toolChangeWire = toolChanges; _tasksConfig = tasksConfig; _legacyCapabilities = legacyCapabilities; _ownedTransport = ownedTransport; _defaultToolCallMetadata = defaultToolCallMetadata; }

    /// <summary>The official protocol client, exposing resources, prompts, notification handlers, and advanced MCP operations.</summary>
    public ProtocolClient Protocol => _protocol;

    /// <summary>Whether this session remains active and disposal has not begun.</summary>
    public bool IsConnected { get { lock (_gate) return _disposal is null && !_protocol.Completion.IsCompleted; } }

    /// <summary>Connects and negotiates a protocol session over an official MCP transport.</summary>
    /// <param name="transport">The transport factory. The resulting connection is owned by this client.</param>
    /// <param name="options">Official client options, including negotiated capabilities and request handlers.</param>
    /// <param name="cancellationToken">Cancellation for connection and negotiation.</param>
    /// <returns>A connected MCP client.</returns>
    public static async Task<McpClient> CreateAsync(IClientTransport transport, McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(transport, nameof(transport));
        cancellationToken.ThrowIfCancellationRequested();
        var toolChanges = new ToolChangeWireState();
        var protocol = await ProtocolClient.CreateAsync(new ToolChangeTransport(transport, toolChanges), options, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(protocol, toolChanges);
    }

    /// <summary>Connects using Streamable HTTP, legacy SSE, or the official transport's explicit auto-detection mode.</summary>
    /// <param name="httpClient">The caller-owned HTTP client, including application authentication handlers.</param>
    /// <param name="transportOptions">The endpoint, transport mode, headers, session ownership, and HTTP transport settings.</param>
    /// <param name="clientOptions">Official client negotiation and handler settings.</param>
    /// <param name="cancellationToken">Cancellation for connection and negotiation.</param>
    /// <returns>A connected MCP client.</returns>
    public static Task<McpClient> CreateHttpAsync(HttpClient httpClient, HttpClientTransportOptions transportOptions, McpClientOptions? clientOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(httpClient, nameof(httpClient));
        Guard.NotNull(transportOptions, nameof(transportOptions));
        return CreateHttpCoreAsync(new McpClientSettings { OwnsTransport = true }, httpClient, transportOptions, clientOptions, cancellationToken);
    }

    /// <summary>Starts a server process and connects using newline-delimited MCP over standard input and output.</summary>
    /// <param name="transportOptions">The executable, argument list, environment, working directory, and shutdown settings.</param>
    /// <param name="clientOptions">Official client negotiation and handler settings.</param>
    /// <param name="cancellationToken">Cancellation for process startup and negotiation.</param>
    /// <returns>A connected MCP client whose disposal closes the session and server process.</returns>
    public static Task<McpClient> CreateStdioAsync(StdioClientTransportOptions transportOptions, McpClientOptions? clientOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(transportOptions, nameof(transportOptions));
        return CreateAsync(new McpClientSettings { OwnsTransport = true }, new OwnedStdioClientTransport(transportOptions), clientOptions, cancellationToken);
    }

    /// <summary>Discovers every tool page with bounded pagination and adapts selected tools for the agent.</summary>
    /// <param name="options">Agent-facing prefix, server-name filter, and discovery bounds.</param>
    /// <param name="cancellationToken">Cancellation for discovery.</param>
    /// <returns>The tools in server order, retaining their original server names and schemas.</returns>
    /// <exception cref="InvalidDataException">Discovery exceeds its bounds, repeats a cursor or tool name, or returns malformed definitions.</exception>
    public Task<IReadOnlyList<McpTool>> ListToolsAsync(McpToolListOptions? options = null, CancellationToken cancellationToken = default)
        => DiscoverToolsAsync(options, (definition, name, outputSchema) => new McpTool(this, definition, name, options?.Progress, outputSchema), cancellationToken);

    internal Task<IReadOnlyList<JsonElement>> ListRouterToolSpecsAsync(Func<string, bool> filter, CancellationToken cancellationToken)
        => DiscoverToolsAsync(new McpToolListOptions { Prefix = "", ToolFilter = filter }, (definition, name, _) =>
            JsonSerializer.SerializeToElement(new
            {
                name,
                description = definition.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(description.GetString())
                    ? description.GetString() : "Tool which performs " + name,
                inputSchema = definition.GetProperty("inputSchema"),
                outputSchema = definition.TryGetProperty("outputSchema", out var output) ? (JsonElement?)output : null,
                annotations = definition.TryGetProperty("annotations", out var annotations) ? (JsonElement?)annotations : null
            }), cancellationToken);

    private async Task<IReadOnlyList<T>> DiscoverToolsAsync<T>(McpToolListOptions? options, Func<JsonElement, string, JsonSchema?, T> adapt, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        options ??= new();
        if (options.MaximumPages < 1 || options.MaximumTools < 1) throw new ArgumentOutOfRangeException(nameof(options), "Discovery bounds must be positive.");
        var tools = new List<T>();
        var serverNames = new HashSet<string>(StringComparer.Ordinal);
        var agentNames = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var taskSupport = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownTools = new List<ProtocolTool>();
        var outputSchemas = new Dictionary<string, JsonSchema?>(StringComparer.Ordinal);
        var discovered = 0;
        string? cursor = null;
        for (var page = 0; page < options.MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var response = await _protocol.SendRequestAsync(new JsonRpcRequest
            {
                Id = new RequestId(Guid.NewGuid().ToString("N")),
                Method = "tools/list",
                Params = parameters
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var raw = JsonSerializer.SerializeToElement(response.Result, JsonOptions);
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("tools", out var definitions) || definitions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("MCP tools/list must return a tools array.");
            foreach (var definition in definitions.EnumerateArray())
            {
                if (++discovered > options.MaximumTools) throw new InvalidDataException("MCP tool discovery exceeded its tool-count limit.");
                ProtocolTool native;
                try { native = definition.Deserialize<ProtocolTool>(JsonOptions) ?? throw new InvalidDataException("MCP returned a null tool definition."); }
                catch (JsonException error) { throw new InvalidDataException("MCP returned a malformed tool definition: " + error.Message, error); }
                catch (ArgumentException error) { throw new InvalidDataException("MCP returned an invalid tool schema: " + error.Message, error); }
                if (!serverNames.Add(native.Name)) throw new InvalidDataException("MCP returned duplicate server tool names.");
                taskSupport[native.Name] = ReadLegacyTaskSupport(definition);
                knownTools.Add(native);
                if (options.ToolFilter is not null && !options.ToolFilter(native.Name)) continue;
                _ = ToolSchema.Compile(native.InputSchema);
                var outputSchema = native.OutputSchema is { } output ? ToolSchema.Compile(output) : null;
                var name = string.IsNullOrEmpty(options.Prefix) ? native.Name : options.Prefix + "_" + native.Name;
                if (options.ContinueOnError && !string.IsNullOrEmpty(options.Prefix) && name.Length > 64 && name.All(character => Guard.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                {
                    SdkLogging.Warning("MCP tool '{0}' has prefixed name '{1}' of length {2}, exceeding 64 characters; skipping because ContinueOnError is enabled.", native.Name, name, name.Length);
                    continue;
                }
                if (!agentNames.Add(name)) throw new InvalidDataException("MCP discovery produced duplicate agent-facing names.");
                var tool = adapt(definition, name, outputSchema);
                tools.Add(tool);
                outputSchemas[native.Name] = outputSchema;
            }
            cursor = null;
            if (raw.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null)
            {
                if (next.ValueKind != JsonValueKind.String) throw new InvalidDataException("MCP tools/list nextCursor must be a string.");
                cursor = next.GetString();
            }
            if (cursor is null)
            {
                lock (_gate)
                {
                    if (_disposal is not null) throw new ObjectDisposedException(nameof(McpClient));
                    cancellationToken.ThrowIfCancellationRequested();
                    // The official SDK validates its entire known-tool batch before adding anything.
                    // Both native factories omit its loggerFactory, so rejection uses NullLogger;
                    // no application callback runs here. Re-review this gate if protocol logging is added.
                    // Keep existing additive name semantics: unrelated accepted schemas are retained.
                    _protocol.AddKnownTools(knownTools);
                    foreach (var schema in outputSchemas) _outputSchemas[schema.Key] = schema.Value;
                    SetLegacyToolDefinitions(taskSupport);
                }
                return Array.AsReadOnly(tools.ToArray());
            }
            if (!cursors.Add(cursor)) throw new InvalidDataException("MCP tool discovery repeated a pagination cursor.");
        }
        throw new InvalidDataException("MCP tool discovery exceeded its page limit.");
    }

    /// <summary>Calls a server tool and returns its complete official MCP result without discarding rich content.</summary>
    /// <param name="serverName">The original server-side tool name, unaffected by an agent-facing prefix.</param>
    /// <param name="arguments">A JSON object of tool arguments.</param>
    /// <param name="progress">An optional progress observer invoked by the official protocol client.</param>
    /// <param name="options">Official per-request options and metadata.</param>
    /// <param name="cancellationToken">Cancellation propagated to the request and MCP cancellation notification.</param>
    /// <returns>The complete MCP result, including isError, structured content, and metadata.</returns>
    /// <exception cref="InvalidDataException">A discovered output schema does not match a successful result.</exception>
    public ValueTask<CallToolResult> CallToolAsync(string serverName, JsonElement arguments, IProgress<ProgressNotificationValue>? progress = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
        => CallToolCoreAsync(serverName, arguments, progress, options, false, null, cancellationToken);

    internal ValueTask<CallToolResult> CallBoundToolAsync(string serverName, JsonElement arguments, JsonSchema? outputSchema,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
        => CallToolCoreAsync(serverName, arguments, progress, null, true, outputSchema, cancellationToken);

    private async ValueTask<CallToolResult> CallToolCoreAsync(string serverName, JsonElement arguments,
        IProgress<ProgressNotificationValue>? progress, RequestOptions? options, bool boundSchema, JsonSchema? outputSchema, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Guard.NotNullOrWhiteSpace(serverName, nameof(serverName));
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP tool arguments must be a JSON object.", nameof(arguments));
        lock (_gate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(McpClient));
            if (!boundSchema) _outputSchemas.TryGetValue(serverName, out outputSchema);
        }
        options = SnapshotToolCallOptions(options);
        var input = arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var finalized = _tasksConfig is { ProtocolProfile: not McpTasksProtocolProfile.Legacy20251125 } && SupportsTaskExtension();
        var legacy = !finalized && _tasksConfig is { ProtocolProfile: McpTasksProtocolProfile.Legacy20251125 or McpTasksProtocolProfile.Negotiated };
        var result = finalized
            ? await CompleteTaskToolAsync(serverName, arguments, progress, options, cancellationToken).ConfigureAwait(false)
            : legacy
            ? await CompleteLegacyToolAsync(serverName, arguments, progress, options, cancellationToken).ConfigureAwait(false)
            : await _protocol.CallToolAsync(serverName, input, progress, options, cancellationToken).ConfigureAwait(false);
        if (result.IsError != true && outputSchema is not null && (result.StructuredContent is not { } structured || !outputSchema.Evaluate(structured).IsValid))
            throw new InvalidDataException("MCP tool '" + serverName + "' returned missing or invalid structured content for its output schema.");
        return result;
    }

    private RequestOptions? SnapshotToolCallOptions(RequestOptions? options)
    {
        var snapshot = options is null ? null : new RequestOptions
        {
            JsonSerializerOptions = options.JsonSerializerOptions,
            Meta = options.Meta,
            ProgressToken = options.ProgressToken
        };
        if (snapshot?.Meta is { } supplied) snapshot.Meta = (JsonObject)supplied.DeepClone();
        else if (_defaultToolCallMetadata is { } configured)
        {
            snapshot ??= new();
            snapshot.Meta = JsonNode.Parse(configured.GetRawText())!.AsObject();
        }
        return snapshot;
    }

    /// <summary>Closes the owned protocol session once; concurrent disposal calls await the same cleanup.</summary>
    /// <returns>The asynchronous session cleanup.</returns>
    public ValueTask DisposeAsync()
    {
        RefuseToolChangeCallbackReentry(observePublishedTaskShutdown: true);
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _ = CompleteDisposalAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource<bool> completion)
    {
        try { await DisposeCoreAsync().ConfigureAwait(false); completion.TrySetResult(true); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try { await DisposeToolChangesAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { using var synchronous = new SynchronousCleanupScope(this, taskShutdown: true); _taskShutdown.Cancel(); }
        catch (Exception error) { if (failure is null) failure = error; else AttachDisposalError(failure, error); }
        try
        {
            ValueTask disposing;
            using (var synchronous = new SynchronousCleanupScope(this)) disposing = _protocol.DisposeAsync();
            await disposing.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (failure is null) failure = error;
            else AttachDisposalError(failure, error);
        }
        finally { _taskShutdown.Dispose(); }
        if (_ownedTransport is not null)
            try { await DisposeTransportAsync(_ownedTransport, this).ConfigureAwait(false); }
            catch (Exception error) { if (failure is null) failure = error; else AttachDisposalError(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async ValueTask DisposeTransportAsync(IClientTransport transport, McpClient? owner = null)
    {
        if (transport is IAsyncDisposable asynchronous)
        {
            ValueTask disposing;
            using (var scope = owner is null ? null : new SynchronousCleanupScope(owner)) disposing = asynchronous.DisposeAsync();
            await disposing.ConfigureAwait(false);
        }
        else if (transport is IDisposable synchronous)
        {
            using var scope = owner is null ? null : new SynchronousCleanupScope(owner);
            synchronous.Dispose();
        }
    }

    private sealed class SynchronousCleanupScope : IDisposable
    {
        internal McpClient Owner { get; }
        internal bool TaskShutdown { get; }
        internal SynchronousCleanupScope? Previous { get; } = _synchronousCleanup;
        internal SynchronousCleanupScope(McpClient owner, bool taskShutdown = false)
        { Owner = owner; TaskShutdown = taskShutdown; _synchronousCleanup = this; }
        public void Dispose() => _synchronousCleanup = Previous;
    }

    private static void AttachDisposalError(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        var errors = primary.Data[key] is IEnumerable<Exception> existing ? existing.ToList() : new List<Exception>();
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
        primary.Data[key] = errors;
    }
    private void ThrowIfDisposed() { lock (_gate) if (_disposal is not null) throw new ObjectDisposedException(nameof(McpClient)); }
}

/// <summary>Bounded discovery settings; filters receive original server names and prefixes affect only agent-facing names.</summary>
public sealed class McpToolListOptions
{
    /// <summary>An optional prefix applied as prefix_toolName.</summary>
    public string? Prefix { get; init; }
    /// <summary>Whether discovery warns and skips a tool whose nonempty prefix makes its adapted name exceed 64 characters. Defaults to false; other name, schema, and protocol errors remain fatal.</summary>
    public bool ContinueOnError { get; init; }
    /// <summary>An optional selection predicate applied to original server tool names.</summary>
    public Func<string, bool>? ToolFilter { get; init; }
    /// <summary>An optional progress observer used when an adapted tool is invoked by the agent.</summary>
    public IProgress<ProgressNotificationValue>? Progress { get; init; }
    /// <summary>The maximum discovery pages, defaulting to 64.</summary>
    public int MaximumPages { get; init; } = 64;
    /// <summary>The maximum tools received across all pages before filtering, defaulting to 10000.</summary>
    public int MaximumTools { get; init; } = 10000;
}
