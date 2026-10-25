using System.Diagnostics;
using System.Text.Json;

namespace Strands.Agents.Telemetry;

internal static class TraceContent
{
    private const string DetailsEvent = "gen_ai.client.inference.operation.details";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static void Input(Activity activity, ModelRequest request, TraceConventions conventions, string? operation)
    {
        if (request.SystemPrompt is { } instructions)
        {
            var content = conventions.Content("gen_ai.system_instructions", () => conventions.Latest
                ? JsonSerializer.Serialize(new[] { new { type = "text", content = instructions } })
                : JsonSerializer.Serialize(new[] { new { text = instructions } }));
            Emit(activity, conventions.Latest ? DetailsEvent : "gen_ai.system.message",
                conventions.Latest ? "gen_ai.system_instructions" : "content", content, conventions.Latest && conventions.AttributesOnly);
            if (operation == "invoke_agent")
                activity.SetTag("system_prompt", conventions.Content("gen_ai.system_instructions", () => JsonSerializer.Serialize(instructions)));
        }
        if (request.Tools is { Count: > 0 } tools)
        {
            if (operation == "invoke_agent") activity.SetTag("gen_ai.agent.tools", JsonSerializer.Serialize(tools.Select(tool => tool.Name)));
            if (conventions.IncludeToolDefinitions)
                activity.SetTag("gen_ai.tool.definitions", JsonSerializer.Serialize(tools.ToDictionary(tool => tool.Name,
                    tool => new { name = tool.Name, description = tool.Description, inputSchema = tool.InputSchema }, StringComparer.Ordinal)));
        }
        if (conventions.Latest)
        {
            Emit(activity, DetailsEvent, "gen_ai.input.messages", conventions.Content("gen_ai.input.messages", () =>
                JsonSerializer.Serialize(request.Messages.Select(message => new { role = RoleName(message.Role), parts = Parts(message.Content) }))), conventions.AttributesOnly);
        }
        else
        {
            foreach (var message in request.Messages)
            {
                var name = message.Role == Role.User && message.Content.Any(block => block is ToolResultBlock)
                    ? "gen_ai.tool.message" : "gen_ai." + RoleName(message.Role) + ".message";
                Emit(activity, name, "content", conventions.Content("gen_ai.input.messages", () => JsonSerializer.Serialize(message.Content.Select(LegacyBlock))), false);
            }
        }
    }

    internal static void Output(Activity activity, Message message, string? stopReason, TraceConventions conventions, string? operation)
    {
        var isAgent = operation == "invoke_agent";
        var reason = stopReason ?? (isAgent ? StopReasons.EndTurn : "unknown");
        var text = string.Join("\n", message.Content.Select(VisibleText).OfType<string>());
        if (conventions.Latest)
        {
            Emit(activity, DetailsEvent, "gen_ai.output.messages", conventions.Content("gen_ai.output.messages", () => JsonSerializer.Serialize(new[]
            {
                new { role = isAgent ? "assistant" : RoleName(message.Role),
                    parts = isAgent ? new object[] { new { type = "text", content = text } } : Parts(message.Content), finish_reason = reason },
            })), conventions.AttributesOnly);
        }
        else
        {
            var tags = new ActivityTagsCollection
            {
                { "finish_reason", reason },
                { "message", conventions.Content("gen_ai.output.messages", () => isAgent ? text : JsonSerializer.Serialize(message.Content.Select(StableBlock).Where(value => value is not null))) },
            };
            activity.AddEvent(new ActivityEvent("gen_ai.choice", tags: tags));
        }
    }

    internal static void ToolInput(Activity activity, ToolUseBlock tool, TraceConventions conventions)
    {
        if (conventions.Latest)
        {
            activity.SetTag("gen_ai.tool.call.arguments", conventions.Content("gen_ai.tool.call.arguments", () => tool.Input.GetRawText()));
            Emit(activity, DetailsEvent, "gen_ai.input.messages", conventions.Content("gen_ai.input.messages", () => JsonSerializer.Serialize(new[]
            {
                new { role = "tool", parts = new[] { new { type = "tool_call", name = tool.Name, id = tool.ToolUseId, arguments = tool.Input } } },
            })), conventions.AttributesOnly);
        }
        else
        {
            activity.AddEvent(new ActivityEvent("gen_ai.tool.message", tags: new ActivityTagsCollection
            {
                { "role", "tool" }, { "id", tool.ToolUseId },
                { "content", conventions.Content("gen_ai.input.messages", () => tool.Input.GetRawText()) },
            }));
        }
    }

    internal static void ToolOutput(Activity activity, ToolResultBlock result, TraceConventions conventions)
    {
        var status = result.Status == ToolResultStatus.Error ? "error" : "success";
        activity.SetTag("gen_ai.tool.status", status);
        if (conventions.Latest)
        {
            if (result.Status == ToolResultStatus.Success)
                activity.SetTag("gen_ai.tool.call.result", conventions.Content("gen_ai.tool.call.result", () => JsonSerializer.Serialize(ResultContent(result))));
            Emit(activity, DetailsEvent, "gen_ai.output.messages", conventions.Content("gen_ai.output.messages", () => JsonSerializer.Serialize(new[]
            {
                new { role = "tool", parts = new[] { new { type = "tool_call_response", id = result.ToolUseId, response = ResultContent(result) } } },
            })), conventions.AttributesOnly);
        }
        else
        {
            activity.AddEvent(new ActivityEvent("gen_ai.choice", tags: new ActivityTagsCollection
            {
                { "id", result.ToolUseId }, { "message", conventions.Content("gen_ai.output.messages", () => JsonSerializer.Serialize(ResultContent(result))) },
            }));
        }
    }

    private static object[] Parts(IReadOnlyList<ContentBlock> blocks) => blocks.Select(block => VisibleText(block) is { } text
        ? (object)new { type = "text", content = text } : block switch
        {
            ToolUseBlock tool => new { type = "tool_call", name = tool.Name, id = tool.ToolUseId, arguments = tool.Input },
            ToolResultBlock result => new { type = "tool_call_response", id = result.ToolUseId, response = ResultContent(result) },
            _ => new { type = BlockName(block) },
        }).ToArray();

    private static object? StableBlock(ContentBlock block) => VisibleText(block) is { } text
        ? (object)new { text } : block switch
        {
            ToolUseBlock tool => new { type = "toolUse", name = tool.Name, toolUseId = tool.ToolUseId, input = tool.Input },
            ToolResultBlock result => new { type = "toolResult", toolUseId = result.ToolUseId, content = ResultContent(result) },
            _ => null,
        };

    private static string? VisibleText(ContentBlock block) => block switch
    {
        TextBlock text => text.Text,
        CitationsBlock citations => citations.Text,
        Models.ResponsesContentBlock response => response.Text,
        Models.GoogleContentBlock google when google.Part.TryGetProperty("text", out _)
            && (!google.Part.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True) => google.Text,
        Models.AnthropicContentBlock anthropic when anthropic.Content.GetProperty("type").GetString() == "text" => anthropic.Text,
        _ => null,
    };

    private static object LegacyBlock(ContentBlock block)
    {
        if (block is TextBlock text) return new { type = "textBlock", text = text.Text };
        if (block is ToolUseBlock tool) return new { type = "toolUseBlock", toolUseId = tool.ToolUseId, name = tool.Name, input = tool.Input };
        if (block is ToolResultBlock result) return new { type = "toolResultBlock", toolUseId = result.ToolUseId, content = ResultContent(result), status = result.Status == ToolResultStatus.Error ? "error" : "success" };
        var fields = JsonSerializer.SerializeToElement(block, block.GetType(), JsonOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        fields["type"] = BlockName(block);
        return fields;
    }

    private static object[] ResultContent(ToolResultBlock result) => result.ContentBlocks.Count == 0 && !result.HasExplicitContent
        ? new object[] { new { text = result.Content } } : result.ContentBlocks.Select(ResultPart).ToArray();

    private static object ResultPart(ContentBlock block) => block switch
    {
        TextBlock text => (object)new { text = text.Text },
        JsonBlock json => new { json = json.Json },
        _ => LegacyBlock(block),
    };

    private static string BlockName(ContentBlock block)
    {
        var name = block.GetType().Name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string RoleName(Role role) => role.ToString().ToLowerInvariant();
    private static void Emit(Activity activity, string name, string key, string value, bool attributesOnly)
    {
        if (attributesOnly) activity.SetTag(key, value);
        else activity.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection { { key, value } }));
    }
}
