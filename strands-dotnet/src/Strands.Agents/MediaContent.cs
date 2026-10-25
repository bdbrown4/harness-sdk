using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Strands.Agents;

/// <summary>An immutable S3 media reference. Providers decide whether remote sources are supported.</summary>
public sealed record S3Location
{
    /// <summary>Creates a reference using the Bedrock S3 URI and optional twelve-digit account identifier contract.</summary>
    [JsonConstructor]
    public S3Location(string uri, string? bucketOwner = null)
    {
        if (uri is null || uri.Length > 1024 || !Regex.IsMatch(uri, @"\As3://[a-z0-9][.\-a-z0-9]{1,61}[a-z0-9](/.*)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("An S3 source requires a valid s3:// bucket URI of at most 1024 characters.", nameof(uri));
        if (bucketOwner is not null && (bucketOwner.Length != 12 || bucketOwner.Any(character => character is < '0' or > '9')))
            throw new ArgumentException("The bucket owner must contain exactly twelve ASCII digits.", nameof(bucketOwner));
        Uri = uri;
        BucketOwner = bucketOwner;
    }
    /// <summary>The original S3 object URI, preserved without URL normalization.</summary>
    public string Uri { get; }
    /// <summary>The optional AWS account owning the bucket.</summary>
    public string? BucketOwner { get; }
}

/// <summary>Video content supplied as base64 bytes or an S3 object.</summary>
[method: JsonConstructor]
public sealed record VideoBlock(string MediaType, string Data) : ContentBlock
{
    /// <summary>An S3 source, mutually exclusive with nonempty Data.</summary>
    public S3Location? Location { get; init; }
    /// <summary>Creates video referencing an S3 object.</summary>
    public VideoBlock(string mediaType, S3Location location) : this(mediaType, "") => Location = location ?? throw new ArgumentNullException(nameof(location));
}

/// <summary>The role of selectively guarded text in a provider guardrail evaluation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GuardQualifier>))]
public enum GuardQualifier
{
    /// <summary>Source material used for grounding checks.</summary>
    [JsonStringEnumMemberName("grounding_source")]
    GroundingSource,
    /// <summary>The user query used for relevance checks.</summary>
    [JsonStringEnumMemberName("query")]
    Query,
    /// <summary>Content evaluated by configured guardrail policies.</summary>
    [JsonStringEnumMemberName("guard_content")]
    GuardContent
}

/// <summary>Exactly one text or PNG/JPEG byte image selected for provider guardrail evaluation.</summary>
public sealed record GuardContentBlock : ContentBlock
{
    /// <summary>Creates selected text or image content, copying text qualifiers.</summary>
    [JsonConstructor]
    public GuardContentBlock(string? text, ImageBlock? image = null, IReadOnlyList<GuardQualifier>? qualifiers = null)
    {
        if ((text is null) == (image is null)) throw new ArgumentException("Guard content requires exactly one text or image source.");
        if (image is not null && (image.Location is not null || image.Url is not null || image.MediaType is not ("image/png" or "image/jpeg")))
            throw new ArgumentException("Guard images require inline PNG or JPEG bytes.", nameof(image));
        if (image is not null) MediaContent.Snapshot(image);
        if (qualifiers?.Any(value => !Enum.IsDefined(typeof(GuardQualifier), value)) == true) throw new ArgumentException("Unknown guard qualifier.", nameof(qualifiers));
        if (image is not null && qualifiers is { Count: > 0 }) throw new ArgumentException("Guard qualifiers apply only to text.", nameof(qualifiers));
        Text = text;
        Image = image;
        Qualifiers = Array.AsReadOnly(qualifiers?.ToArray() ?? []);
    }
    /// <summary>Creates selected image content.</summary>
    public GuardContentBlock(ImageBlock image) : this(null, image ?? throw new ArgumentNullException(nameof(image))) { }
    /// <summary>Selected text, or null for an image.</summary>
    public string? Text { get; }
    /// <summary>Selected inline PNG/JPEG image, or null for text.</summary>
    public ImageBlock? Image { get; }
    /// <summary>Copied text qualifiers, in caller order.</summary>
    public IReadOnlyList<GuardQualifier> Qualifiers { get; }
}

internal static class MediaContent
{
    internal static ContentBlock Snapshot(ContentBlock block)
    {
        var (data, location, url) = Source(block);
        if (data is null) throw new ArgumentException("Inline media data cannot be null.", nameof(block));
        var additional = block is DocumentBlock document ? (document.Text is null ? 0 : 1) + (document.Content is null ? 0 : 1) : 0;
        if ((data.Length > 0 ? 1 : 0) + (location is null ? 0 : 1) + (url is null ? 0 : 1) + additional > 1)
            throw new ArgumentException("Media requires a single byte, S3, or URL source.", nameof(block));
        if (url is not null && (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https")))
            throw new ArgumentException("Remote images require an absolute HTTP or HTTPS URL.", nameof(block));
        if (block is DocumentBlock { Content: { } content } segmented)
        {
            if (content.Any(item => item?.Text is null)) throw new ArgumentException("Document segments require nonnull text blocks.", nameof(block));
            return segmented with { Content = Array.AsReadOnly(content.ToArray()) };
        }
        return block;
    }

    internal static string InlineData(ContentBlock block, string provider)
    {
        Snapshot(block);
        var (data, location, url) = Source(block);
        if (location is not null || url is not null) throw new NotSupportedException(provider + " requires inline bytes for this media content.");
        if (block is DocumentBlock { Text: { } text }) return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (block is DocumentBlock { Content: not null }) throw new NotSupportedException(provider + " cannot encode segmented document content as a single byte source.");
        return data;
    }

    internal static void RejectS3(ContentBlock block, string provider)
    {
        if (block is ImageBlock { Location: not null } or AudioBlock { Location: not null } or VideoBlock { Location: not null } or DocumentBlock { Location: not null })
            throw new NotSupportedException(provider + " does not support S3 media sources.");
        if (block is ToolResultBlock result)
            foreach (var content in result.ContentBlocks) RejectS3(content, provider);
    }

    internal static void RejectGuardSelection(ContentBlock block, string provider)
    {
        if (block is GuardContentBlock) throw new NotSupportedException(provider + " cannot retain selective guardrail semantics.");
        if (block is ToolResultBlock result)
            foreach (var content in result.ContentBlocks) RejectGuardSelection(content, provider);
    }

    internal static IEnumerable<ContentBlock> ExpandDocuments(IEnumerable<ContentBlock> blocks, bool plainText = false)
    {
        foreach (var block in blocks)
        {
            if (block is DocumentBlock { Content: { } segments })
            {
                foreach (var segment in segments) yield return segment;
            }
            else if (plainText && block is DocumentBlock { Text: { } text }) yield return new TextBlock(text);
            else yield return block;
        }
    }

    private static (string Data, S3Location? Location, Uri? Url) Source(ContentBlock block) => block switch
    {
        ImageBlock image => (image.Data, image.Location, image.Url),
        AudioBlock audio => (audio.Data, audio.Location, null),
        VideoBlock video => (video.Data, video.Location, null),
        DocumentBlock document => (document.Data, document.Location, null),
        _ => throw new ArgumentException("Expected media content.", nameof(block))
    };
}
