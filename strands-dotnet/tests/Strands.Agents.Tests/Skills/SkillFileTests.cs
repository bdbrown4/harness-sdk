using Strands.Agents.Files;
using Strands.Agents.Skills;

namespace Strands.Agents.Tests.Skills;

public sealed class SkillFileTests
{
    [Fact]
    public async Task DirectFileLoadingUsesSelectedFilesystemAndValidatesTheDirectoryName()
    {
        var files = new CatalogFiles();

        var directory = await Skill.FromFileAsync("/selected/pdf-processing", files, strict: true);
        var directFile = await Skill.FromFileAsync("/selected/pdf-processing/SKILL.md", files, strict: true);

        Assert.Equal("pdf-processing", directory.Name);
        Assert.Equal(directory.Instructions, directFile.Instructions);
        Assert.Equal("/selected/pdf-processing", directFile.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => Skill.FromFileAsync("/selected/wrong-name/SKILL.md", files, strict: true));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Skill.FromFileAsync("/missing", files));
    }

    [Fact]
    public async Task DirectoryLoadingIsSortedAndSkipsMalformedChildrenWithoutSwallowingCallerCancellation()
    {
        var files = new CatalogFiles();

        var skills = await Skill.FromDirectoryAsync("/selected", files, strict: true);

        Assert.Equal(new[] { "alpha", "pdf-processing" }, skills.Select(skill => skill.Name));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Skill.FromDirectoryAsync("/missing", files));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Skill.FromDirectoryAsync("/selected", files, cancellationToken: canceled.Token));
    }

    private sealed class CatalogFiles : IReadOnlyFileSystem
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return path switch
            {
                "/selected/alpha/SKILL.md" => new("---\nname: alpha\ndescription: The first skill\n---\nAlpha instructions"),
                "/selected/pdf-processing/SKILL.md" or "/selected/wrong-name/SKILL.md" => new("---\nname: pdf-processing\ndescription: Process PDFs\n---\nPDF instructions"),
                "/selected/malformed/SKILL.md" => new("---\nname: malformed\n---\nMissing description"),
                _ => throw new FileNotFoundException("The selected filesystem has no such file.", path),
            };
        }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileEntry> files = path switch
            {
                "/selected" => [new("pdf-processing", true), new("malformed", true), new("alpha", true), new("not-a-skill", true)],
                "/selected/alpha" or "/selected/pdf-processing" or "/selected/malformed" => [new("SKILL.md", false)],
                "/selected/not-a-skill" => [],
                _ => throw new DirectoryNotFoundException("The selected filesystem has no such directory."),
            };
            return new(files);
        }
    }
}
