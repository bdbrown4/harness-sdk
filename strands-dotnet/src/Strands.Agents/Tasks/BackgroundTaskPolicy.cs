using System.Text.Json;
using System.Text.Json.Nodes;

namespace Strands.Agents.Tasks;

internal enum BackgroundTaskMode { Never, Agentic, Always }
internal readonly record struct BackgroundTaskRule(BackgroundTaskMode Mode, bool Exact);
internal sealed class BackgroundTaskPolicy
{
    internal const string SelectionProperty = "_background_execution";
    internal const string ManageToolName = "strands_manage_background_task";
    private readonly Dictionary<string, BackgroundTaskMode> _rules = new(StringComparer.Ordinal);
    internal BackgroundTaskPolicy(BackgroundTasksConfig config)
    {
        Guard.NotNull(config, nameof(config)); Guard.NotNull(config.Always, nameof(config.Always)); Guard.NotNull(config.Never, nameof(config.Never));
        Assign(config.Agentic ?? (config.Always.Contains("*") || config.Never.Contains("*") ? [] : new[] { "*" }), BackgroundTaskMode.Agentic);
        Assign(config.Always, BackgroundTaskMode.Always); Assign(config.Never, BackgroundTaskMode.Never);
    }
    private void Assign(IReadOnlyList<string> names, BackgroundTaskMode mode)
    {
        foreach (var name in names)
        {
            Guard.NotNullOrWhiteSpace(name, nameof(names));
            if (_rules.TryGetValue(name, out var previous) && previous != mode) throw new ArgumentException("Tool '" + name + "' cannot have conflicting background execution modes.");
            _rules[name] = mode;
        }
    }
    internal void Normalize(BackgroundTasksConfig config)
    {
        var normalized = new BackgroundTaskPolicy(config);
        _rules.Clear();
        foreach (var pair in normalized._rules) _rules.Add(pair.Key, pair.Value);
    }
    internal BackgroundTaskRule? For(string name) => _rules.TryGetValue(name, out var exact) ? new(exact, true)
        : _rules.TryGetValue("*", out var wildcard) ? new(wildcard, false) : null;
    internal ToolSpec? AddSelection(ToolSpec spec)
    {
        var schema = spec.InputSchema;
        if (new[] { "$ref", "allOf", "anyOf", "oneOf", "not", "if", "then", "else" }.Any(key => schema.TryGetProperty(key, out _)) ||
            schema.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "object") ||
            schema.TryGetProperty("properties", out var properties) && properties.ValueKind != JsonValueKind.Object || DeclaresSelection(schema)) return null;
        var copy = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var fields = copy["properties"]?.AsObject() ?? new JsonObject();
        fields[SelectionProperty] = new JsonObject { ["type"] = "boolean", ["description"] = "Run this tool in the background and continue without waiting for its result." };
        copy["type"] = "object";
        if (copy["properties"] is null) copy["properties"] = fields;
        return new(spec.Name, spec.Description, JsonSerializer.SerializeToElement(copy)) { OutputSchema = spec.OutputSchema?.Clone(), Annotations = spec.Annotations?.Clone() };
    }
    internal (ToolUseBlock Call, bool Selected) Prepare(ToolUseBlock call, string? requestedToolName = null)
    {
        if (call.Input.ValueKind != JsonValueKind.Object || !call.Input.TryGetProperty(SelectionProperty, out var flag)) return ((ToolUseBlock)Message.CloneBlock(call), false);
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("The reserved '" + SelectionProperty + "' parameter must be a boolean.");
        var copy = JsonNode.Parse(call.Input.GetRawText())!.AsObject(); copy.Remove(SelectionProperty);
        return (call with { Input = JsonSerializer.SerializeToElement(copy) }, flag.GetBoolean() && For(requestedToolName ?? call.Name)?.Mode == BackgroundTaskMode.Agentic);
    }
    internal bool ShouldDispatch(ITool? tool, bool selected, ToolSpec? resolvedSpec = null)
    {
        if (tool is not null && Agent.IsDelegationTool(tool)) return false;
        if (tool is null) return false;
        var spec = resolvedSpec ?? tool.Spec;
        if (For(spec.Name) is not { } policy || policy.Mode == BackgroundTaskMode.Never) return false;
        if (!Eligible(tool, spec) || policy.Mode == BackgroundTaskMode.Agentic && AddSelection(spec) is null)
        {
            if (policy.Exact) throw new ArgumentException("Tool '" + spec.Name + "' cannot honor its configured background execution mode.");
            return false;
        }
        return policy.Mode == BackgroundTaskMode.Always || selected;
    }
    internal void Validate(IReadOnlyList<ITool> tools, Func<ITool, ToolSpec>? resolveSpec = null)
    {
        foreach (var tool in tools)
        {
            var spec = resolveSpec?.Invoke(tool) ?? tool.Spec;
            if (DeclaresSelection(spec.InputSchema)) throw new ArgumentException("Tool '" + spec.Name + "' declares reserved parameter '" + SelectionProperty + "'.");
            if (For(spec.Name) is { Exact: true, Mode: not BackgroundTaskMode.Never }) _ = ShouldDispatch(tool, false, spec);
        }
    }
    internal static bool Eligible(ITool tool, ToolSpec? resolvedSpec = null) => !Agent.IsDelegationTool(tool) && tool is not IStructuredOutputTool && (resolvedSpec ?? tool.Spec).Name is not (ManageToolName or "summarize_context" or "truncate_context" or "pin_context" or "strands_structured_output");
    private static bool DeclaresSelection(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(SelectionProperty, out _) ||
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array && required.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == SelectionProperty);
}
