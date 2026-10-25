using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Creates delegation tools with closed authority choices, fresh children, bounded depth, and valid shared context.</summary>
public static class Subagent
{
    /// <summary>Instructions for the standard generalist role, matching the Python and TypeScript harnesses.</summary>
    public const string GeneralistInstructions = "You are a general-purpose subagent handling a focused subtask on behalf of a parent agent. You run in your own fresh conversation and cannot ask follow-up questions, so work from the task as given, make reasonable assumptions where it is underspecified, and see it through to a verified result. Return a self-contained answer: state what you did, what you found, and anything the parent needs to act on. Your final message is the only thing that returns to the parent, so put the substance there rather than in intermediate steps.";
    /// <summary>The standard generalist role.</summary>
    public static Preset Generalist { get; } = new() { Instructions = GeneralistInstructions, Description = "a general-purpose agent for a focused subtask that runs in its own context" };
    /// <summary>Builds a real tool. The supplied builder must return a fresh child and apply its resolved message history.</summary>
    public static IRichTool Create(SubagentOptions options) => new DelegationTool(options);
    internal static IRichTool Create(SubagentOptions options, IReadOnlyList<string> independentTools) => new DelegationTool(options, independentTools);

    private sealed class DelegationTool : IRichTool, IStreamingTool
    {
        private const string DepthStateKey = "subagentDepth";
        private readonly SubagentOptions _options;
        private readonly Authority _tools;
        private readonly Authority _mcpServers;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ITool>> _serverTools;
        private readonly HashSet<string> _remoteToolNames;
        private readonly IReadOnlyList<string> _independentTools;
        private readonly IReadOnlyDictionary<string, Preset> _presets;
        private readonly string? _defaultPreset;

        internal DelegationTool(SubagentOptions options, IReadOnlyList<string>? independentTools = null)
        {
            Guard.NotNull(options, nameof(options));
            Guard.NotNull(options.Builder, nameof(options.Builder));
            Guard.NotNull(options.Presets, nameof(options.Presets));
            Guard.NotNull(options.InheritedTools, nameof(options.InheritedTools));
            Guard.NotNull(options.InheritedMcpServers, nameof(options.InheritedMcpServers));
            if (options.MaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be non-negative.");
            _options = options;
            _independentTools = independentTools?.ToArray() ?? Array.Empty<string>();
            _serverTools = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<ITool>>(options.InheritedMcpServers.ToDictionary(pair =>
            {
                Guard.NotBlank(pair.Key, "MCP server name"); return pair.Key;
            }, pair =>
            {
                Guard.NotNull(pair.Value, nameof(options.InheritedMcpServers));
                if (pair.Value.Any(tool => tool is null)) throw new ArgumentException("MCP server tool groups cannot contain null.", nameof(options));
                return (IReadOnlyList<ITool>)Array.AsReadOnly(pair.Value.ToArray());
            }, StringComparer.Ordinal));
            _remoteToolNames = new(_serverTools.Values.SelectMany(group => group).Select(tool => tool.Spec.Name), StringComparer.Ordinal);
            _tools = options.Tools is Fixed { Value: IReadOnlyList<string> fixedTools } ? new Fixed(Array.AsReadOnly(fixedTools.ToArray())) : options.Tools ?? new Choice(options.InheritedTools.Where(name => !_remoteToolNames.Contains(name)).Select(name => new Option(name)).ToArray(), multiple: true);
            _mcpServers = options.McpServers is Fixed { Value: IReadOnlyList<string> fixedServers } ? new Fixed(Array.AsReadOnly(fixedServers.ToArray())) : options.McpServers ?? (_serverTools.Count == 0 ? new Inherit() : new Choice(_serverTools.Keys.Select(name => new Option(name)).ToArray(), multiple: true));
            ValidateAxis(options.Instructions, "instructions", allowOpen: true, allowInherit: false);
            ValidateAxis(_tools, "tools", allowOpen: false, allowInherit: true);
            ValidateAxis(_mcpServers, "mcp_servers", allowOpen: false, allowInherit: true);
            ValidateAxis(options.Model, "model", allowOpen: false, allowInherit: true);
            ValidateAxis(options.Context, "context", allowOpen: false, allowInherit: false);
            if (options.Tools is Choice { Options.Count: 0 }) throw new ArgumentException("tools=Choice([]) offers no options; use Fixed([]) or Inherit().", nameof(options));
            if (_tools is Choice { Multiple: false }) throw new ArgumentException("A tools Choice must set multiple=true.", nameof(options));
            if (_mcpServers is Choice { Options.Count: 0 }) throw new ArgumentException("McpServers=Choice([]) offers no options; use Fixed([]) or Inherit().", nameof(options));
            if (_mcpServers is Choice { Multiple: false }) throw new ArgumentException("A McpServers Choice must set multiple=true.", nameof(options));
            _presets = options.Presets.ToDictionary(pair => pair.Key, pair =>
            {
                Guard.NotBlank(pair.Key, nameof(options));
                Guard.NotNull(pair.Value, nameof(options));
                ValidateContext(pair.Value.Context);
                ValidateModel(pair.Value.Model);
                return pair.Value with { Tools = pair.Value.Tools is null ? null : Array.AsReadOnly(pair.Value.Tools.ToArray()) };
            }, StringComparer.Ordinal);
            _defaultPreset = options.DefaultPreset ?? _presets.Keys.FirstOrDefault();
            if (_defaultPreset is not null && !_presets.ContainsKey(_defaultPreset)) throw new ArgumentException("DefaultPreset must name a configured role.", nameof(options));
            var description = "Delegate a self-contained task to a subagent that runs in its own context and returns a final report. Reach for this when a subtask would otherwise flood your context with intermediate work and you only need its conclusion. Do not poll for progress or redo its work.";
            if (_presets.Count > 0) description += "\n\nAvailable subagents (agent_type):\n" + string.Join("\n", _presets.Select(pair => "- " + pair.Key + ": " + (string.IsNullOrEmpty(pair.Value.Description) ? pair.Key : pair.Value.Description)));
            Spec = new(options.Name, description, BuildSchema());
        }

        public ToolSpec Spec { get; }
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            ToolStreamRunner.InvokeAsync(this, input, context, null, cancellationToken);

        public async IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Agent? child = null;
            IDisposable? readiness = null;
            IAsyncEnumerator<IToolStreamEvent>? iterator = null;
            Exception? primary = null;
            ToolResultBlock? terminal = null;
            var reachedBoundary = false;
            try
            {
                try
                {
                    if (context.AgentToolInterrupts is { HasForwarded: true } journal) journal.RequireAnyAnswer();
                    var remaining = RemainingDepth(context.Agent);
                    if (remaining <= 0)
                        terminal = Error(context, $"Delegation depth limit reached ({_options.MaxDepth} levels); you cannot delegate further. Complete this task yourself instead of calling subagent again.");
                    else
                    {
                        var spec = SubagentContext.Prepare(Resolve(input, context.Agent), context.Agent.Messages);
                        child = await _options.Builder(spec, context, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Subagent builder returned null.");
                        if (ReferenceEquals(child, context.Agent))
                        {
                            child = null; // The caller's parent was never transferred to us.
                            throw new InvalidOperationException("Subagent builder must return a fresh child, not the calling parent.");
                        }
                        await child.InitializeAsync(cancellationToken).ConfigureAwait(false);
                        var allowed = AllowedToolNames(spec, context.Agent);
                        ValidateChildTools(child, allowed);
                        child.State.Set(DepthStateKey, remaining - 1);
                        var ownedChild = child;
                        // A restored interrupted turn carries its old app state. Reapply current
                        // authority after restoration, before any new model/tool execution.
                        readiness = child.Hooks.Register<BeforeInvocationEvent>((_, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            ValidateChildTools(ownedChild, allowed);
                            var current = RemainingDepth(context.Agent);
                            if (current <= 0) throw new InvalidOperationException("Delegation depth limit reached; the interrupted child cannot run.");
                            ownedChild.State.Set(DepthStateKey, Math.Min(remaining, current) - 1);
                            return default;
                        }, int.MaxValue);
                        var adapter = child.AsFreshChildTool(Spec, spec.Prompt, FinalResult);
                        iterator = adapter.StreamAsync(input, context, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    }
                }
                catch (Exception error) { primary = error; }
                if (iterator is not null)
                    while (true)
                    {
                        bool moved;
                        try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception error) { primary = error; break; }
                        if (!moved) break;
                        if (iterator.Current is ToolStreamResultEvent completed) terminal = completed.Result;
                        else yield return iterator.Current;
                    }
                reachedBoundary = true;
            }
            finally
            {
                if (iterator is not null)
                    try { await iterator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { RetainFailure(ref primary, cleanup); }
                if (readiness is not null)
                    try { readiness.Dispose(); }
                    catch (Exception cleanup) { RetainFailure(ref primary, cleanup); }
                if (child is not null)
                    try { await child.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { RetainFailure(ref primary, cleanup); }
                // Consumer disposal skips code after this finally. Do not lose its cleanup error.
                if (!reachedBoundary && primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
            }
            if (primary is InterruptException || primary is OperationCanceledException && cancellationToken.IsCancellationRequested)
                ExceptionDispatchInfo.Capture(primary).Throw();
            yield return new ToolStreamResultEvent(primary is not null ? Error(context, "Subagent error: " + primary.Message) with { Error = primary }
                : terminal ?? Error(context, "Subagent produced no result."));
        }

        private int RemainingDepth(Agent parent)
        {
            var stored = parent.State.Get(DepthStateKey);
            return stored is null ? _options.MaxDepth : stored.Value.ValueKind == JsonValueKind.Number && stored.Value.TryGetInt32(out var depth) ? depth : 0;
        }

        private static void ValidateChildTools(Agent child, HashSet<string> allowed)
        {
            var unexpected = child.Tools.FirstOrDefault(tool => !allowed.Contains(child.RegisteredToolSpec(tool).Name));
            if (unexpected is not null) throw new InvalidOperationException($"Child tool '{child.RegisteredToolSpec(unexpected).Name}' exceeds the delegated tool subset.");
        }

        private static ToolResultBlock FinalResult(AgentResult result, ToolContext context)
        {
            if (result.StopReason is StopReasons.Cancelled or StopReasons.ContentFiltered or "refusal" or StopReasons.MaxTurns or StopReasons.MaxTokens or StopReasons.MaxTotalTokens or StopReasons.MaxOutputTokens)
                return Error(context, $"Subagent stopped before finishing (stopReason={result.StopReason})." + (string.IsNullOrWhiteSpace(result.ToString()) ? string.Empty : "\n\nPartial output:\n" + result.ToString().TrimEnd('\n')));
            return new(context.ToolUse.ToolUseId, result.ToString());
        }

        private static void RetainFailure(ref Exception? primary, Exception cleanup)
        {
            if (primary is null) { primary = cleanup; return; }
            if (ReferenceEquals(primary, cleanup)) return;
            const string key = "Strands.CleanupErrors";
            if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
            if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
        }

        private AgentSpec Resolve(JsonElement raw, Agent parent)
        {
            ConfigurationSchema.Object(raw, "subagent input");
            if (!raw.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.String) throw new ArgumentException("task must be a string.");
            var role = ReadString(raw, "agent_type");
            if (role is not null && !_presets.ContainsKey(role)) throw new ArgumentException($"Unknown agent_type '{role}'; valid values: {string.Join(", ", _presets.Keys)}.");
            var overridingInstructions = _options.Instructions is Open or Choice && raw.TryGetProperty("instructions", out _);
            role ??= overridingInstructions ? null : _defaultPreset;
            var preset = role is not null ? _presets[role] : null;
            var instructions = _options.Instructions switch
            {
                Open when raw.TryGetProperty("instructions", out var text) && text.ValueKind == JsonValueKind.String => text.GetString(),
                Choice choice when Selected(raw, "instructions", choice) is { } option => option.Value as string,
                _ when preset is not null => preset.Instructions,
                Fixed fixedValue => fixedValue.Value as string,
                _ => null,
            };
            IReadOnlyList<string>? tools;
            if (_tools is Choice toolChoice && raw.TryGetProperty("tools", out var requested))
                tools = ReadNames(requested).Select(name => toolChoice.Options.FirstOrDefault(option => option.Name == name)?.Value as string).Where(name => name is not null).Cast<string>().ToArray();
            else if (preset?.Tools is not null)
                tools = _tools is Choice allowed ? preset.Tools.Where(name => allowed.Options.Any(option => Equals(option.Value, name))).ToArray() : preset.Tools;
            else tools = _tools switch { Choice choice => choice.Options.Select(option => (string)option.Value!).ToArray(), Fixed fixedValue => fixedValue.Value as IReadOnlyList<string>, _ => null };
            var parentNames = new HashSet<string>(parent.Tools.Select(tool => parent.RegisteredToolSpec(tool).Name).Where(name => !_remoteToolNames.Contains(name)), StringComparer.Ordinal);
            if (tools is not null) tools = Array.AsReadOnly(tools.Where(parentNames.Contains).Distinct(StringComparer.Ordinal).ToArray());
            IReadOnlyList<string>? servers = _mcpServers switch
            {
                Choice choice when raw.TryGetProperty("mcp_servers", out var selected) => ReadNames(selected).Select(name => choice.Options.FirstOrDefault(option => option.Name == name)?.Value as string).Where(name => name is not null).Cast<string>().ToArray(),
                Choice choice => choice.Options.Select(option => (string)option.Value!).ToArray(),
                Fixed fixedValue => fixedValue.Value as IReadOnlyList<string>,
                _ => null,
            };
            var availableServers = AvailableServers(parent);
            if (servers is not null) servers = Array.AsReadOnly(servers.Where(availableServers.Contains).Distinct(StringComparer.Ordinal).ToArray());
            var model = _options.Model is Choice models && Selected(raw, "model", models) is { } selectedModel ? selectedModel.Value : preset?.Model ?? (_options.Model is Fixed fixedModel ? fixedModel.Value : null);
            var context = _options.Context is Choice contexts && Selected(raw, "context", contexts) is { } selectedContext ? selectedContext.Value as string ?? "none" : preset?.Context ?? (_options.Context is Fixed fixedContext ? fixedContext.Value as string : null) ?? "none";
            var lastMessages = preset?.LastMessages;
            if (raw.TryGetProperty("last_messages", out var cap) && cap.ValueKind != JsonValueKind.Null)
            {
                if (cap.ValueKind != JsonValueKind.Number || !cap.TryGetInt32(out var number)) throw new ArgumentException("last_messages must be an integer.");
                lastMessages = number;
            }
            return new() { Task = task.GetString()!, AgentType = role, Instructions = instructions, Tools = tools, McpServers = servers, Model = model, Context = context, LastMessages = lastMessages };
        }

        private HashSet<string> AvailableServers(Agent parent)
        {
            var names = new HashSet<string>(parent.Tools.Select(tool => parent.RegisteredToolSpec(tool).Name), StringComparer.Ordinal);
            return new(_serverTools.Where(pair => pair.Value.All(tool => names.Contains(tool.Spec.Name))).Select(pair => pair.Key), StringComparer.Ordinal);
        }
        private HashSet<string> AllowedToolNames(AgentSpec spec, Agent parent)
        {
            var names = new HashSet<string>(spec.Tools ?? parent.Tools.Select(tool => parent.RegisteredToolSpec(tool).Name).Where(name => !_remoteToolNames.Contains(name)).ToArray(), StringComparer.Ordinal);
            names.UnionWith(_independentTools.Where(name => parent.Tools.Any(tool => parent.RegisteredToolSpec(tool).Name == name)));
            foreach (var server in spec.McpServers ?? AvailableServers(parent).ToArray())
                if (_serverTools.TryGetValue(server, out var tools)) names.UnionWith(tools.Select(tool => tool.Spec.Name));
            return names;
        }

        private JsonElement BuildSchema()
        {
            var properties = new Dictionary<string, object> { ["task"] = new { type = "string", description = "The self-contained task, including all context the subagent needs. It starts from a blank conversation and cannot ask follow-up questions." } };
            if (_presets.Count > 0) properties["agent_type"] = new { type = "string", @enum = _presets.Keys.ToArray(), description = "Which subagent role to use. Omit to use the default." };
            if (_options.Instructions is Open) properties["instructions"] = new { type = "string", description = "A system prompt defining the subagent's role." };
            else if (_options.Instructions is Choice instructions) properties["instructions"] = ChoiceSchema(instructions, "A system prompt defining the subagent's role.");
            if (_tools is Choice tools) properties["tools"] = ChoiceSchema(tools, "Subset of tools to grant the subagent. Fewer is safer; it cannot exceed your own.");
            if (_mcpServers is Choice servers) properties["mcp_servers"] = ChoiceSchema(servers, "Subset of MCP servers whose tools to grant the subagent. Fewer is safer; it cannot exceed your own. Omit to inherit the parent servers; an empty list grants none.");
            if (_options.Model is Choice model) properties["model"] = ChoiceSchema(model, "Which model the subagent runs on.");
            if (_options.Context is Choice context)
            {
                properties["context"] = ChoiceSchema(context, "How much of this conversation the subagent sees: none (fresh start), all (full history), or no_tools (text turns only). More context costs more tokens.");
                if (context.Options.Any(option => !Equals(option.Value, "none"))) properties["last_messages"] = new { type = "integer", description = "Optional: limit shared context to the last N messages. Omit to share all selected context." };
            }
            return JsonSerializer.SerializeToElement(new { type = "object", properties, required = new[] { "task" } });
        }

        private static object ChoiceSchema(Choice choice, string description)
        {
            var lines = choice.Options.Where(option => option.Description.Length > 0).Select(option => "- " + option.Name + ": " + option.Description).ToArray();
            if (lines.Length > 0) description += "\nOptions:\n" + string.Join("\n", lines);
            var names = choice.Options.Select(option => option.Name).ToArray();
            return choice.Multiple ? new { type = "array", items = new { type = "string", @enum = names }, description } : (object)new { type = "string", @enum = names, description };
        }
        private static Option? Selected(JsonElement raw, string field, Choice choice) => ReadString(raw, field) is { } name ? choice.Options.FirstOrDefault(option => option.Name == name) : null;
        private static string? ReadString(JsonElement raw, string field) => raw.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static IEnumerable<string> ReadNames(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! } : Array.Empty<string>();
        private static ToolResultBlock Error(ToolContext context, string message) => new(context.ToolUse.ToolUseId, message, ToolResultStatus.Error);
        private static void ValidateAxis(Authority? authority, string axis, bool allowOpen, bool allowInherit)
        {
            if (authority is null || authority is Open && !allowOpen || authority is Inherit && !allowInherit || authority is not (Fixed or Open or Inherit or Choice)) throw new ArgumentException("Unsupported authority for " + axis + ".");
            IEnumerable<object?> values = authority is Choice choice ? choice.Options.Select(option => (object?)option.Value) : authority is Fixed fixedValue ? new[] { fixedValue.Value } : Array.Empty<object?>();
            if (axis is not ("tools" or "mcp_servers") && authority is Choice { Multiple: true }) throw new ArgumentException(axis + " selects one option; multiple must be false.");
            foreach (var value in values)
                if (axis == "context")
                {
                    if (value is not null and not string) throw new ArgumentException("Context values must be strings.");
                    ValidateContext(value as string ?? "none");
                }
                else if (axis == "model") ValidateModel(value);
                else if (axis == "instructions" && value is not null and not string) throw new ArgumentException("Instruction values must be strings.");
                else if ((axis is "tools" or "mcp_servers") && (authority is Choice && value is not string || authority is Fixed && value is not null && value is not IReadOnlyList<string>)) throw new ArgumentException(axis + " choices must name a grant; Fixed values must be a list of names or null.");
        }
        private static void ValidateContext(string context)
        {
            if (context is not ("none" or "all" or "no_tools")) throw new ArgumentException("Context must be none, all, or no_tools.");
        }
        private static void ValidateModel(object? value)
        {
            if (value is not null and not IModel and not string) throw new ArgumentException("Model values must be IModel, provider/name strings, or null.");
            if (value is string name) Guard.NotBlank(name, nameof(value));
        }
    }
}
