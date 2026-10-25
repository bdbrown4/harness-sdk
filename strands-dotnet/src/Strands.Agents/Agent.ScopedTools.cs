using System.Collections.ObjectModel;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private static InvocationOptions CopyEphemeralTools(InvocationOptions options)
    {
        Guard.NotNull(options.EphemeralTools, nameof(options.EphemeralTools));
        return options with { EphemeralTools = Array.AsReadOnly(options.EphemeralTools.ToArray()) };
    }

    private IReadOnlyDictionary<string, ITool> BindEphemeralTools(InvocationOptions options)
    {
        var tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in options.EphemeralTools)
        {
            if (tool is null) throw new ArgumentException("Invocation-local tools cannot contain null.", nameof(options));
            if (_options.ToolFilter?.Invoke(tool) == false) continue;
            var specification = tool.Spec ?? throw new ArgumentException("Invocation-local tools require a specification.", nameof(options));
            if (_tools.ContainsKey(specification.Name) || tools.ContainsKey(specification.Name) || options.StructuredOutputTool?.Spec.Name == specification.Name)
                throw new ArgumentException($"Duplicate invocation-local tool name: {specification.Name}.", nameof(options));
            ValidateNormalizedToolName(specification.Name, _tools.Keys.Concat(tools.Keys).Concat(options.StructuredOutputTool is { } output ? new[] { output.Spec.Name } : Array.Empty<string>()));
            _ = ToolSchema.Compile(specification.InputSchema);
            tools.Add(specification.Name, tool);
        }
        return new ReadOnlyDictionary<string, ITool>(tools);
    }
}
