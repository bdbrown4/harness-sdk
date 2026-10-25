using System.Text.Json;
using Strands.Agents.Files;

namespace Strands.Agents.Skills;

/// <summary>Metadata and instructions for one progressively disclosed agent skill.</summary>
public sealed record SkillOptions
{
    /// <summary>The skill identifier.</summary>
    public required string Name { get; init; }
    /// <summary>Guidance for deciding when to activate the skill.</summary>
    public required string Description { get; init; }
    /// <summary>The full Markdown instructions revealed on activation.</summary>
    public string Instructions { get; init; } = string.Empty;
    /// <summary>The source directory, or null for a skill loaded without a local path.</summary>
    public string? Path { get; init; }
    /// <summary>Advisory tool names. This metadata does not change the agent's tool authority.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    /// <summary>Additional YAML metadata represented as a JSON object.</summary>
    public JsonElement? Metadata { get; init; }
    /// <summary>The optional license identifier.</summary>
    public string? License { get; init; }
    /// <summary>Optional runtime or environment compatibility guidance.</summary>
    public string? Compatibility { get; init; }
}

/// <summary>An immutable skill with owned instructions and metadata.</summary>
public sealed class Skill
{
    /// <summary>Copies a native skill definition.</summary>
    public Skill(SkillOptions options)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.Name, nameof(options.Name));
        Guard.NotNull(options.Description, nameof(options.Description));
        Guard.NotNull(options.Instructions, nameof(options.Instructions));
        if (options.Metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Skill metadata must be a JSON object.", nameof(options));
        Name = options.Name;
        Description = options.Description;
        Instructions = options.Instructions;
        Path = options.Path;
        AllowedTools = options.AllowedTools is null ? null : Array.AsReadOnly(options.AllowedTools.ToArray());
        Metadata = options.Metadata?.Clone() ?? JsonSerializer.SerializeToElement(new { });
        License = options.License;
        Compatibility = options.Compatibility;
    }
    /// <summary>The skill identifier.</summary>
    public string Name { get; }
    /// <summary>Guidance for deciding when to activate the skill.</summary>
    public string Description { get; }
    /// <summary>The complete Markdown instructions.</summary>
    public string Instructions { get; }
    /// <summary>The optional source directory.</summary>
    public string? Path { get; }
    /// <summary>Advisory tool names, independent of granted tool authority.</summary>
    public IReadOnlyList<string>? AllowedTools { get; }
    /// <summary>The owned additional metadata object.</summary>
    public JsonElement Metadata { get; }
    /// <summary>The optional license identifier.</summary>
    public string? License { get; }
    /// <summary>The optional compatibility guidance.</summary>
    public string? Compatibility { get; }

    /// <summary>Parses YAML frontmatter and Markdown instructions from a SKILL.md document.</summary>
    public static Skill FromContent(string content, bool strict = false, string? path = null)
        => SkillDocument.Parse(content, strict, path);

    /// <summary>Downloads a raw UTF-8 SKILL.md document from HTTPS with a 30 second deadline and 5 MiB limit.</summary>
    /// <param name="url">An absolute HTTPS URL pointing to the document.</param>
    /// <param name="httpClient">An optional caller-owned HTTP client.</param>
    /// <param name="strict">Whether cosmetic validation issues reject the skill.</param>
    /// <param name="cancellationToken">Cancels the request and document read.</param>
    /// <returns>The parsed skill, without a local filesystem path.</returns>
    public static Task<Skill> FromUrlAsync(string url, HttpClient? httpClient = null, bool strict = false, CancellationToken cancellationToken = default)
        => SkillHttpsSource.LoadAsync(url, httpClient, strict, cancellationToken);

    /// <summary>Loads a skill directory or SKILL.md file through the supplied filesystem, or the host filesystem when omitted.</summary>
    public static Task<Skill> FromFileAsync(string path, IReadOnlyFileSystem? fileSystem = null, bool strict = false, CancellationToken cancellationToken = default)
        => SkillFileSources.LoadFileAsync(path, fileSystem, strict, cancellationToken);

    /// <summary>Loads sorted skill subdirectories, skipping invalid children; a missing parent directory fails.</summary>
    public static Task<IReadOnlyList<Skill>> FromDirectoryAsync(string path, IReadOnlyFileSystem? fileSystem = null, bool strict = false, CancellationToken cancellationToken = default)
        => SkillFileSources.LoadDirectoryAsync(path, fileSystem, strict, cancellationToken);
}
