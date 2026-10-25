namespace Strands.Harness;

/// <summary>Selects the text paging input and numbering contract.</summary>
public enum ReadPagingProfile
{
    /// <summary>Native nullable integer literals and exact integer slice arithmetic.</summary>
    NativeIntegral,
    /// <summary>Explicit Python/Pydantic 2.5 input coercion and integer line slicing.</summary>
    Python,
    /// <summary>Explicit TypeScript numeric input, array slicing and fractional line labels.</summary>
    TypeScript
}

/// <summary>File read presentation and optional media allocation policy.</summary>
public sealed record ReadConfig
{
    /// <summary>The paging input and numbering contract. Defaults to NativeIntegral.</summary>
    public ReadPagingProfile PagingProfile { get; init; } = ReadPagingProfile.NativeIntegral;

    /// <summary>Whether to return image/document blocks. Null uses the harness model policy; a standalone file tool assumes media support.</summary>
    public bool? Media { get; init; }
    /// <summary>An optional positive byte limit for image/document reads. Null matches upstream's unbounded media read; text keeps its independent sandbox limit.</summary>
    /// <remarks>An explicit limit requires the selected filesystem to implement IBoundedFileReader. The tool never falls back to an unbounded read or truncates a media file.</remarks>
    public int? MaximumMediaBytes { get; init; }
    internal void Validate()
    {
        if (!Enum.IsDefined(typeof(ReadPagingProfile), PagingProfile)) throw new ArgumentOutOfRangeException(nameof(PagingProfile));
        if (MaximumMediaBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumMediaBytes), "The media byte limit must be positive.");
    }
}
