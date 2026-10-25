namespace Strands.Agents.Sandbox;

/// <summary>A binary artifact produced by a selected sandbox.</summary>
public sealed class OutputFile
{
    private readonly byte[] _content;
    /// <summary>Copies artifact data with an optional MIME type. Names and MIME types remain literal data.</summary>
    public OutputFile(string name, byte[] content, string mimeType = "application/octet-stream")
    {
        Guard.NotNull(name, nameof(name));
        Guard.NotNull(content, nameof(content));
        Guard.NotNull(mimeType, nameof(mimeType));
        Name = name;
        _content = (byte[])content.Clone();
        MimeType = mimeType;
    }
    /// <summary>The literal artifact name, without filesystem authority.</summary>
    public string Name { get; }
    /// <summary>An independent copy of the binary artifact content.</summary>
    public byte[] Content => (byte[])_content.Clone();
    /// <summary>The supplied MIME type.</summary>
    public string MimeType { get; }
}
