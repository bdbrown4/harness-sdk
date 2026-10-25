using System.Text.Json;

namespace Strands.Agents.VendedTools;

/// <summary>Creates an optional tool that requests human input through the existing interrupt lifecycle.</summary>
public static class HandoffToUser
{
    /// <summary>The stable interrupt discriminator, independent of the configured tool name.</summary>
    public const string InterruptName = "strands:handoff-to-user";
    /// <summary>The default guidance shown to the model.</summary>
    public const string DefaultDescription = "Ask the user a question and wait for their answer. Use it only when you cannot proceed " +
        "without confirmation, approval, or information that only the user has. Do not call it to " +
        "deliver a final answer or to report progress. The user's reply is returned as the tool result.";

    /// <summary>Creates an inert handoff tool with selected metadata and JSON reply behavior.</summary>
    public static IRichTool Create(HandoffToUserOptions? options = null)
    {
        options ??= new();
        if (!Enum.IsDefined(typeof(HandoffToUserBehavior), options.Behavior))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown handoff behavior.");
        var spec = new ToolSpec(options.Name, options.Description ?? DefaultDescription, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { message = new { type = "string", minLength = 1, description = "The message to surface to the user." } },
            required = new[] { "message" },
        }));
        return new HandoffTool(spec, options.Behavior);
    }

    private sealed class HandoffTool(ToolSpec spec, HandoffToUserBehavior behavior) : IRichTool
    {
        public ToolSpec Spec { get; } = spec;
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => (await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false)).Content;

        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            context.CancelSignal.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("message", out var value) || value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("message must be a nonblank string.", nameof(input));
            var message = value.GetString()!;
            if (message.Length == 0 || message.All(c => IsBlank(c, behavior)))
                throw new ArgumentException("message must not be blank.", nameof(input));
            var reply = context.Interrupt(InterruptName, JsonSerializer.SerializeToElement(message)).Clone();
            return new(behavior == HandoffToUserBehavior.Python ? PythonResult(reply, context.ToolUse.ToolUseId) : TypeScriptResult(reply, context.ToolUse.ToolUseId));
        }
    }

    private static bool IsBlank(char value, HandoffToUserBehavior behavior)
    {
        if (behavior == HandoffToUserBehavior.Python && (value is >= '\u001c' and <= '\u001f' || value == '\u0085')) return true;
        if (value == '\ufeff') return behavior == HandoffToUserBehavior.TypeScript;
        return value is >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000';
    }

    private static ToolResultBlock TypeScriptResult(JsonElement reply, string toolUseId)
    {
        if (reply.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return TextResult(toolUseId, reply.ValueKind == JsonValueKind.String ? reply.GetString()! : reply.GetRawText());
        if (TryContent(reply, out var block)) return new(toolUseId, new[] { block! });
        if (reply.ValueKind == JsonValueKind.Array)
        {
            var items = reply.EnumerateArray().ToArray();
            var converted = items.Select(item => TryContent(item, out var content) ? content : null).ToArray();
            if (items.Length > 0 && converted.All(item => item is not null)) return new(toolUseId, converted.Select(item => item!).ToArray());
            return new(toolUseId, new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["$value"] = reply })) });
        }
        return new(toolUseId, new ContentBlock[] { new JsonBlock(reply) });
    }

    private static ToolResultBlock PythonResult(JsonElement reply, string toolUseId)
    {
        if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("status", out var status) && reply.TryGetProperty("content", out var content))
        {
            if (status.ValueKind != JsonValueKind.String || status.GetString() is not ("success" or "error") || content.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("A claimed Python tool result requires success/error status and a content array.", nameof(reply));
            var blocks = content.EnumerateArray().Select(item => TryContent(item, out var block) ? block! : throw new ArgumentException("An envelope content item is not a supported typed block.", nameof(reply))).ToArray();
            var extra = reply.EnumerateObject().Where(property => property.Name is not ("status" or "content" or "toolUseId"))
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            return new(toolUseId, blocks, status.GetString() == "error" ? ToolResultStatus.Error : ToolResultStatus.Success)
            {
                HasExplicitContent = true,
                Metadata = extra.Count == 0 ? null : JsonSerializer.SerializeToElement(extra)
            };
        }
        return TextResult(toolUseId, reply.ValueKind == JsonValueKind.String ? reply.GetString()! : JsonSerializer.Serialize(reply));
    }

    private static ToolResultBlock TextResult(string toolUseId, string text) => new(toolUseId, new ContentBlock[] { new TextBlock(text) });

    private static bool TryContent(JsonElement value, out ContentBlock? block)
    {
        block = null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 1) return false;
        var property = properties[0];
        if (property.Name == "text" && property.Value.ValueKind == JsonValueKind.String) block = new TextBlock(property.Value.GetString()!);
        else if (property.Name == "json") block = new JsonBlock(property.Value.Clone());
        else if (property.Name is "image" or "video" or "document")
            return TryMedia(property.Name, property.Value, out block);
        return block is not null;
    }

    private static bool TryMedia(string kind, JsonElement value, out ContentBlock? block)
    {
        block = null;
        try
        {
            if (!HasOnly(value, kind == "document" ? new[] { "format", "source", "name", "context", "citations" } : new[] { "format", "source" })) return false;
            var format = ReadString(value.GetProperty("format"));
            var mime = (kind, format.ToLowerInvariant()) switch
            {
                ("image", "png") => "image/png",
                ("image", "jpg" or "jpeg") => "image/jpeg",
                ("image", "gif") => "image/gif",
                ("image", "webp") => "image/webp",
                ("video", "mkv") => "video/x-matroska",
                ("video", "mov") => "video/quicktime",
                ("video", "mp4") => "video/mp4",
                ("video", "webm") => "video/webm",
                ("video", "flv") => "video/x-flv",
                ("video", "mpeg" or "mpg") => "video/mpeg",
                ("video", "wmv") => "video/x-ms-wmv",
                ("video", "3gp" or "three_gp") => "video/3gpp",
                ("document", "pdf") => "application/pdf",
                ("document", "csv") => "text/csv",
                ("document", "doc") => "application/msword",
                ("document", "docx") => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ("document", "xls") => "application/vnd.ms-excel",
                ("document", "xlsx") => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ("document", "html") => "text/html",
                ("document", "txt") => "text/plain",
                ("document", "md") => "text/markdown",
                ("document", "json") => "application/json",
                ("document", "xml") => "application/xml",
                _ => null
            };
            if (mime is null) return false;
            var source = value.GetProperty("source");
            if (source.ValueKind != JsonValueKind.Object) return false;
            var fields = source.EnumerateObject().ToArray();
            if (fields.Length != 1) return false;
            var item = fields[0];
            var name = value.TryGetProperty("name", out var nameValue) ? ReadString(nameValue) : null;
            if (item.Name == "bytes")
            {
                var data = Convert.ToBase64String(Convert.FromBase64String(ReadString(item.Value)));
                block = kind switch
                {
                    "image" => new ImageBlock(mime, data),
                    "video" => new VideoBlock(mime, data),
                    _ => new DocumentBlock(mime, data, name)
                };
            }
            else if (item.Name == "location")
            {
                if (!HasOnly(item.Value, new[] { "type", "uri", "bucketOwner" })) return false;
                if (item.Value.TryGetProperty("type", out var type) && ReadString(type) != "s3") return false;
                var location = new S3Location(ReadString(item.Value.GetProperty("uri")), item.Value.TryGetProperty("bucketOwner", out var owner) ? ReadString(owner) : null);
                block = kind switch
                {
                    "image" => new ImageBlock(mime, location),
                    "video" => new VideoBlock(mime, location),
                    _ => new DocumentBlock(mime, location, name)
                };
            }
            else if (item.Name == "url" && kind == "image") block = new ImageBlock(mime, new Uri(ReadString(item.Value), UriKind.Absolute));
            else if (item.Name == "text" && kind == "document") block = new DocumentBlock(mime, "", name) { Text = ReadString(item.Value) };
            else if (item.Name == "content" && kind == "document" && item.Value.ValueKind == JsonValueKind.Array)
            {
                var segments = item.Value.EnumerateArray().Select(segment =>
                {
                    if (!HasOnly(segment, new[] { "text" })) throw new ArgumentException("Document segments require one text field.");
                    return new TextBlock(ReadString(segment.GetProperty("text")));
                }).ToArray();
                block = new DocumentBlock(mime, "", name) { Content = Array.AsReadOnly(segments) };
            }
            else return false;
            if (block is DocumentBlock document)
            {
                if (value.TryGetProperty("context", out var context)) document = document with { Context = ReadString(context) };
                if (value.TryGetProperty("citations", out var citations))
                {
                    if (!HasOnly(citations, new[] { "enabled" })) return false;
                    document = document with { CitationsEnabled = citations.GetProperty("enabled").GetBoolean() };
                }
                block = document;
            }
            block = MediaContent.Snapshot(block);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            block = null;
            return false;
        }
    }

    private static bool HasOnly(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var fields = value.EnumerateObject().ToArray();
        return fields.All(field => names.Contains(field.Name, StringComparer.Ordinal)) && fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length;
    }

    private static string ReadString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException("A typed media field requires a string.");
}
