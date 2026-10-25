namespace Strands.Tests;

internal static class TestPaths
{
    internal static string GetPhysicalTempPath()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
#if NETFRAMEWORK
        return path;
#else
        // Resolve the trusted existing OS temp root before creating owned children.
        // This keeps cwd identity and strict cleanup ancestry checks consistent.
        var root = Path.GetPathRoot(path)!;
        var directory = new DirectoryInfo(root);
        foreach (var component in path.Substring(root.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ancestor = new DirectoryInfo(Path.Combine(directory.FullName, component));
            directory = new DirectoryInfo(ancestor.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? ancestor.FullName);
        }
        return directory.FullName;
#endif
    }
}
