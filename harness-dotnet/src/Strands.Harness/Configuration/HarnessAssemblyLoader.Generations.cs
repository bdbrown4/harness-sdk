using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Strands.Harness;

public sealed partial class HarnessAssemblyLoader
{
    private readonly Dictionary<string, Dictionary<string, AssemblyExports>> _generations = new(StringComparer.Ordinal);

    /// <summary>Resolves one native configuration using explicitly declared file generations.</summary>
    /// <remarks>Nonempty module Files opt into root-image re-import. Dependencies use the host deployment, image Location is empty, and generations are retained without an unload guarantee. Existing Bind and Load behavior is unchanged.</remarks>
    public HarnessOptions ResolveOptions(HarnessAgentConfig configuration, HarnessConfigBindings? bindings = null, string? baseDirectory = null)
    {
        Guard.NotNull(configuration, nameof(configuration));
        bindings ??= new();
        if (bindings.ModuleResolver is not null) return configuration.ToOptions(bindings, baseDirectory);
        var references = GenerationReferences(configuration.ToJsonElement(), bindings);
        if (!references.Any(reference => reference.Files.Count > 0)) return configuration.ToOptions(Bind(bindings), baseDirectory);
        var capture = CaptureGeneration(references);
        return configuration.ToOptions(bindings with { ModuleResolver = reference => LoadGeneration(reference, capture) }, baseDirectory);
    }

    private GenerationCapture CaptureGeneration(IReadOnlyList<HarnessModuleReference> references)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(OperatingPathComparer);
        var inputs = new HashSet<string>(OperatingPathComparer);
        foreach (var reference in references)
        {
            if (reference.Language is not null && reference.Language != "csharp")
                throw new NotSupportedException($"Cannot load {reference.Language} module '{reference.Module}' in the native managed assembly loader. Supply a C# .dll or an explicit application binding.");
            var path = ResolvePath(reference.Module);
            paths[reference.Module] = path;
            roots.Add(path);
            inputs.Add(path);
            foreach (var file in reference.Files) inputs.Add(ResolveDeclaredPath(file));
        }
        var images = new Dictionary<string, byte[]>(OperatingPathComparer);
        string generation;
        using (var hash = SHA256.Create())
        {
            foreach (var path in inputs.OrderBy(path => path, OperatingPathComparer)) HashSourcePath(hash, path, roots, images);
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            generation = BitConverter.ToString(hash.Hash!).Replace("-", "");
        }
        return new(paths, images, generation);
    }

    private object LoadGeneration(HarnessModuleReference reference, GenerationCapture capture)
        => LoadGeneration(reference, capture.Paths, capture.Images, capture.Fingerprint);

    private sealed class GenerationCapture(IReadOnlyDictionary<string, string> paths, IReadOnlyDictionary<string, byte[]> images, string fingerprint)
    {
        internal IReadOnlyDictionary<string, string> Paths { get; } = paths;
        internal IReadOnlyDictionary<string, byte[]> Images { get; } = images;
        internal string Fingerprint { get; } = fingerprint;
    }

    private object LoadGeneration(HarnessModuleReference reference, IReadOnlyDictionary<string, string> paths, IReadOnlyDictionary<string, byte[]> images, string generation)
    {
        var path = paths[reference.Module];
        AssemblyExports exports;
        lock (_gate)
        {
            if (!_generations.TryGetValue(generation, out var assemblies))
                _generations.Add(generation, assemblies = new(OperatingPathComparer));
            if (!assemblies.TryGetValue(path, out exports!))
            {
                var assembly = Assembly.Load(images[path]);
                if (!string.IsNullOrEmpty(assembly.Location))
                    throw new FileLoadException("The runtime did not create a fresh in-memory module image. Use the ordinary managed loader for this assembly.", path);
                exports = Discover(assembly);
                assemblies.Add(path, exports);
            }
        }
        // Exported getters can synchronously resolve further configuration without holding the cache gate.
        return ResolveExport(exports, reference);
    }

    private static IReadOnlyList<HarnessModuleReference> GenerationReferences(JsonElement config, HarnessConfigBindings bindings)
    {
        var result = new List<HarnessModuleReference>();
        void Add(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) result.Add(new(value));
        }
        void AddMany(string key, bool enabled)
        {
            if (enabled) foreach (var value in config.GetProperty(key).EnumerateArray()) Add(value);
        }
        AddMany("tools", bindings.ToolResolver is null);
        AddMany("subagents", bindings.SubagentResolver is null);
        AddMany("plugins", bindings.PluginResolver is null);
        AddMany("interventionModules", bindings.InterventionModuleResolver is null);
        AddMany("memoryStores", bindings.MemoryStoreResolver is null);
        if (bindings.SandboxResolver is null) Add(config.GetProperty("sandbox"));
        if (bindings.ModelModuleResolver is null && bindings.AgentOptions?.Model is null) Add(config.GetProperty("modelModule"));
        if (bindings.AgentConfigModuleResolver is null)
            foreach (var pair in config.GetProperty("agentConfigModules").EnumerateObject()) Add(pair.Value);
        if (bindings.ModelModuleResolver is null && config.GetProperty("builtinTools").ValueKind == JsonValueKind.Object &&
            config.GetProperty("builtinTools").TryGetProperty("web_fetch", out var fetch) && fetch.ValueKind == JsonValueKind.Object && fetch.TryGetProperty("model", out var model)) Add(model);
        return result;
    }

    private string ResolveDeclaredPath(string file)
    {
        Guard.NotBlank(file, nameof(file));
        if (Path.IsPathRooted(file) && !Guard.IsFullyQualifiedLocalPath(file))
            throw new ArgumentException("Declared file paths must be fully qualified or relative to the selected directory.");
        var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(Directory, file));
        var prefix = Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var isRoot = OperatingPathComparer.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!isRoot && !path.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Declared file paths must remain beneath the selected trusted directory.");
        var current = Directory;
        RejectLink(current);
        if (isRoot) return path;
        foreach (var part in path.Substring(prefix.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (AttributesOrMissing(current) is not { } attributes) break;
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Declared files cannot traverse symbolic links or reparse points within the selected directory.");
        }
        return path;
    }

    private static FileAttributes? AttributesOrMissing(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void HashSourcePath(HashAlgorithm hash, string path, ISet<string> roots, IDictionary<string, byte[]> images)
    {
        HashText(hash, path);
        var attributes = AttributesOrMissing(path);
        if (attributes is null) { HashText(hash, "missing"); return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Declared files cannot traverse symbolic links or reparse points within the selected directory.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            HashText(hash, "directory");
            foreach (var child in System.IO.Directory.GetFileSystemEntries(path).OrderBy(item => item, OperatingPathComparer))
                if (Path.GetFileName(child) is not (".git" or ".venv" or "node_modules")) HashSourcePath(hash, child, roots, images);
            return;
        }
        HashText(hash, "file");
        byte[] digest;
        using (var fileHash = SHA256.Create())
        {
            if (roots.Contains(path))
            {
                if (!images.TryGetValue(path, out var bytes)) images.Add(path, bytes = ReadSourceImage(path));
                digest = fileHash.ComputeHash(bytes);
            }
            else
            {
                using var stream = OpenSource(path);
                digest = HashSourceStream(fileHash, stream);
            }
        }
        hash.TransformBlock(digest, 0, digest.Length, digest, 0);
    }

    private static FileStream OpenSource(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadSourceImage(string path)
    {
        using var stream = OpenSource(path);
        return ReadSourceImage(stream);
    }

    internal static byte[] ReadSourceImage(Stream stream)
    {
        var length = stream.Length;
        if (length < 0 || length > int.MaxValue) throw new IOException("The managed image is too large to capture.");
        var image = new byte[(int)length];
        var offset = 0;
        while (offset < image.Length)
        {
            var read = stream.Read(image, offset, image.Length - offset);
            if (read == 0) throw new EndOfStreamException("A managed image shrank while being captured.");
            offset += read;
        }
        return image;
    }

    internal static byte[] HashSourceStream(HashAlgorithm hash, Stream stream)
    {
        var remaining = stream.Length;
        if (remaining < 0) throw new IOException("The declared input has an invalid length.");
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("A declared input shrank while being captured.");
            hash.TransformBlock(buffer, 0, read, buffer, 0);
            remaining -= read;
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hash.Hash!;
    }

    private static void HashText(HashAlgorithm hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var size = BitConverter.GetBytes(bytes.Length);
        hash.TransformBlock(size, 0, size.Length, size, 0);
        hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
    }
}
