using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>The location of evidence referenced by generated text.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DocumentCharCitationLocation), "documentChar")]
[JsonDerivedType(typeof(DocumentPageCitationLocation), "documentPage")]
[JsonDerivedType(typeof(DocumentChunkCitationLocation), "documentChunk")]
[JsonDerivedType(typeof(SearchResultCitationLocation), "searchResult")]
[JsonDerivedType(typeof(WebCitationLocation), "web")]
public abstract record CitationLocation;
/// <summary>A character range within a source document.</summary>
public sealed record DocumentCharCitationLocation(int DocumentIndex, int Start, int End) : CitationLocation;
/// <summary>A page range within a source document.</summary>
public sealed record DocumentPageCitationLocation(int DocumentIndex, int Start, int End) : CitationLocation;
/// <summary>A content-block range within a source document.</summary>
public sealed record DocumentChunkCitationLocation(int DocumentIndex, int Start, int End) : CitationLocation;
/// <summary>A content-block range within a search result.</summary>
public sealed record SearchResultCitationLocation(int SearchResultIndex, int Start, int End) : CitationLocation;
/// <summary>A web source and its optional domain.</summary>
public sealed record WebCitationLocation(string Url, string? Domain = null) : CitationLocation;
/// <summary>A source excerpt or a portion of generated text.</summary>
public sealed record CitationText(string Text);

/// <summary>Links generated content to a source location and optional quoted evidence.</summary>
public sealed record Citation
{
    /// <summary>Copies the source excerpts into an immutable citation.</summary>
    [JsonConstructor]
    public Citation(CitationLocation location, string source = "", IReadOnlyList<CitationText>? sourceContent = null, string title = "")
    {
        Guard.NotNull(location, nameof(location));
        Guard.NotNull(source, nameof(source));
        Guard.NotNull(title, nameof(title));
        Location = location;
        Source = source;
        SourceContent = CopyText(sourceContent ?? []);
        Title = title;
    }
    /// <summary>The source range or web address.</summary>
    public CitationLocation Location { get; }
    /// <summary>The provider source identifier, file identifier, or URL.</summary>
    public string Source { get; }
    /// <summary>Detached source excerpts referenced by this citation.</summary>
    public IReadOnlyList<CitationText> SourceContent { get; }
    /// <summary>The source title, or an empty string when absent.</summary>
    public string Title { get; }
    internal static IReadOnlyList<CitationText> CopyText(IReadOnlyList<CitationText> values)
    {
        Guard.NotNull(values, nameof(values));
        return Array.AsReadOnly(values.Select(value => value?.Text is not null ? value : throw new ArgumentException("Citation text cannot be null.", nameof(values))).ToArray());
    }
    internal static IReadOnlyList<Citation> Copy(IReadOnlyList<Citation> values)
    {
        Guard.NotNull(values, nameof(values));
        return Array.AsReadOnly(values.Select(value => value ?? throw new ArgumentException("Citations cannot contain null.", nameof(values))).ToArray());
    }
}

/// <summary>Generated text accompanied by source citations.</summary>
public sealed record CitationsBlock : ContentBlock
{
    /// <summary>Copies generated text, citations, and optional native replay data.</summary>
    [JsonConstructor]
    public CitationsBlock(IReadOnlyList<Citation> citations, IReadOnlyList<CitationText> content, JsonElement? providerMetadata = null)
    {
        if (providerMetadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Citation provider metadata must be a JSON object.", nameof(providerMetadata));
        Citations = Citation.Copy(citations);
        Content = Citation.CopyText(content);
        ProviderMetadata = providerMetadata?.Clone();
    }
    /// <summary>Source citations associated with the generated text.</summary>
    public IReadOnlyList<Citation> Citations { get; }
    /// <summary>The ordered generated text portions.</summary>
    public IReadOnlyList<CitationText> Content { get; }
    /// <summary>Optional provider-keyed replay fields, kept separate from normalized citation locations.</summary>
    public JsonElement? ProviderMetadata { get; }
    /// <summary>Concatenated generated display text.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Content.Select(value => value.Text));
}
