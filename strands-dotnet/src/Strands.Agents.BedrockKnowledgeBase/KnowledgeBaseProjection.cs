using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Amazon.BedrockAgentRuntime.Model;
using Strands.Agents.Memory;

namespace Strands.Agents.BedrockKnowledgeBase;

internal static class KnowledgeBaseProjection
{
    internal static IReadOnlyList<MemoryEntry> Entries(IReadOnlyList<KnowledgeBaseRetrievalResult>? results, BedrockKnowledgeBaseProfile profile, double? minimum, double? maximum)
    {
        var entries = new List<MemoryEntry>();
        foreach (var result in results ?? [])
        {
            if (result is null) throw new InvalidDataException("Knowledge-base results cannot contain null.");
            if (result.Score is { } score && (minimum is { } min && !(score >= min) || maximum is { } max && !(score <= max))) continue;
            var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (result.Metadata is not null)
                foreach (var item in result.Metadata)
                {
                    var value = JsonSerializer.SerializeToElement(item.Value);
                    metadata[item.Key] = profile == BedrockKnowledgeBaseProfile.TypeScript ? NumberWrapper(value) : value;
                }
            if (result.Location is { } location)
            {
                var value = Location(location);
                if (profile == BedrockKnowledgeBaseProfile.TypeScript || value.Count != 0)
                    metadata[profile == BedrockKnowledgeBaseProfile.TypeScript ? "_sourceLocation" : "_source_location"] = JsonSerializer.SerializeToElement(value);
            }
            if (result.Score is { } relevance)
                metadata[profile == BedrockKnowledgeBaseProfile.TypeScript ? "_relevanceScore" : "_relevance_score"] = JsonSerializer.SerializeToElement(relevance);
            entries.Add(new(result.Content?.Text ?? "", metadata: JsonSerializer.SerializeToElement(metadata)));
        }
        return entries.AsReadOnly();
    }

    private static Dictionary<string, object?> Location(RetrievalResultLocation location)
    {
        var value = new Dictionary<string, object?>();
        if (location.Type is { } type) value["type"] = type.Value;
        Add("confluenceLocation", "url", location.ConfluenceLocation?.Url, location.ConfluenceLocation is not null);
        Add("customDocumentLocation", "id", location.CustomDocumentLocation?.Id, location.CustomDocumentLocation is not null);
        Add("googleDriveLocation", "url", location.GoogleDriveLocation?.Url, location.GoogleDriveLocation is not null);
        Add("kendraDocumentLocation", "uri", location.KendraDocumentLocation?.Uri, location.KendraDocumentLocation is not null);
        Add("oneDriveLocation", "url", location.OneDriveLocation?.Url, location.OneDriveLocation is not null);
        Add("s3Location", "uri", location.S3Location?.Uri, location.S3Location is not null);
        Add("salesforceLocation", "url", location.SalesforceLocation?.Url, location.SalesforceLocation is not null);
        Add("sharePointLocation", "url", location.SharePointLocation?.Url, location.SharePointLocation is not null);
        Add("sqlLocation", "query", location.SqlLocation?.Query, location.SqlLocation is not null);
        Add("webLocation", "url", location.WebLocation?.Url, location.WebLocation is not null);
        return value;
        void Add(string key, string member, string? text, bool present)
        {
            if (!present) return;
            var nested = new Dictionary<string, object?>();
            if (text is not null) nested[member] = text;
            value[key] = nested;
        }
    }

    private static JsonElement NumberWrapper(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("string", out var encoded) || encoded.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "bigDecimal") return value;
        var text = encoded.GetString()!;
        var start = 0; var end = text.Length;
        while (start < end && WhiteSpace(text[start])) start++;
        while (end > start && WhiteSpace(text[end - 1])) end--;
        if (start == end) return value;
        text = text.Substring(start, end - start);
        double number;
        if (text.Length > 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            var radix = text[1] is 'x' or 'X' ? 16 : text[1] is 'b' or 'B' ? 2 : 8;
            var integer = BigInteger.Zero;
            for (var index = 2; index < text.Length; index++)
            {
                var c = text[index];
                var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0 || digit >= radix) return value;
                integer = integer * radix + digit;
            }
            number = RoundedInteger(integer);
        }
        else if (!DecimalLiteral(text) || !double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number)) return value;
        return double.IsNaN(number) || double.IsInfinity(number) ? value : JsonSerializer.SerializeToElement(number);
    }
    private static double RoundedInteger(BigInteger integer)
    {
        var bytes = integer.ToByteArray();
        var last = bytes.Length - 1;
        while (last > 0 && bytes[last] == 0) last--;
        if (last >= 128) return double.PositiveInfinity;
        var bits = last * 8;
        for (var top = bytes[last]; top != 0; top >>= 1) bits++;
        if (bits <= 53) return (double)(ulong)integer;
        var shift = bits - 53;
        var leading = integer >> shift;
        var remainder = integer - (leading << shift);
        var half = BigInteger.One << (shift - 1);
        if (remainder > half || remainder == half && !leading.IsEven) leading++;
        return (double)(ulong)leading * Math.Pow(2, shift);
    }
    private static bool DecimalLiteral(string text)
    {
        var index = text[0] is '+' or '-' ? 1 : 0;
        var digits = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9') { digits++; index++; }
        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && text[index] is >= '0' and <= '9') { digits++; index++; }
        }
        if (digits == 0) return false;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            var first = index;
            while (index < text.Length && text[index] is >= '0' and <= '9') index++;
            if (index == first) return false;
        }
        return index == text.Length;
    }
    private static bool WhiteSpace(char value) => value is >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
}
