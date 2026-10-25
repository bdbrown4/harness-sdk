using System.Text.Json;
using System.Text.RegularExpressions;

namespace Strands.Agents.Monty;

/// <summary>Creates tools that orchestrate registered agent tools using genuine bounded Monty Python execution.</summary>
public static class ProgrammaticToolCaller
{
    /// <summary>The shared model-facing instructions for Python tool orchestration.</summary>
    public const string DefaultDescription =
        "Execute Python code that orchestrates the agent's other tools, each exposed as an async function " +
        "taking keyword arguments -- always `await` them, e.g. `result = await read(path=\"/etc/hosts\")`. " +
        "The code runs in an async context, so `await` and `asyncio.gather(...)` work without boilerplate. " +
        "A tool result that is structured content or JSON text (an object or array) arrives parsed, as a " +
        "dict/list; anything else arrives as a string. A tool that fails raises RuntimeError, which you can " +
        "catch. Only text sent to `print()` is returned to you: a tool's return value stays in the code's local " +
        "scope unless you print it. A tool whose name is not a valid Python identifier (for example " +
        "`fetch-url` or `ns.fetch`) is also available with those characters replaced by underscores " +
        "(`fetch_url`, `ns_fetch`). The code runs in a sandboxed Python subset: `asyncio`, `json`, `re`, " +
        "`math`, `datetime`, `collections`, `itertools`, `functools`, `dataclasses`, `typing`, `base64` " +
        "are importable; filesystem, network, and subprocess access do not exist -- use the agent's tools " +
        "for those. Generators (`yield`), class inheritance, `match`, and `del` are not supported. Use this " +
        "to chain, loop over, filter, or parallelize tool calls in a single turn instead of one tool call " +
        "per model round-trip, keeping large intermediate results out of the conversation.";
    /// <summary>Creates a caller. Each invocation starts a fresh native worker and exposes only the selected registered tools.</summary>
    public static ITool Create(ProgrammaticToolCallerOptions? options = null)
    {
        options ??= new();
        if (options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive and fit the portable timer range, or null.");
        if (options.MaxDuration <= TimeSpan.Zero || options.MaxMemoryBytes <= 0 || options.MaxSuspensions <= 0 || options.MaxRecursionDepth <= 0) throw new ArgumentOutOfRangeException(nameof(options), "All guest resource limits must be positive.");
        var allowed = options.AllowedTools?.ToArray();
        if (allowed?.Any(string.IsNullOrWhiteSpace) == true) throw new ArgumentException("Allowed tool names must be nonempty.", nameof(options));
        return new Caller(options with { AllowedTools = allowed });
    }

    private sealed class Caller(ProgrammaticToolCallerOptions options) : IRichTool
    {
        public ToolSpec Spec { get; } = new(options.Name, options.Description, JsonSerializer.SerializeToElement(new { type = "object", properties = new { code = new { type = "string", description = "Python code to execute. Use await tool_name(...) to call tools." } }, required = new[] { "code" } }));
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            if (context?.Agent is null) throw new ArgumentException("The programmatic tool caller requires an agent context.", nameof(context));
            var code = input.GetProperty("code").GetString() ?? throw new ArgumentException("code must be a string.", nameof(input));
            cancellationToken.ThrowIfCancellationRequested();
            await context.Agent.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var functions = BuildFunctions(context, options.AllowedTools);
            var output = new Output();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.Timeout is { } timeout) operation.CancelAfter(timeout);
            try
            {
                await NativeWorker.RunAsync(code, functions, options, output, operation.Token).ConfigureAwait(false);
                var text = output.Text();
                return new(context.ToolUse.ToolUseId, text.Length == 0 ? "(no output)" : text);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            {
                var cancelled = new MontyCancellationException(error, cancellationToken);
                foreach (System.Collections.DictionaryEntry item in error.Data) cancelled.Data[item.Key] = item.Value;
                throw cancelled;
            }
            catch (OperationCanceledException error) when (options.Timeout is not null)
            {
                return new(context.ToolUse.ToolUseId, output.WithError("Execution error: timed out after " + options.Timeout.Value.TotalSeconds.ToString("g", System.Globalization.CultureInfo.InvariantCulture) + " seconds."), ToolResultStatus.Error) { Error = error };
            }
            catch (MontyCodeException error)
            {
                return new(context.ToolUse.ToolUseId, error.Syntax ? Output.Cap("Syntax error:\n" + error.Message) : output.WithError("Execution error:\n" + error.Message), ToolResultStatus.Error) { Error = error };
            }
        }
    }

    private static IReadOnlyDictionary<string, NativeWorker.HostFunction> BuildFunctions(ToolContext context, IReadOnlyList<string>? allowed)
    {
        var available = context.Agent.Tools.Where(tool => tool is not Caller).Select(tool => tool.Spec.Name).Where(name => allowed is null || allowed.Contains(name, StringComparer.Ordinal)).ToArray();
        var lookup = new Dictionary<string, NativeWorker.HostFunction>(StringComparer.Ordinal);
        foreach (var name in available) lookup.Add(name, (args, kwargs, token) => CallToolAsync(context, name, args, kwargs, token));
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in available.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            var alias = Regex.Replace(name, "[^A-Za-z0-9_]", "_");
            if (!Regex.IsMatch(alias, "^[A-Za-z_][A-Za-z0-9_]*$") || lookup.ContainsKey(alias)) continue;
            if (aliases.ContainsKey(alias)) ambiguous.Add(alias);
            else aliases.Add(alias, name);
        }
        foreach (var pair in aliases)
            if (!ambiguous.Contains(pair.Key)) lookup.Add(pair.Key, lookup[pair.Value]);
        foreach (var alias in ambiguous)
            try { SdkLogging.Warning("programmatic_tool_caller: multiple tools normalize to '{Alias}', no alias injected.", alias); } catch (Exception) { }
        return lookup;
    }

    private static async Task<JsonElement> CallToolAsync(ToolContext parent, string name, JsonElement positional, JsonElement keywords, CancellationToken cancellationToken)
    {
        if (positional.GetArrayLength() != 0) throw new MontyTypeException(name + "() takes keyword arguments only, e.g. " + name + "(key=value)");
        cancellationToken.ThrowIfCancellationRequested();
        var result = await parent.Agent.InvokeToolAsync(new("ptc_" + Guid.NewGuid().ToString("N"), name, keywords), parent.InvocationState, cancellationToken).ConfigureAwait(false);
        if (result.Status == ToolResultStatus.Error) throw new InvalidOperationException("Tool '" + name + "' error: " + result.Content);
        if (result.StructuredContent is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured) return structured.Clone();
        if (result.ContentBlocks.Count == 1 && result.ContentBlocks[0] is JsonBlock json) return json.Json.Clone();
        var text = result.ContentBlocks.Count == 0 ? result.Content : string.Join("\n", result.ContentBlocks.Select(block => block switch { TextBlock value => value.Text, JsonBlock value => value.Json.GetRawText(), _ => null }).Where(value => value is not null));
        if (result.ContentBlocks.Count <= 1 && (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)))
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { }
        }
        return JsonSerializer.SerializeToElement(text);
    }
}
