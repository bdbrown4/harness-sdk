using Native = Amazon.BedrockRuntime.Model;

namespace Strands.Agents.Bedrock;

internal static class BedrockCitations
{
    internal static Native.CitationsContentBlock Map(CitationsBlock block) => new()
    {
        Content = block.Content.Select(value => new Native.CitationGeneratedContent { Text = value.Text }).ToList(),
        Citations = block.Citations.Select(value => new Native.Citation
        {
            Location = Map(value.Location),
            Source = value.Source,
            Title = value.Title,
            SourceContent = value.SourceContent.Select(text => new Native.CitationSourceContent { Text = text.Text }).ToList()
        }).ToList()
    };

    private static Native.CitationLocation Map(CitationLocation location) => location switch
    {
        DocumentCharCitationLocation value => new() { DocumentChar = new() { DocumentIndex = value.DocumentIndex, Start = value.Start, End = value.End } },
        DocumentPageCitationLocation value => new() { DocumentPage = new() { DocumentIndex = value.DocumentIndex, Start = value.Start, End = value.End } },
        DocumentChunkCitationLocation value => new() { DocumentChunk = new() { DocumentIndex = value.DocumentIndex, Start = value.Start, End = value.End } },
        SearchResultCitationLocation value => new() { SearchResultLocation = new() { SearchResultIndex = value.SearchResultIndex, Start = value.Start, End = value.End } },
        WebCitationLocation value => new() { Web = new() { Url = value.Url, Domain = value.Domain } },
        _ => throw new ModelException("Bedrock cannot represent this citation location.")
    };

    internal static Citation? Read(Native.CitationsDelta delta)
    {
        if (delta.Location is not { } source) return null;
        var members = (source.DocumentChar is not null ? 1 : 0) + (source.DocumentPage is not null ? 1 : 0)
            + (source.DocumentChunk is not null ? 1 : 0) + (source.SearchResultLocation is not null ? 1 : 0) + (source.Web is not null ? 1 : 0);
        if (members == 0) return null;
        if (members != 1) throw new ModelException("Bedrock citation locations must contain exactly one location variant.");
        CitationLocation location;
        if (source.DocumentChar is { } characters) location = new DocumentCharCitationLocation(characters.DocumentIndex ?? 0, characters.Start ?? 0, characters.End ?? 0);
        else if (source.DocumentPage is { } pages) location = new DocumentPageCitationLocation(pages.DocumentIndex ?? 0, pages.Start ?? 0, pages.End ?? 0);
        else if (source.DocumentChunk is { } chunks) location = new DocumentChunkCitationLocation(chunks.DocumentIndex ?? 0, chunks.Start ?? 0, chunks.End ?? 0);
        else if (source.SearchResultLocation is { } search) location = new SearchResultCitationLocation(search.SearchResultIndex ?? 0, search.Start ?? 0, search.End ?? 0);
        else location = new WebCitationLocation(source.Web!.Url ?? "", source.Web.Domain);
        return new(location, delta.Source ?? "", delta.SourceContent?.Where(value => value?.Text is not null).Select(value => new CitationText(value.Text)).ToArray() ?? [], delta.Title ?? "");
    }
}
