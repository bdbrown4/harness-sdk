using System.Text.Json;

namespace Strands.Agents.Models;

internal static class AnthropicCitations
{
    internal static Citation? Format(JsonElement value)
    {
        var type = ProviderHttp.RequiredString(value, "type");
        CitationLocation location;
        var source = "";
        switch (type)
        {
            case "web_search_result_location":
                source = ProviderHttp.OptionalString(value, "url") ?? "";
                var domain = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;
                location = new WebCitationLocation(source, domain);
                break;
            case "search_result_location":
                location = new SearchResultCitationLocation(Index(value, "search_result_index"), Index(value, "start_block_index"), Index(value, "end_block_index"));
                source = ProviderHttp.OptionalString(value, "source") ?? "";
                break;
            case "char_location":
                location = new DocumentCharCitationLocation(Index(value, "document_index"), Index(value, "start_char_index"), Index(value, "end_char_index"));
                source = ProviderHttp.OptionalString(value, "file_id") ?? "";
                break;
            case "page_location":
                location = new DocumentPageCitationLocation(Index(value, "document_index"), Index(value, "start_page_number"), Index(value, "end_page_number"));
                source = ProviderHttp.OptionalString(value, "file_id") ?? "";
                break;
            case "content_block_location":
                location = new DocumentChunkCitationLocation(Index(value, "document_index"), Index(value, "start_block_index"), Index(value, "end_block_index"));
                source = ProviderHttp.OptionalString(value, "file_id") ?? "";
                break;
            default: return null;
        }
        var excerpt = ProviderHttp.OptionalString(value, "cited_text");
        return new(location, source, string.IsNullOrEmpty(excerpt) ? [] : [new(excerpt!)],
            ProviderHttp.OptionalString(value, "title") ?? ProviderHttp.OptionalString(value, "document_title") ?? "");
    }

    private static int Index(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var index)) return 0;
        if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var result) || result < 0)
            throw new ModelException($"Anthropic citation field '{name}' must be a nonnegative integer.");
        return result;
    }
}
