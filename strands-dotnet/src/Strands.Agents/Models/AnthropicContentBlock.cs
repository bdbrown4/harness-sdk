using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Models;

/// <summary>Detached Anthropic text with citations, or content executed by the provider rather than a local tool.</summary>
public sealed record AnthropicContentBlock : ContentBlock
{
    /// <summary>Copies a native Messages content block, preserving opaque citation and server-tool replay fields.</summary>
    /// <param name="content">A text or supported server-tool block. Client tool calls use ToolUseBlock instead.</param>
    [JsonConstructor]
    public AnthropicContentBlock(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Anthropic content must be an object with a string type.", nameof(content));
        if (type.GetString() == "text")
        {
            if (!content.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Anthropic text content requires a string text field.", nameof(content));
            if (content.TryGetProperty("citations", out var citations) &&
                (citations.ValueKind != JsonValueKind.Array || citations.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object)))
                throw new ArgumentException("Anthropic citations must be an array of objects.", nameof(content));
        }
        else if (!IsServerContent(type.GetString()))
            throw new ArgumentException("Use correlated tool or reasoning blocks for client tool calls and thinking.", nameof(content));
        Content = content.Clone();
    }

    /// <summary>The complete native block, including encrypted fields required for replay.</summary>
    public JsonElement Content { get; }
    /// <summary>Visible text; server-tool input and output do not become display text.</summary>
    [JsonIgnore]
    public string Text => Content.GetProperty("type").GetString() == "text" ? Content.GetProperty("text").GetString()! : "";

    internal static bool IsServerContent(string? type) => type is "bash_code_execution_tool_result" or "code_execution_tool_result"
        or "container_upload" or "mcp_tool_result" or "mcp_tool_use" or "server_tool_use"
        or "text_editor_code_execution_tool_result" or "tool_search_tool_result" or "web_fetch_tool_result" or "web_search_tool_result";
}
