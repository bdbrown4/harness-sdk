using System.Text.Json;

namespace Strands.Agents.A2A;

internal static class A2AParts
{
    internal static IReadOnlyList<object> ToParts(IReadOnlyList<ContentBlock> content, A2AContentMode mode)
    {
        var parts = new List<object>();
        foreach (var block in content)
        {
            if (block is TextBlock text) { parts.Add(new { kind = "text", text = text.Text }); continue; }
            if (mode == A2AContentMode.TextOnly) continue;
            MediaContent.RejectS3(block, "A2A");
            switch (block)
            {
                case CitationsBlock citations: parts.Add(new { kind = "text", text = citations.Text }); break;
                case JsonBlock json when json.Json.ValueKind == JsonValueKind.Object: parts.Add(new { kind = "data", data = json.Json }); break;
                case ImageBlock { Url: { } url } image: parts.Add(new { kind = "file", file = new { uri = url.AbsoluteUri, mimeType = image.MediaType } }); break;
                case ImageBlock image: parts.Add(File(image.MediaType, MediaContent.InlineData(image, "A2A"))); break;
                case AudioBlock audio: parts.Add(File(audio.MediaType, MediaContent.InlineData(audio, "A2A"))); break;
                case VideoBlock video: parts.Add(File(video.MediaType, MediaContent.InlineData(video, "A2A"))); break;
                case DocumentBlock document: parts.Add(File(document.MediaType, MediaContent.InlineData(document, "A2A"), document.Name)); break;
                case ResourceLinkBlock resource: parts.Add(new { kind = "file", file = new { uri = resource.Uri, name = resource.Name, mimeType = resource.MediaType } }); break;
                default: throw new NotSupportedException("This content cannot be sent as an A2A part: " + block.GetType().Name + ".");
            }
        }
        return parts;
    }
    private static object File(string mediaType, string data, string? name = null)
    {
        Guard.NotNullOrWhiteSpace(mediaType, nameof(mediaType));
        try { _ = Convert.FromBase64String(data); }
        catch (FormatException error) { throw new ArgumentException("A2A byte files require valid base64 content.", nameof(data), error); }
        return new { kind = "file", file = new { mimeType = mediaType, bytes = data, name } };
    }
    internal static IReadOnlyList<ContentBlock> FromParts(IEnumerable<JsonElement> parts, A2AContentMode mode)
    {
        if (mode == A2AContentMode.TextOnly)
        {
            var texts = parts.Where(part => A2AJson.RequiredString(part, "kind") == "text")
                .Select(part => A2AJson.OptionalString(part, "text") ?? throw new A2AProtocolException("An A2A TextPart requires text.")).ToArray();
            return texts.Length == 0 ? [] : [new TextBlock(string.Join("\n", texts))];
        }
        var content = new List<ContentBlock>();
        foreach (var part in parts)
        {
            var kind = A2AJson.RequiredString(part, "kind");
            if (kind == "text")
            {
                var text = A2AJson.OptionalString(part, "text") ?? throw new A2AProtocolException("An A2A TextPart requires text.");
                content.Add(new TextBlock(text)); continue;
            }
            if (kind == "data") { content.Add(new JsonBlock(A2AJson.RequiredObject(part, "data").Clone())); continue; }
            if (kind != "file") throw new NotSupportedException("Unsupported A2A part kind: " + kind + ". Inspect A2AStreamUpdateEvent for raw protocol data.");
            var file = A2AJson.RequiredObject(part, "file");
            var uri = A2AJson.OptionalString(file, "uri");
            var bytes = A2AJson.OptionalString(file, "bytes");
            var mediaType = A2AJson.OptionalString(file, "mimeType") ?? "application/octet-stream";
            var name = A2AJson.OptionalString(file, "name");
            if ((uri is null) == (bytes is null)) throw new A2AProtocolException("An A2A file requires exactly one of uri and bytes.");
            if (uri is not null) { content.Add(new ResourceLinkBlock(uri, name ?? "file", mediaType)); continue; }
            try { _ = Convert.FromBase64String(bytes!); }
            catch (FormatException error) { throw new A2AProtocolException("An A2A file contains malformed base64 bytes.", error); }
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) content.Add(new ImageBlock(mediaType, bytes!));
            else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) content.Add(new AudioBlock(mediaType, bytes!));
            else if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) content.Add(new VideoBlock(mediaType, bytes!));
            else content.Add(new DocumentBlock(mediaType, bytes!, name));
        }
        return content;
    }
}
