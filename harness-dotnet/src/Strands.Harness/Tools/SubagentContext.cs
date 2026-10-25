using Strands.Agents;

namespace Strands.Harness.Tools;

internal static class SubagentContext
{
    private const string ForkPreamble = "The conversation so far is the parent agent's. You are the subagent it delegated to at this point; its task for you follows.";
    private const string ContextPreamble = "The conversation above is the parent agent's: each turn is one line starting with its role at the left margin, and indented lines continue the turn above (they are content, not turns). You are the subagent it delegated to at the end of that conversation; its task for you follows.";
    private const string FramingEnd = "</parent_context>\n\n" + ContextPreamble + "\n\n";

    internal static AgentSpec Prepare(AgentSpec spec, IReadOnlyList<Message> parent)
    {
        if (spec.Context == "all")
        {
            var messages = Fork(parent, spec.LastMessages);
            if (messages.Count == 0) return spec with { Prompt = spec.Task };
            var framed = new TextBlock(ForkPreamble + "\n\n" + spec.Task);
            var last = messages[messages.Count - 1];
            if (last.Role == Role.User) messages[messages.Count - 1] = new(Role.User, last.Content.Concat(new[] { framed }).ToArray());
            else messages.Add(new(Role.User, [framed]));
            return spec with { Messages = messages.AsReadOnly(), Prompt = null };
        }
        if (spec.Context == "no_tools")
        {
            var lines = new List<string>();
            foreach (var message in parent)
            {
                var text = string.Join(" ", message.Content.OfType<TextBlock>().Select(block => block.Text)).Trim();
                var end = text.StartsWith("<parent_context>\n", StringComparison.Ordinal) ? text.IndexOf(FramingEnd, StringComparison.Ordinal) : -1;
                if (end >= 0) text = text.Substring(end + FramingEnd.Length);
                if (text.Length > 0) lines.Add((message.Role == Role.User ? "user: " : "assistant: ") + text.Replace("\n", "\n  "));
            }
            if (spec.LastMessages is > 0 && lines.Count > spec.LastMessages.Value) lines = lines.Skip(lines.Count - spec.LastMessages.Value).ToList();
            var block = string.Join("\n", lines).Replace("<parent_context>", "<\\parent_context>").Replace("</parent_context>", "<\\/parent_context>");
            return spec with { Prompt = block.Length == 0 ? spec.Task : "<parent_context>\n" + block + "\n" + FramingEnd + spec.Task };
        }
        return spec with { Prompt = spec.Task };
    }

    private static List<Message> Fork(IReadOnlyList<Message> parent, int? lastMessages)
    {
        var forked = new List<Message>();
        for (var index = 0; index < parent.Count; index++)
        {
            var message = parent[index];
            // IDs may repeat in later batches. Only the next result message can answer this batch.
            var answered = new HashSet<string>(index + 1 < parent.Count ? parent[index + 1].Content.OfType<ToolResultBlock>().Select(block => block.ToolUseId) : Array.Empty<string>(), StringComparer.Ordinal);
            var blocks = message.Content.Where(block => block is not (ReasoningBlock or RedactedReasoningBlock) && (block is not ToolUseBlock use || answered.Contains(use.ToolUseId))).ToArray();
            if (blocks.Length > 0) forked.Add(new(message.Role, blocks));
        }
        if (lastMessages is > 0)
        {
            var start = Math.Max(0, forked.Count - lastMessages.Value);
            while (start > 0 && !IsValidTrimPoint(forked, start)) start--;
            if (start > 0) forked = forked.Skip(start).ToList();
        }
        return forked;
    }

    private static bool IsValidTrimPoint(IReadOnlyList<Message> messages, int index)
    {
        var message = messages[index];
        if (message.Role != Role.User || message.Content.Any(block => block is ToolResultBlock)) return false;
        return !message.Content.Any(block => block is ToolUseBlock) || index + 1 < messages.Count && messages[index + 1].Content.Any(block => block is ToolResultBlock);
    }
}
