using System.Text;
using System.Text.Json;

namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    private MultiAgentInput ToolInput(MultiAgentInput original, string target, MultiAgentState state, string? handoff)
    {
        var text = new StringBuilder("Context:\n");
        if (!string.IsNullOrEmpty(handoff)) text.Append("Handoff Message: ").Append(handoff).Append("\n\n");
        text.Append(original.IsTextInput ? "User Request: " + original.Text : "User Request: Multi-modal task").Append("\n\n");
        if (state.Results.Count > 0) text.Append("Previous agents who worked on this: ").Append(string.Join(" → ", state.Results.Select(result => result.NodeId))).Append("\n\n");
        var shared = SharedContext.GetAll();
        if (shared.Count > 0)
        {
            text.Append("Shared knowledge from previous agents:\n");
            foreach (var pair in shared)
            {
                if (pair.Value.Count == 0) continue;
                var rendered = _options.ContextTextProfile == SwarmContextTextProfile.PythonRepr
                    ? AgentResultRendering.PythonValue(JsonSerializer.SerializeToElement(pair.Value))
                    : JsonSerializer.Serialize(pair.Value);
                text.Append("• ").Append(pair.Key).Append(": ").Append(rendered).Append('\n');
            }
            text.Append('\n');
        }
        var others = _options.Nodes.Where(node => node.Id != target).ToArray();
        if (others.Length > 0)
        {
            text.Append("Other agents available for collaboration:\n");
            foreach (var node in others)
            {
                text.Append("Agent name: ").Append(node.Id).Append('.');
                if (!string.IsNullOrEmpty(node.Description)) text.Append(" Agent description: ").Append(node.Description);
                text.Append('\n');
            }
            text.Append('\n');
        }
        text.Append("You have access to swarm coordination tools if you need help from other agents. If you don't hand off to another agent, the swarm will consider the task complete.\n\n");
        var content = new List<ContentBlock> { new TextBlock(text.ToString()) };
        if (!original.IsTextInput) content.AddRange(original.Content);
        return new(content);
    }

    internal static bool IsHandoffTool(ITool tool) => tool is HandoffTool;

    private sealed class HandoffTool(Swarm swarm, ToolTurn turn) : IRichTool
    {
        internal const string Name = "handoff_to_agent";
        public ToolSpec Spec { get; } = new(Name, "Transfer control to another agent in the swarm for specialized help.", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { agent_name = new { type = "string" }, message = new { type = "string" }, context = new { type = new[] { "object", "null" } } },
            required = new[] { "agent_name", "message" },
            additionalProperties = false
        }));
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = input.GetProperty("agent_name").GetString()!;
            if (!swarm.Nodes.ContainsKey(target)) return new(new ToolResultBlock(context.ToolUse.ToolUseId, $"Error: Agent '{target}' not found in swarm.", ToolResultStatus.Error));
            var message = input.GetProperty("message").GetString()!;
            var decision = new Dictionary<string, object?> { ["agentId"] = target, ["message"] = message };
            if (input.TryGetProperty("context", out var contribution) && contribution.ValueKind == JsonValueKind.Object)
            {
                if (contribution.EnumerateObject().Any(property => string.IsNullOrWhiteSpace(property.Name)))
                    return new(new ToolResultBlock(context.ToolUse.ToolUseId, "Error in handoff: shared context keys must be nonblank.", ToolResultStatus.Error));
                decision["context"] = contribution;
            }
            var result = new ToolResultBlock(context.ToolUse.ToolUseId, $"Handing off to {target}: {message}");
            turn.Record(context.Agent, new(context.ToolUse, Name, result), JsonSerializer.SerializeToElement(decision));
            return new(result);
        }
    }

}
