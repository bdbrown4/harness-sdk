using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Strands.Agents.SchemaImplementation;
using ProtocolContent = ModelContextProtocol.Protocol.ContentBlock;

namespace Strands.Agents.Mcp;

/// <summary>Adapts an MCP server tool to the native Strands tool contract without changing its server-side name.</summary>
public sealed class McpTool : IRichTool
{
    private readonly McpClient _client;
    private readonly JsonSchema? _outputSchema;
    private readonly IProgress<ProgressNotificationValue>? _progress;

    internal McpTool(McpClient client, JsonElement definition, string agentName, IProgress<ProgressNotificationValue>? progress, JsonSchema? outputSchema)
    {
        _client = client;
        _outputSchema = outputSchema;
        _progress = progress;
        Definition = definition.Clone();
        ServerName = definition.GetProperty("name").GetString() ?? throw new InvalidDataException("MCP tool name is null.");
        var description = definition.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        OutputSchema = definition.TryGetProperty("outputSchema", out var output) && output.ValueKind != JsonValueKind.Null ? output.Clone() : null;
        Annotations = definition.TryGetProperty("annotations", out var annotations) && annotations.ValueKind != JsonValueKind.Null ? annotations.Clone() : null;
        Spec = new ToolSpec(agentName, string.IsNullOrWhiteSpace(description) ? "Tool which performs " + ServerName : description!, definition.GetProperty("inputSchema"))
        {
            OutputSchema = OutputSchema,
            Annotations = Annotations
        };
    }

    /// <inheritdoc/>
    public ToolSpec Spec { get; }
    /// <summary>The original server-side name used for calls and discovery filtering.</summary>
    public string ServerName { get; }
    /// <summary>The complete immutable JSON tool definition, retaining protocol and vendor fields.</summary>
    public JsonElement Definition { get; }
    /// <summary>The optional output schema, validated against successful structured results.</summary>
    public JsonElement? OutputSchema { get; }
    /// <summary>Opaque, untrusted annotation hints. These do not authorize tool execution.</summary>
    public JsonElement? Annotations { get; }

    /// <inheritdoc/>
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var result = await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false);
        if (result.Status == ToolResultStatus.Error) throw new InvalidOperationException(result.Content, result.Error);
        if (result.ContentBlocks.Any(block => block is not TextBlock and not JsonBlock))
            throw new NotSupportedException("This MCP result contains rich content. InvokeResultAsync retains its media and resource blocks.");
        return result.Content;
    }

    /// <inheritdoc/>
    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        try
        {
            var result = await _client.CallBoundToolAsync(ServerName, input, _outputSchema, _progress, cancellationToken).ConfigureAwait(false);
            return MapResult(result, context.ToolUse.ToolUseId);
        }
        catch (UrlElicitationRequiredException error) when (CanFormatUrlElicitation(error))
        {
            return FormatUrlElicitation(error, context.ToolUse.ToolUseId);
        }
    }

    internal static bool CanFormatUrlElicitation(UrlElicitationRequiredException error) =>
        error.Elicitations.Count > 0 &&
        error.Elicitations.All(value => value is { Mode: "url", ElicitationId: not null, Url: not null, Message: not null });

    internal static ToolResultBlock FormatUrlElicitation(UrlElicitationRequiredException error, string toolUseId)
    {
        var elicitations = new JsonArray();
        foreach (var elicitation in error.Elicitations)
        {
            var value = new JsonObject
            {
                ["mode"] = elicitation.Mode,
                ["elicitationId"] = elicitation.ElicitationId,
                ["url"] = elicitation.Url,
                ["message"] = elicitation.Message
            };
            if (elicitation.Meta is { } metadata) value["_meta"] = metadata.DeepClone();
            elicitations.Add(value);
        }
        return new(toolUseId,
            "MCP Elicitation required: [" + error.Message + "] with data " + elicitations.ToJsonString(), ToolResultStatus.Error)
        { Error = error };
    }

    internal static ToolResultBlock MapResult(CallToolResult result, string toolUseId) =>
        new(toolUseId, result.Content.Select(MapContent).ToArray(), result.IsError == true ? ToolResultStatus.Error : ToolResultStatus.Success, result.StructuredContent)
        { Metadata = result.Meta is { Count: > 0 } metadata ? JsonSerializer.SerializeToElement(metadata) : null };

    private static ContentBlock MapContent(ProtocolContent block) => block switch
    {
        TextContentBlock text => new TextBlock(text.Text),
        ImageContentBlock image => new ImageBlock(image.MimeType, Encoding.UTF8.GetString(image.Data.ToArray())),
        AudioContentBlock audio => new AudioBlock(audio.MimeType, Encoding.UTF8.GetString(audio.Data.ToArray())),
        EmbeddedResourceBlock { Resource: TextResourceContents text } => new TextBlock(text.Text),
        EmbeddedResourceBlock { Resource: BlobResourceContents blob } when blob.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true =>
            new ImageBlock(blob.MimeType!, Encoding.UTF8.GetString(blob.Blob.ToArray())),
        EmbeddedResourceBlock { Resource: BlobResourceContents blob } when blob.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true =>
            new AudioBlock(blob.MimeType!, Encoding.UTF8.GetString(blob.Blob.ToArray())),
        ModelContextProtocol.Protocol.ResourceLinkBlock link => new ResourceLinkBlock(link.Uri, link.Name, link.MimeType, link.Description),
        _ => throw new NotSupportedException("MCP result content type '" + block.Type + "' cannot be represented by this Strands adapter. Use McpClient.CallToolAsync to inspect the complete protocol result.")
    };
}
