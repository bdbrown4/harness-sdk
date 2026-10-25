using System.Reflection;
using System.Runtime.ExceptionServices;
using Strands.Agents;

namespace Strands.Harness;

/// <summary>Names an explicitly exported native module value, tool method, or static tool collection.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class HarnessExportAttribute : Attribute
{
    /// <summary>Creates an export with its portable reference name.</summary>
    public HarnessExportAttribute(string name = "default") { Guard.NotBlank(name, nameof(name)); Name = name; }
    /// <summary>The case-sensitive export name.</summary>
    public string Name { get; }
}

/// <summary>Opt-in loading of trusted managed assembly exports beneath an application-selected directory.</summary>
/// <remarks>Loading an assembly executes trusted application code with process permissions. The loader does not provide isolation, unload assemblies, install dependencies, or execute Python/TypeScript source.</remarks>
public sealed partial class HarnessAssemblyLoader
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AssemblyExports> _assemblies = new(OperatingPathComparer);
    private static StringComparer OperatingPathComparer => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    /// <summary>Creates a loader rooted at a fully resolved directory containing trusted managed assemblies.</summary>
    public HarnessAssemblyLoader(string directory) { Guard.NotBlank(directory, nameof(directory)); Directory = Path.GetFullPath(directory); }
    /// <summary>The application-selected trusted assembly directory.</summary>
    public string Directory { get; }
    /// <summary>Adds native assembly resolution as the fallback, retaining all explicit application resolvers.</summary>
    public HarnessConfigBindings Bind(HarnessConfigBindings? bindings = null) => (bindings ?? new()) with { ModuleResolver = bindings?.ModuleResolver ?? Load };
    /// <summary>Loads the selected native export. Exported objects remain module/application-owned.</summary>
    public object Load(HarnessModuleReference reference)
    {
        Guard.NotNull(reference, nameof(reference));
        if (reference.Language is not null && reference.Language != "csharp")
            throw new NotSupportedException($"Cannot load {reference.Language} module '{reference.Module}' in the native managed assembly loader. Supply a C# .dll or an explicit application binding.");
        var path = ResolvePath(reference.Module);
        AssemblyExports exports;
        lock (_gate)
        {
            if (!_assemblies.TryGetValue(path, out exports!))
            {
                var assembly = Assembly.LoadFrom(path);
                var origin = AssemblyOrigin(assembly);
                if (string.IsNullOrEmpty(origin) || !OperatingPathComparer.Equals(Path.GetFullPath(origin), path))
                    throw new FileLoadException("An assembly with this identity is already loaded from another location. Select its original path or use a distinct assembly name; module identities are not isolated.", path);
                exports = Discover(assembly);
                _assemblies.Add(path, exports);
            }
        }
        return ResolveExport(exports, reference);
    }

    private static object ResolveExport(AssemblyExports exports, HarnessModuleReference reference)
    {
        var member = FindExport(exports, reference.Export);
        object? value;
        try { value = ReadExport(member, reference.Kind); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } cause)
        { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
        return value ?? throw new InvalidOperationException($"Module '{reference.Module}' export '{reference.Export}' returned null.");
    }

    private static string AssemblyOrigin(Assembly assembly)
    {
#if NETSTANDARD2_0
        // Framework shadow copies preserve the original LoadFrom URI in CodeBase.
        if (Uri.TryCreate(assembly.CodeBase, UriKind.Absolute, out var origin) && origin.IsFile) return origin.LocalPath;
#endif
        return assembly.Location;
    }

    private string ResolvePath(string module)
    {
        if (!module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Native assembly modules must name a managed .dll file beneath the selected directory; foreign source and package names are not loaded.");
        if (Path.IsPathRooted(module) && !Guard.IsFullyQualifiedLocalPath(module)) throw new ArgumentException("Assembly module paths must be fully qualified or relative to the selected directory.");
        var path = Path.GetFullPath(Path.IsPathRooted(module) ? module : Path.Combine(Directory, module));
        var prefix = Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Assembly module paths must remain beneath the selected trusted directory.");
        var current = Directory;
        RejectLink(current);
        foreach (var part in path.Substring(prefix.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Assembly module paths cannot traverse symbolic links or reparse points within the selected directory.");
    }

    private static AssemblyExports Discover(Assembly assembly)
    {
        var names = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var type in assembly.GetExportedTypes())
        {
            AddExport(names, type);
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (member is FieldInfo or PropertyInfo or MethodInfo) AddExport(names, member);
        }
        return new(assembly, names);
    }

    private static void AddExport(IDictionary<string, MemberInfo> exports, MemberInfo member)
    {
        if (member.GetCustomAttribute<HarnessExportAttribute>() is not { } attribute) return;
        if (exports.ContainsKey(attribute.Name)) throw new ArgumentException($"Assembly '{member.Module.Assembly.GetName().Name}' declares duplicate export '{attribute.Name}'. Export names must be unique before any value is read.");
        exports.Add(attribute.Name, member);
    }

    private static MemberInfo FindExport(AssemblyExports exports, string name)
    {
        if (exports.Names.TryGetValue(name, out var member)) return member;
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        if (separator > 0)
        {
            var type = exports.Assembly.GetType(name.Substring(0, separator), throwOnError: false, ignoreCase: false);
            if (type?.IsVisible == true)
            {
                var candidates = type.GetMember(name.Substring(separator + 2), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(item => item is FieldInfo or PropertyInfo || item is MethodInfo method && !method.IsSpecialName).ToArray();
                if (candidates.Length == 1) return candidates[0];
                if (candidates.Length > 1) throw new ArgumentException($"Export '{name}' is ambiguous; overloaded members require a unique named HarnessExport.");
            }
        }
        throw new ArgumentException($"Assembly '{exports.Assembly.GetName().Name}' does not export '{name}'. Use HarnessExport or a public Type::Member reference.");
    }

    private static object? ReadExport(MemberInfo member, string kind)
    {
        object? value = member switch
        {
            Type type when kind == "tool" => ToolFactory.FromType(type),
            FieldInfo field => field.GetValue(null),
            PropertyInfo property when property.GetMethod is { IsPublic: true, IsStatic: true } && property.GetIndexParameters().Length == 0 => property.GetValue(null),
            MethodInfo method when kind == "tool" && method.IsDefined(typeof(ToolAttribute), true) => ToolFactory.Create(method, null),
            MethodInfo method when kind == "subagent" && IsSubagentFactory(method) => method.Invoke(null, null),
            _ => throw new ArgumentException($"Export '{member.Name}' must be a public static value, a tool method marked Tool, a tool collection type, or a parameterless synchronous subagent factory."),
        };
        if (kind == "subagent" && value is Delegate factory)
        {
            if (factory.GetInvocationList().Length != 1 || !IsSubagentFactory(factory.GetType().GetMethod("Invoke")!))
                throw new ArgumentException("Subagent export factories must have one parameterless synchronous invocation target.");
            value = factory.DynamicInvoke();
        }
        return value;
    }

    private static bool IsSubagentFactory(MethodInfo method) => !method.ContainsGenericParameters && method.GetParameters().Length == 0 &&
        (typeof(Agent).IsAssignableFrom(method.ReturnType) || typeof(IReadOnlyList<Agent>).IsAssignableFrom(method.ReturnType));

    private sealed class AssemblyExports(Assembly assembly, IReadOnlyDictionary<string, MemberInfo> names)
    {
        internal Assembly Assembly { get; } = assembly;
        internal IReadOnlyDictionary<string, MemberInfo> Names { get; } = names;
    }
}
