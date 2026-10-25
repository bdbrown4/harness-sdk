using System.Collections.ObjectModel;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

public static partial class SwarmTool
{
    private static SwarmToolOptions Capture(SwarmToolOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotBlank(options.Name, nameof(options.Name));
        Guard.NotNull(options.Presets, nameof(options.Presets));
        if (options.MaxAgents <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxAgents));
        if (options.MaxHandoffs is <= 0 || options.MaxIterations is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ExecutionTimeout is { } total && (total <= TimeSpan.Zero || total.TotalMilliseconds > int.MaxValue) ||
            options.NodeTimeout is { } node && (node <= TimeSpan.Zero || node.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.RepetitiveHandoffDetectionWindow is < 0 || options.RepetitiveHandoffMinUniqueAgents is < 0) throw new ArgumentOutOfRangeException(nameof(options));
        var presets = new Dictionary<string, Preset>(StringComparer.Ordinal);
        foreach (var pair in options.Presets)
        {
            Guard.NotBlank(pair.Key, nameof(options.Presets)); Guard.NotNull(pair.Value, nameof(options.Presets));
            ValidateModel(pair.Value.Model);
            presets.Add(pair.Key, pair.Value with { Tools = CopyNames(pair.Value.Tools) });
        }
        var defaultPreset = options.DefaultPreset ?? presets.Keys.FirstOrDefault();
        if (defaultPreset is not null && !presets.ContainsKey(defaultPreset)) throw new ArgumentException("DefaultPreset must name a configured role.", nameof(options));
        return options with
        {
            Presets = new ReadOnlyDictionary<string, Preset>(presets),
            DefaultPreset = defaultPreset,
            Instructions = CopyAxis(options.Instructions, "instructions"),
            Tools = CopyAxis(options.Tools, "tools"),
            Model = CopyAxis(options.Model, "model")
        };
    }

    private static IReadOnlyList<string>? CopyNames(IReadOnlyList<string>? names)
    {
        if (names is null) return null;
        if (names.Any(name => name is null)) throw new ArgumentException("Tool names cannot contain null.");
        return Array.AsReadOnly(names.ToArray());
    }

    private static Authority CopyAxis(Authority axis, string kind)
    {
        Guard.NotNull(axis, kind);
        if (axis is Open && kind == "instructions" || axis is Inherit && kind is "tools" or "model") return axis;
        if (axis is Fixed fixedAxis)
        {
            ValidateAxisValue(kind, fixedAxis.Value, false);
            return kind == "tools" ? new Fixed(CopyNames(fixedAxis.Value as IReadOnlyList<string>)) : fixedAxis;
        }
        if (axis is Choice choice)
        {
            if (choice.Options.Count == 0 || kind == "tools" && !choice.Multiple) throw new ArgumentException(kind + " Choice requires available options and tools requires multiple=true.");
            foreach (var option in choice.Options) ValidateAxisValue(kind, option.Value, true);
            return new Choice(choice.Options.ToArray(), choice.Multiple);
        }
        throw new ArgumentException("Unsupported authority for " + kind + ".");
    }

    private static void ValidateAxisValue(string kind, object? value, bool choice)
    {
        if (kind == "model") { ValidateModel(value); return; }
        if (kind == "instructions" && value is null or string) return;
        if (kind == "tools" && (choice ? value is string : value is null or IReadOnlyList<string>)) return;
        throw new ArgumentException("Unsupported value for " + kind + ".");
    }

    private static void ValidateModel(object? value)
    {
        if (value is not null and not IModel and not string) throw new ArgumentException("Models must be native IModel instances or provider/name strings.");
        if (value is string name) Guard.NotBlank(name, nameof(value));
    }

    private static JsonElement BuildSchema(SwarmToolOptions options)
    {
        var axes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (options.Presets.Count > 0)
        {
            var description = "Role to assign this agent." + (options.DefaultPreset is { } role ? " Omit to use the default ('" + role + "')." : string.Empty);
            var lines = options.Presets.Select(pair => "- " + pair.Key + ": " + DescribePreset(pair.Value)).ToArray();
            axes["agent_type"] = new { type = "string", @enum = options.Presets.Keys.ToArray(), description = description + "\nOptions:\n" + string.Join("\n", lines) };
        }
        AddAxis(axes, "instructions", options.Instructions, "A system prompt defining this agent's role.");
        AddAxis(axes, "tools", options.Tools, "Subset of tools to grant this agent. Fewer is safer; it cannot exceed your own. Omit to grant all inherited tools.");
        AddAxis(axes, "model", options.Model, "Which model this agent runs on. Omit to inherit the parent's model.");
        var properties = new Dictionary<string, object> { ["name"] = new { type = "string", description = "A unique name for this agent." } };
        foreach (var pair in axes) properties.Add(pair.Key, pair.Value);
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = new[] { "task", "agents" },
            properties = new
            {
                task = new { type = "string", description = "The objective for the agent team." },
                entry_point = new { type = "string", description = "Name of the agent that receives the task and starts the swarm. Defaults to the first agent." },
                agents = new
                {
                    type = "array",
                    description = "Agent specifications.",
                    minItems = 1,
                    maxItems = options.MaxAgents,
                    items = new { type = "object", properties, required = options.Instructions is Open && options.Presets.Count == 0 ? new[] { "name", "instructions" } : new[] { "name" }, additionalProperties = false }
                }
            }
        });
    }

    private static string DescribePreset(Preset preset)
    {
        var fields = new List<string>();
        if (preset.Instructions is not null) fields.Add("instructions");
        if (preset.Tools is not null) fields.Add("tools: " + (preset.Tools.Count == 0 ? "none" : string.Join(", ", preset.Tools)));
        if (preset.Model is not null) fields.Add(preset.Model is string name ? "model: " + name : "model");
        if (fields.Count == 0) return preset.Description;
        var details = "sets " + string.Join("; ", fields);
        return preset.Description.Length == 0 ? details : preset.Description + " (" + details + ")";
    }

    private static void AddAxis(Dictionary<string, object> properties, string name, Authority axis, string description)
    {
        if (axis is Open) properties[name] = new { type = "string", description };
        else if (axis is Choice choice)
        {
            var descriptions = choice.Options.Where(option => option.Description.Length > 0).Select(option => "- " + option.Name + ": " + option.Description).ToArray();
            if (descriptions.Length > 0) description += "\nOptions:\n" + string.Join("\n", descriptions);
            var values = choice.Options.Select(option => option.Name).ToArray();
            properties[name] = choice.Multiple ? new { type = "array", items = new { type = "string", @enum = values }, description } : (object)new { type = "string", @enum = values, description };
        }
    }

    private static (string Task, string? Entry, IReadOnlyList<SwarmAgentSpec> Specs) Resolve(JsonElement input, SwarmToolOptions options)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.String ||
            !input.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array) throw new ArgumentException("Swarm input requires task:string and agents:array.");
        var entry = ReadString(input, "entry_point"); var specs = new List<SwarmAgentSpec>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in agents.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each agent must be an object.");
            var name = ReadString(raw, "name")?.Trim();
            if (string.IsNullOrEmpty(name) || !names.Add(name!)) throw new ArgumentException("Agent names must be nonempty and distinct.");
            var role = ReadString(raw, "agent_type");
            if (role is not null && !options.Presets.ContainsKey(role)) throw new ArgumentException("Unknown agent_type: " + role);
            role ??= options.Instructions is Open or Choice && raw.TryGetProperty("instructions", out _) ? null : options.DefaultPreset;
            var preset = role is null ? null : options.Presets[role];
            specs.Add(new()
            {
                Name = name!,
                AgentType = role,
                Instructions = Scalar(raw, "instructions", options.Instructions, preset?.Instructions) as string,
                Tools = ResolveTools(raw, options.Tools, preset?.Tools),
                Model = Scalar(raw, "model", options.Model, preset?.Model)
            });
        }
        if (specs.Count == 0 || specs.Count > options.MaxAgents) throw new ArgumentException($"Between 1 and {options.MaxAgents} agents required, got {specs.Count}.");
        if (entry is not null && !names.Contains(entry)) throw new ArgumentException("entry_point does not match any agent name: " + entry);
        return (task.GetString()!, entry, Array.AsReadOnly(specs.ToArray()));
    }

    private static object? Scalar(JsonElement raw, string key, Authority axis, object? preset)
    {
        if (raw.TryGetProperty(key, out var value))
        {
            if (axis is Open) return value.ValueKind is JsonValueKind.String or JsonValueKind.Null ? value.GetString() : throw new ArgumentException(key + " must be a string or null.");
            if (axis is Choice choice && value.ValueKind == JsonValueKind.String && choice.Options.FirstOrDefault(option => option.Name == value.GetString()) is { } option) return option.Value;
        }
        return preset ?? (axis as Fixed)?.Value;
    }

    private static IReadOnlyList<string>? ResolveTools(JsonElement raw, Authority axis, IReadOnlyList<string>? preset)
    {
        if (axis is Choice choice)
        {
            if (raw.TryGetProperty("tools", out var requested))
            {
                var names = requested.ValueKind == JsonValueKind.String ? new[] { requested.GetString()! } : requested.ValueKind == JsonValueKind.Array
                    ? requested.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];
                return Array.AsReadOnly(names.Select(name => choice.Options.FirstOrDefault(option => option.Name == name)?.Value as string).Where(value => value is not null).Cast<string>().ToArray());
            }
            var permitted = choice.Options.Select(option => (string)option.Value!).ToArray();
            return Array.AsReadOnly(preset is null ? permitted : preset.Where(value => permitted.Contains(value, StringComparer.Ordinal)).ToArray());
        }
        return CopyNames(preset ?? (axis as Fixed)?.Value as IReadOnlyList<string>);
    }

    private static string? ReadString(JsonElement raw, string key) => !raw.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException(key + " must be a string.");
}
