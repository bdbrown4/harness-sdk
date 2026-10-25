using Strands.Agents.Files;

namespace Strands.Agents.Skills;

internal static class SkillFileSources
{
    internal static async Task<Skill> LoadFileAsync(string path, IReadOnlyFileSystem? files, bool strict, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        if (files is null) { path = Path.GetFullPath(path); files = new LocalFileSystem(); }
        var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var name = path.Substring(index + 1);
        string directory;
        string document;
        if (string.Equals(name, "skill.md", StringComparison.OrdinalIgnoreCase))
        {
            directory = Parent(path); document = path;
        }
        else
        {
            directory = path;
            var entries = await files.ListFilesAsync(path, cancellationToken).ConfigureAwait(false);
            var filename = SkillFilename(entries) ?? throw new FileNotFoundException("The skill directory has no SKILL.md or skill.md file.", path);
            document = Join(path, filename);
        }
        var content = await files.ReadTextAsync(document, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Skill.FromContent(content, strict, directory);
    }

    internal static async Task<IReadOnlyList<Skill>> LoadDirectoryAsync(string path, IReadOnlyFileSystem? files, bool strict, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        if (files is null) { path = Path.GetFullPath(path); files = new LocalFileSystem(); }
        var entries = await files.ListFilesAsync(path, cancellationToken).ConfigureAwait(false);
        var skills = new List<Skill>();
        foreach (var entry in entries.Where(entry => entry.IsDirectory).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = Join(path, entry.Name);
            try { skills.Add(await LoadFileAsync(child, files, strict, cancellationToken).ConfigureAwait(false)); }
            catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested)) { Warn(child, error); }
        }
        return skills.AsReadOnly();
    }

    internal static async Task<IReadOnlyList<Skill>> DiscoverAsync(string path, IReadOnlyFileSystem files, bool strict, CancellationToken cancellationToken)
    {
        var skills = new List<Skill>();
        IReadOnlyList<FileEntry> entries;
        try { entries = await files.ListFilesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (path.EndsWith("skill.md", StringComparison.OrdinalIgnoreCase)) await LoadAsync(Parent(path), path).ConfigureAwait(false);
            else Warn(path, error);
            return skills.AsReadOnly();
        }
        if (SkillFilename(entries) is { } filename) await LoadAsync(path, Join(path, filename)).ConfigureAwait(false);
        else
            foreach (var entry in entries.Where(entry => entry.IsDirectory).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = Join(path, entry.Name);
                IReadOnlyList<FileEntry> children;
                try { children = await files.ListFilesAsync(child, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested)) { Warn(child, error); continue; }
                if (SkillFilename(children) is { } childFilename) await LoadAsync(child, Join(child, childFilename)).ConfigureAwait(false);
            }
        return skills.AsReadOnly();

        async Task LoadAsync(string directory, string document)
        {
            try
            {
                var content = await files.ReadTextAsync(document, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                skills.Add(Skill.FromContent(content, strict, directory));
            }
            catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested)) { Warn(directory, error); }
        }
    }

    internal static async Task<IReadOnlyList<string>> ResourcesAsync(string path, IReadOnlyFileSystem files, int maximum, CancellationToken cancellationToken)
    {
        var resources = new List<string>();
        foreach (var directory in new[] { "scripts", "references", "assets" })
        {
            IReadOnlyList<string> entries;
            try { entries = await RecursiveAsync(Join(path, directory), 0).ConfigureAwait(false); }
            catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested)) { continue; }
            foreach (var entry in entries.OrderBy(value => value, StringComparer.Ordinal))
            {
                resources.Add(directory + "/" + entry);
                if (resources.Count >= maximum)
                {
                    resources.Add("... (truncated at " + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files)");
                    return resources.AsReadOnly();
                }
            }
        }
        return resources.AsReadOnly();

        async Task<IReadOnlyList<string>> RecursiveAsync(string directory, int depth)
        {
            if (depth >= 3) return [];
            var entries = await files.ListFilesAsync(directory, cancellationToken).ConfigureAwait(false);
            var names = new List<string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                    names.AddRange((await RecursiveAsync(Join(directory, entry.Name), depth + 1).ConfigureAwait(false)).Select(value => entry.Name + "/" + value));
                else names.Add(entry.Name);
            }
            return names.AsReadOnly();
        }
    }

    internal static string Join(string directory, string name) => directory.TrimEnd('/', '\\') + "/" + name;
    private static string Parent(string path)
    {
        var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return index < 0 ? "." : index == 0 || (index == 2 && path[1] == ':') ? path.Substring(0, index + 1) : path.Substring(0, index);
    }
    private static string? SkillFilename(IReadOnlyList<FileEntry> entries) => new[] { "SKILL.md", "skill.md" }.FirstOrDefault(name => entries.Any(entry => !entry.IsDirectory && string.Equals(entry.Name, name, StringComparison.Ordinal)));
    private static void Warn(string path, Exception error) => SdkLogging.Warning("path=<{Path}>, error_type=<{ErrorType}> | failed to load skill", path, error.GetType().Name);
}
