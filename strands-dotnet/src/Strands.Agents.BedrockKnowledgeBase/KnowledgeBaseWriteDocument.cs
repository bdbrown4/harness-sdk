using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;

namespace Strands.Agents.BedrockKnowledgeBase;

internal static class KnowledgeBaseWriteDocument
{
    internal static bool Blank(string content, BedrockKnowledgeBaseProfile profile) => content.All(value =>
        value is >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000'
        || (profile == BedrockKnowledgeBaseProfile.TypeScript ? value == '\ufeff' : value is >= '\u001c' and <= '\u001f' or '\u0085'));

    internal static string NewId(BedrockKnowledgeBaseProfile profile)
    {
        if (profile == BedrockKnowledgeBaseProfile.Python) return Guid.NewGuid().ToString("D");
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 5; index >= 0; index--) { bytes[index] = (byte)timestamp; timestamp >>= 8; }
        bytes[6] = (byte)((bytes[6] & 15) | 0x70); bytes[8] = (byte)((bytes[8] & 63) | 0x80);
        var encoded = string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        return encoded.Substring(0, 8) + "-" + encoded.Substring(8, 4) + "-" + encoded.Substring(12, 4) + "-" + encoded.Substring(16, 4) + "-" + encoded.Substring(20, 12);
    }

    internal static KnowledgeBaseDocument Custom(string id, string content, JsonElement? metadata, string? scope, string key, IReadOnlyList<DocumentAccessControlEntry>? acl, BedrockKnowledgeBaseProfile profile, Action<string, bool> dropped)
    {
        var attributes = Attributes(metadata, scope, key, dropped);
        var includeAcl = acl is not null && (profile == BedrockKnowledgeBaseProfile.TypeScript || acl.Count != 0);
        if (includeAcl && attributes.Count == 0) throw new ArgumentException("A CUSTOM inline AccessControlList requires at least one metadata attribute. Set Scope or supply supported metadata.", nameof(metadata));
        return new()
        {
            Content = new() { DataSourceType = ContentDataSourceType.CUSTOM, Custom = new() { CustomDocumentIdentifier = new() { Id = id }, SourceType = CustomSourceType.IN_LINE, InlineContent = new() { Type = InlineContentType.TEXT, TextContent = new() { Data = content } } } },
            Metadata = attributes.Count == 0 ? null : new DocumentMetadata
            {
                Type = MetadataSourceType.IN_LINE_ATTRIBUTE,
                InlineAttributes = attributes,
                AccessControlList = includeAcl ? acl!.Select(entry => new DocumentAccessControlEntry { Name = entry.Name, Type = entry.Type, Access = entry.Access }).ToList() : null
            }
        };
    }
    internal static List<MetadataAttribute> Attributes(JsonElement? metadata, string? scope, string key, Action<string, bool> dropped)
    {
        var attributes = new List<MetadataAttribute>();
        if (!string.IsNullOrEmpty(scope)) attributes.Add(new() { Key = key, Value = new() { Type = MetadataValueType.STRING, StringValue = scope } });
        if (metadata is { } json)
            foreach (var entry in json.EnumerateObject())
            {
                if (!string.IsNullOrEmpty(scope) && entry.Name == key) { dropped(entry.Name, true); continue; }
                var value = Attribute(entry.Value);
                if (value is null) dropped(entry.Name, false);
                else attributes.Add(new() { Key = entry.Name, Value = value });
            }
        return attributes;
    }

    private static MetadataAttributeValue? Attribute(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new() { Type = MetadataValueType.STRING, StringValue = value.GetString() },
        JsonValueKind.True or JsonValueKind.False => new() { Type = MetadataValueType.BOOLEAN, BooleanValue = value.GetBoolean() },
        JsonValueKind.Number => new() { Type = MetadataValueType.NUMBER, NumberValue = value.GetDouble() },
        JsonValueKind.Array when value.GetArrayLength() > 0 && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) => new() { Type = MetadataValueType.STRING_LIST, StringListValue = value.EnumerateArray().Select(item => item.GetString()!).ToList() },
        _ => null
    };
}
