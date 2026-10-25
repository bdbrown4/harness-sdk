using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents.Conversation;

internal static class ClassicToolResultTruncator
{
    private const int PreserveUnits = 200;
    private const int TypeScriptThreshold = 450;
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static Message? Truncate(Message message, ClassicToolResultTruncationPolicy policy)
    {
        var changed = false;
        var blocks = message.Content.Select(block =>
        {
            if (block is not ToolResultBlock result) return block;
            var items = result.ContentBlocks.Count == 0 ? new ContentBlock[] { new TextBlock(result.Content) } : result.ContentBlocks;
            var replacement = items.Select(item => policy == ClassicToolResultTruncationPolicy.TypeScript ? TypeScript(item) : Python(item)).ToArray();
            if (items.SequenceEqual(replacement)) return block;
            changed = true;
            return result with
            {
                Content = string.Concat(replacement.OfType<TextBlock>().Select(text => text.Text)),
                ContentBlocks = Array.AsReadOnly(replacement.Select(Message.CloneBlock).ToArray()),
                StructuredContent = result.StructuredContent?.Clone()
            };
        }).ToArray();
        return changed ? new(message.Role, blocks, message.TrackingId, message.Metadata) : null;
    }

    private static ContentBlock Python(ContentBlock item)
    {
        if (item is ImageBlock image)
        {
            var size = image.Data.Length == 0 ? 0 : Convert.FromBase64String(image.Data).Length;
            return new TextBlock($"[image: {Format(image.MediaType)}, {Number(size)} bytes]");
        }
        if (item is not TextBlock text || text.Text.Contains("... [truncated:")) return item;
        var boundaries = new List<int>();
        for (var index = 0; index < text.Text.Length; index++)
        {
            boundaries.Add(index);
            if (char.IsHighSurrogate(text.Text[index]) && index + 1 < text.Text.Length && char.IsLowSurrogate(text.Text[index + 1])) index++;
        }
        return boundaries.Count <= 400 ? item : new TextBlock(text.Text.Substring(0, boundaries[200]) + "...\n\n... [truncated: " + Number(boundaries.Count - 400) + " chars removed] ...\n\n..." + text.Text.Substring(boundaries[boundaries.Count - 200]));
    }

    private static ContentBlock TypeScript(ContentBlock item)
    {
        switch (item)
        {
            case ImageBlock image:
                return new TextBlock("[image: " + Format(image.MediaType) + ", " + Source(image.Data, image.Location is not null, image.Url is not null) + "]");
            case VideoBlock video:
                return new TextBlock("[video: " + Format(video.MediaType) + ", " + Source(video.Data, video.Location is not null) + "]");
            case DocumentBlock { Text: { } text } document:
                return TruncateText(text) is { } truncated ? document with { Text = truncated } : item;
            case DocumentBlock { Content: { } content } document:
                {
                    var replacement = content.Select(block => TruncateText(block.Text) is { } truncated ? new TextBlock(truncated) : block).ToArray();
                    return content.SequenceEqual(replacement) ? item : document with { Content = Array.AsReadOnly(replacement) };
                }
            case DocumentBlock document:
                return new TextBlock("[document: " + (document.Name ?? "document") + ", " + Format(document.MediaType) + ", " + Source(document.Data, document.Location is not null) + "]");
            case JsonBlock json:
                {
                    var length = JsonSerializer.Serialize(json.Json, CompactJson).Length;
                    return length > TypeScriptThreshold ? new TextBlock("[json: " + Number(length) + " chars]") : item;
                }
            case TextBlock text:
                return TruncateText(text.Text) is { } preview ? new TextBlock(preview) : item;
            default:
                return item;
        }
    }

    private static string? TruncateText(string text)
    {
        if (text.Length <= TypeScriptThreshold) return null;
        var head = PreserveUnits;
        var tail = text.Length - PreserveUnits;
        // Preserve valid Unicode rather than reproducing JavaScript slice's possible lone surrogate.
        if (char.IsHighSurrogate(text[head - 1]) && char.IsLowSurrogate(text[head])) head--;
        if (char.IsLowSurrogate(text[tail]) && char.IsHighSurrogate(text[tail - 1])) tail++;
        return text.Substring(0, head) + "\n<truncated chars=\"" + Number(tail - head) + "\"/>\n" + text.Substring(tail);
    }

    private static string Source(string data, bool s3, bool url = false) => url ? "source: url" : s3 ? "source: s3" : "source: bytes, " + Number(data.Length == 0 ? 0 : Convert.FromBase64String(data).Length) + " bytes";
    private static string Format(string mediaType) => mediaType.Substring(mediaType.LastIndexOf('/') + 1);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
