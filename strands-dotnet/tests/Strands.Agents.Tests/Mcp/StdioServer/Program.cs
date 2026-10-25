using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var processId = Process.GetCurrentProcess().Id;
if (args.Contains("--emit-stderr", StringComparer.Ordinal)) await Console.Error.WriteLineAsync("owned stdio diagnostic");
if (args.FirstOrDefault(argument => argument.StartsWith("--hold-process=", StringComparison.Ordinal)) is { } holding)
{
    var marker = holding.Substring("--hold-process=".Length);
    File.WriteAllText(marker + ".pending", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(marker + ".pending", marker);
    while (!File.Exists(marker + ".release")) await Task.Delay(25);
    return;
}
if (args.FirstOrDefault(argument => argument.StartsWith("--spawn-descendant=", StringComparison.Ordinal)) is { } spawning)
{
    var child = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory };
    var assembly = typeof(Program).Assembly.Location;
#if NETFRAMEWORK
    child.FileName = assembly;
    child.Arguments = QuoteArgument("--hold-process=" + spawning.Substring("--spawn-descendant=".Length));
#else
    using var current = Process.GetCurrentProcess();
    child.FileName = current.MainModule!.FileName!;
    child.ArgumentList.Add(assembly);
    child.ArgumentList.Add("--hold-process=" + spawning.Substring("--spawn-descendant=".Length));
#endif
    using var started = Process.Start(child) ?? throw new IOException("The test descendant did not start.");
}
var taskMode = args.Contains("--tasks", StringComparer.Ordinal);
var legacyTaskMode = args.Contains("--legacy-tasks", StringComparer.Ordinal);
var toolChanges = args.Contains("--tool-changes", StringComparer.Ordinal);
var modernToolChanges = toolChanges && args.Contains("--modern-tool-changes", StringComparer.Ordinal);
var changedToolName = "echo";
var toolChangeSubscriptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
var tasks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

while (await Console.In.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var request = document.RootElement;
    var method = request.GetProperty("method").GetString();
    if (!request.TryGetProperty("id", out var id))
    {
        if (modernToolChanges && method == "notifications/cancelled" && request.GetProperty("params").TryGetProperty("requestId", out var cancelled))
            toolChangeSubscriptions.Remove(cancelled.GetRawText());
        continue;
    }
    if (modernToolChanges && method == "subscriptions/listen")
    {
        if (request.GetProperty("params").GetProperty("notifications").GetProperty("toolsListChanged").ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The fixture requires a tools-list subscription.");
        toolChangeSubscriptions.Add(id.GetRawText(), id.Clone());
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/subscriptions/acknowledged",
            @params = new
            {
                notifications = new { toolsListChanged = true },
                _meta = new Dictionary<string, object>
                { ["io.modelcontextprotocol/subscriptionId"] = id }
            },
        }));
        await Console.Out.FlushAsync();
        continue;
    }
    var legacyTaskCall = legacyTaskMode && method == "tools/call" && request.GetProperty("params").TryGetProperty("task", out _);
    if ((taskMode && method == "tools/call") || legacyTaskCall) tasks.Add("task-" + tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), request.GetProperty("params").GetProperty("arguments").Clone());
    if (legacyTaskCall && args.FirstOrDefault(argument => argument.StartsWith("--legacy-task-marker=", StringComparison.Ordinal)) is { } legacyMarker)
    {
        var marker = legacyMarker.Substring("--legacy-task-marker=".Length);
        File.WriteAllText(marker + ".pending", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(marker + ".pending", marker);
    }
    if ((taskMode || legacyTaskMode) && method == "tasks/get" && args.Contains("--block-task", StringComparer.Ordinal))
    {
        var marker = args.Single(argument => argument.StartsWith("--task-marker=", StringComparison.Ordinal)).Substring("--task-marker=".Length);
        File.WriteAllText(marker + ".pending", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(marker + ".pending", marker);
        await Task.Delay(Timeout.Infinite);
    }
    if (legacyTaskMode && method == "tasks/result" && args.Contains("--legacy-input", StringComparer.Ordinal))
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        { jsonrpc = "2.0", id = "legacy-approval", method = "elicitation/create", @params = new { message = "Approve the legacy operation", requestedSchema = new { type = "object", properties = new { } } } }));
        await Console.Out.FlushAsync();
        while (true)
        {
            using var answer = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? throw new EndOfStreamException());
            if (answer.RootElement.TryGetProperty("id", out var answerId) && answerId.ValueKind == JsonValueKind.String && answerId.GetString() == "legacy-approval")
            {
                if (answer.RootElement.GetProperty("result").GetProperty("action").GetString() != "accept") throw new InvalidDataException("The fixture requires an accepted legacy input response.");
                break;
            }
        }
    }
    if (method == "initialize" && args.Contains("--block-initialize", StringComparer.Ordinal))
    {
        var marker = args.Single(argument => argument.StartsWith("--initialize-marker=", StringComparison.Ordinal)).Substring("--initialize-marker=".Length);
        File.WriteAllText(marker + ".pending", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(marker + ".pending", marker);
        await Task.Delay(Timeout.Infinite);
    }
    object? result = method switch
    {
        "server/discover" when modernToolChanges => new { supportedVersions = new[] { "2026-07-28" }, capabilities = new { tools = new { listChanged = true } }, _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = "stdio-tool-changes", version = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ttlMs = 0, cacheScope = "private" },
        "server/discover" when taskMode => new { supportedVersions = new[] { "2026-07-28" }, capabilities = new { tools = new { }, extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new { } } }, _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = "stdio-task-fixture", version = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ttlMs = 0, cacheScope = "private" },
        "initialize" when toolChanges => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { listChanged = true } }, serverInfo = new { name = "stdio-tool-changes", version = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        "initialize" when legacyTaskMode => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { }, tasks = new { cancel = new { }, requests = new { tools = new { call = new { } } } } }, serverInfo = new { name = "stdio-legacy-task-fixture", version = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "stdio-fixture", version = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        "tools/list" when toolChanges => new { tools = new[] { new { name = changedToolName, description = "A watched stdio tool", inputSchema = new { type = "object" } } } },
        "tools/list" when legacyTaskMode => new { tools = new[] { new { name = "echo", description = "Echo an input through a legacy task", inputSchema = new { type = "object" }, execution = new { taskSupport = "optional" } } } },
        "tools/list" => new { tools = new[] { new { name = "echo", description = "Echo an input", inputSchema = new { type = "object" } } } },
        "tools/call" when legacyTaskCall => new { task = LegacyState("task-" + (tasks.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "working") },
        "tasks/get" when legacyTaskMode => LegacyState(request.GetProperty("params").GetProperty("taskId").GetString()!, args.Contains("--legacy-input", StringComparer.Ordinal) ? "input_required" : "completed"),
        "tasks/result" when legacyTaskMode => new { content = new[] { new { type = "text", text = ToolText(tasks[request.GetProperty("params").GetProperty("taskId").GetString()!], args) } } },
        "tasks/cancel" when legacyTaskMode => LegacyState(request.GetProperty("params").GetProperty("taskId").GetString()!, "cancelled"),
        "tools/call" when taskMode => new { resultType = "task", taskId = "task-" + (tasks.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), status = "working", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:00Z", ttlMs = (int?)null, pollIntervalMs = 10 },
        "tasks/get" when taskMode => new { resultType = "complete", taskId = request.GetProperty("params").GetProperty("taskId").GetString(), status = "completed", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:01Z", ttlMs = (int?)null, result = new { content = new[] { new { type = "text", text = ToolText(tasks[request.GetProperty("params").GetProperty("taskId").GetString()!], args) } } } },
        "tasks/cancel" or "tasks/update" when taskMode => new { resultType = "complete" },
        "tools/call" => new { content = new[] { new { type = "text", text = ToolText(request.GetProperty("params").GetProperty("arguments"), args) } } },
        "ping" => new { },
        _ => null,
    };
    var response = result is null
        ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } })
        : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    await Console.Out.WriteLineAsync(response);
    await Console.Out.FlushAsync();
    if (toolChanges && method == "tools/call")
    {
        changedToolName = "updated";
        if (modernToolChanges)
        {
            foreach (var subscription in toolChangeSubscriptions.Values)
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", method = "notifications/tools/list_changed", @params = new { _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = subscription } } }));
        }
        else await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/tools/list_changed", @params = new { } }));
        await Console.Out.FlushAsync();
    }
    if (method == "tools/call" && args.Contains("--exit-after-call", StringComparer.Ordinal)) return;
}

#if NETFRAMEWORK
static string QuoteArgument(string value)
{
    var result = new StringBuilder("\""); var slashes = 0;
    foreach (var character in value)
    {
        if (character == '\\') { slashes++; continue; }
        result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
    }
    return result.Append('\\', slashes * 2).Append('"').ToString();
}
#endif

static object LegacyState(string taskId, string status) => new
{ taskId, status, createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:01Z", ttl = (int?)null, pollInterval = 10 };

static string? ToolText(JsonElement input, string[] arguments)
{
    if (!input.TryGetProperty("inspectRuntime", out var inspect) || !inspect.GetBoolean()) return input.GetProperty("value").GetString();
    return JsonSerializer.Serialize(new
    {
        processId = Process.GetCurrentProcess().Id,
        workingDirectory = Environment.CurrentDirectory,
        arguments,
        environment = Environment.GetEnvironmentVariable(input.GetProperty("environment").GetString()!),
        inherited = Environment.GetEnvironmentVariable(input.GetProperty("inherited").GetString()!),
        coordinationEnvironment = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key => key.StartsWith("__STRANDS_MCP_RELEASE_", StringComparison.Ordinal)).ToArray(),
        sentinel = File.ReadAllText(input.GetProperty("sentinel").GetString()!),
    });
}
