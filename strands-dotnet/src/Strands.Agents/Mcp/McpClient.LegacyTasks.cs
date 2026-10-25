using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

public sealed partial class McpClient
{
    private IReadOnlyDictionary<string, string> _legacyTaskSupport = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _legacyToolsListed;
    private bool _legacyProgressWarned;
    private readonly LegacyTaskCapabilities? _legacyCapabilities;

    private static string ReadLegacyTaskSupport(JsonElement definition) =>
        definition.TryGetProperty("execution", out var execution) && execution.ValueKind == JsonValueKind.Object &&
        execution.TryGetProperty("taskSupport", out var support) && support.ValueKind == JsonValueKind.String &&
        support.GetString() is "optional" or "required" ? support.GetString()! : "forbidden";

    private void SetLegacyToolDefinitions(Dictionary<string, string> support)
    { lock (_gate) { _legacyTaskSupport = support; _legacyToolsListed = true; } }

    private async Task EnsureLegacyToolDefinitionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate) if (_legacyToolsListed) return;
        using var request = StartTaskOperation(_tasksConfig!.RequestTimeout, cancellationToken);
        try { await WaitForTaskOperationAsync(ListToolsAsync(cancellationToken: request.Token), request.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryWarn("MCP legacy task discovery failed: " + error.GetType().Name);
            lock (_gate) _legacyToolsListed = true;
        }
    }

    private bool SupportsLegacyTasks(bool cancellation = false)
    {
        if (_protocol.NegotiatedProtocolVersion != "2025-11-25") return false;
        return _legacyCapabilities?.Supports(cancellation) == true;
    }

    private async ValueTask<CallToolResult> CompleteLegacyToolAsync(string serverName, JsonElement arguments,
        IProgress<ProgressNotificationValue>? progress, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var operation = StartTaskOperation(_tasksConfig!.PollTimeout, cancellationToken);
        McpTask? task = null;
        try
        {
            if (SupportsLegacyTasks()) await EnsureLegacyToolDefinitionsAsync(operation.Token).ConfigureAwait(false);
            var parameters = new JsonObject { ["name"] = serverName, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) };
            if (options?.GetMetaForRequest() is { } metadata) parameters["_meta"] = metadata.DeepClone();
            bool useTask;
            lock (_gate) useTask = SupportsLegacyTasks() && _legacyTaskSupport.TryGetValue(serverName, out var support) && support is "optional" or "required";
            if (!useTask)
            {
                var ordinary = await SendTaskRequestAsync("tools/call", parameters, null, progress, operation.Token).ConfigureAwait(false);
                McpTask.ValidateToolResult(ordinary);
                return ordinary.Deserialize<CallToolResult>()!;
            }
            if (progress is not null)
            {
                bool warn;
                lock (_gate) { warn = !_legacyProgressWarned; _legacyProgressWarned = true; }
                if (warn) TryWarn("MCP legacy task calls do not deliver application progress callbacks.");
            }
            var taskParameters = new JsonObject();
            if (_tasksConfig.LegacyTimeToLive is { } ttl) taskParameters["ttl"] = ttl.Ticks / TimeSpan.TicksPerMillisecond;
            parameters["task"] = taskParameters;
            var created = await SendTaskRequestAsync("tools/call", parameters, null, null, operation.Token).ConfigureAwait(false);
            if (created.ValueKind != JsonValueKind.Object || !created.TryGetProperty("task", out var handle)) throw new InvalidDataException("A legacy task call must return a wrapped task handle.");
            task = ParseLegacyTask(handle);
            while (task.Status == McpTaskStatus.Working)
            {
                await Task.Delay(task.PollInterval is { } cadence ? MaxPollInterval(cadence) : _tasksConfig.PollInterval, operation.Token).ConfigureAwait(false);
                var state = await SendTaskRequestAsync("tasks/get", TaskIdParameters(task.TaskId), null, null, operation.Token).ConfigureAwait(false);
                task = ParseLegacyTask(state, task.TaskId);
            }
            if (task.Status == McpTaskStatus.Cancelled) throw new McpTaskCancelledException(task.StatusMessage);
            if (task.Status == McpTaskStatus.Failed) throw new McpLegacyTaskFailedException(task.StatusMessage);
            var result = await SendTaskRequestAsync("tasks/result", TaskIdParameters(task.TaskId), null, null, operation.Token,
                // The existing outer operation owns the complete input wait. A second timer can race its cancellation and misclassify the deadline.
                task.Status == McpTaskStatus.InputRequired ? Timeout.InfiniteTimeSpan : null).ConfigureAwait(false);
            McpTask.ValidateToolResult(result);
            return result.Deserialize<CallToolResult>()!;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("MCP legacy tool task was cancelled.", error, cancellationToken); }
        catch (OperationCanceledException error) when (!_taskShutdown.IsCancellationRequested && operation.IsCancellationRequested)
        { throw new TimeoutException("MCP legacy tool task exceeded its overall deadline.", error); }
        finally
        {
            if (task is not null && task.Status is not (McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled) && IsConnected && SupportsLegacyTasks(cancellation: true))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    var acknowledgement = await SendTaskRequestAsync("tasks/cancel", TaskIdParameters(task.TaskId), null, null, cleanup.Token).ConfigureAwait(false);
                    _ = ParseLegacyTask(acknowledgement, task.TaskId);
                }
                catch (Exception error) { TryWarn("MCP legacy task cancellation failed: " + error.GetType().Name); }
            }
        }
    }

    private static JsonObject TaskIdParameters(string taskId) => new() { ["taskId"] = taskId };

    private static McpTask ParseLegacyTask(JsonElement value, string? taskId = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A legacy task state must be an object.");
        var normalized = value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        if (!normalized.TryGetValue("ttl", out var ttl)) throw new InvalidDataException("A legacy task requires ttl, including null for unlimited retention.");
        normalized["resultType"] = JsonSerializer.SerializeToElement("task");
        normalized["ttlMs"] = ttl;
        if (normalized.TryGetValue("pollInterval", out var poll)) normalized["pollIntervalMs"] = poll;
        var task = McpTask.Parse(JsonSerializer.SerializeToElement(normalized), "task");
        if (taskId is not null && task.TaskId != taskId) throw new InvalidDataException("Legacy task polling returned a different task identifier.");
        return task;
    }
}

/// <summary>A legacy server-reported task failure, whose wire status supplies text but no JSON-RPC error code.</summary>
public sealed class McpLegacyTaskFailedException : Exception
{
    internal McpLegacyTaskFailedException(string? statusMessage) : base(statusMessage is null ? "MCP legacy task failed." : "MCP legacy task failed: " + statusMessage) => StatusMessage = statusMessage;
    /// <summary>The server's original task status message, if supplied.</summary>
    public string? StatusMessage { get; }
}
