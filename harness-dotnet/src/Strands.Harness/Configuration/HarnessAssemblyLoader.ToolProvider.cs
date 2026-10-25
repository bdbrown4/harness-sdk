using Strands.Agents;

namespace Strands.Harness;

public sealed partial class HarnessAssemblyLoader
{
    /// <summary>Creates a borrowed provider for explicitly declared native tool-image generations.</summary>
    /// <remarks>At least one tool declaration must have nonempty Files. Creation and consumer admission perform no filesystem I/O. Tool images use host dependencies and remain loaded; tools remain module-owned. EnableToolProviderUpdates opts an agent into live delivery.</remarks>
    public IToolProviderUpdates CreateToolProvider(HarnessAgentConfig configuration)
    {
        Guard.NotNull(configuration, nameof(configuration));
        var references = configuration.ToJsonElement().GetProperty("tools").EnumerateArray().Select(value => new HarnessModuleReference(value)).ToArray();
        if (references.Length == 0 || !references.Any(reference => reference.Files.Count > 0))
            throw new ArgumentException("A managed tool provider requires tool declarations with explicit Files.", nameof(configuration));
        foreach (var reference in references)
        {
            if (reference.Language is not null && reference.Language != "csharp")
                throw new NotSupportedException("Managed tool providers require native C# assembly declarations.");
            if (!reference.Module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Managed tool providers require .dll module paths.");
            ValidateProviderPath(reference.Module, false);
            foreach (var file in reference.Files) ValidateProviderPath(file, true);
        }
        return new DeclaredToolProvider(this, references, ToolProviderWatcherFactory);
    }

    internal Func<FileSystemWatcher> ToolProviderWatcherFactory { get; set; } = () => new();

    private string ValidateProviderPath(string value, bool allowRoot)
    {
        Guard.NotBlank(value, nameof(value));
        if (Path.IsPathRooted(value) && !Guard.IsFullyQualifiedLocalPath(value))
            throw new ArgumentException("Managed tool provider paths must be fully qualified or relative.");
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Directory, value));
        var root = Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!(allowRoot && OperatingPathComparer.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root)) &&
            !path.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Managed tool provider paths must remain beneath the selected trusted directory.");
        return path;
    }

}
