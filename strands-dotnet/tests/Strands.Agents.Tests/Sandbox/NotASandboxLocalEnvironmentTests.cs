using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Sandbox;
using Xunit.Abstractions;

namespace Strands.Agents.Tests.Sandbox;

public sealed class NotASandboxLocalEnvironmentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LocalCodeRunsKnownInterpreterWithExactUtf8SourceAndNoShell()
    {
        var expected = ExpectedInterpreter();
        var sandbox = new NotASandboxLocalEnvironment();
        const string source = """
            import json, sys
            text = "雪🐍 ' \" $HOME; &"
            print(json.dumps({"text": text, "executable": sys.executable, "argc": len(sys.argv)}, ensure_ascii=False))
            """;
        var result = await sandbox.ExecuteCodeAsync(source, Path.GetFileName(expected), Options());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("雪🐍 ' \" $HOME; &", json.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("argc").GetInt32());
        var actual = json.RootElement.GetProperty("executable").GetString()!;
        Assert.Equal(Canonical(expected), Canonical(actual));
        Receipt(nameof(LocalCodeRunsKnownInterpreterWithExactUtf8SourceAndNoShell), expected, actual);
    }

    [Fact]
    public async Task LocalCodeUsesEnteredCwdAndDetachedEnvironment()
    {
        var expected = ExpectedInterpreter();
        await InOwnedDirectory(async directory =>
        {
            var sandbox = new NotASandboxLocalEnvironment();
            var environment = new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["STRANDS_ENTERED_VALUE"] = "original雪" };
            var options = Options() with { WorkingDirectory = directory, Environment = environment };
            using var caller = new CancellationTokenSource();
            var stream = sandbox.ExecuteCodeStreamingAsync("import json, os, sys; print(json.dumps({'cwd':os.getcwd(), 'value':os.environ['STRANDS_ENTERED_VALUE'], 'executable':sys.executable}, ensure_ascii=False))", Path.GetFileName(expected), options, caller.Token);
            environment["STRANDS_ENTERED_VALUE"] = "changed";
            ExecutionResult? result = null;
            await foreach (var item in stream.WithCancellation(caller.Token))
                if (item is ExecutionResult terminal) result = terminal;
            Assert.NotNull(result);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Stderr);
            using var json = JsonDocument.Parse(result.Stdout);
            Assert.Equal(Canonical(directory), Canonical(json.RootElement.GetProperty("cwd").GetString()!));
            Assert.Equal("original雪", json.RootElement.GetProperty("value").GetString());
            Assert.Equal("changed", environment["STRANDS_ENTERED_VALUE"]);
            var actual = json.RootElement.GetProperty("executable").GetString()!;
            Assert.Equal(Canonical(expected), Canonical(actual));
            Receipt(nameof(LocalCodeUsesEnteredCwdAndDetachedEnvironment), expected, actual);
        });
    }

    [Fact]
    public async Task FreshInterpreterStateAndPersistentFilesWithInputEof()
    {
        var expected = ExpectedInterpreter();
        await InOwnedDirectory(async directory =>
        {
            var sandbox = new NotASandboxLocalEnvironment();
            var options = Options() with { WorkingDirectory = directory };
            var first = await sandbox.ExecuteCodeAsync("from pathlib import Path; secret = 37; Path('persistent.txt').write_text('persisted雪', encoding='utf-8')", Path.GetFileName(expected), options);
            Assert.Equal(0, first.ExitCode);
            Assert.Equal("", first.Stderr);
            const string secondSource = """
                from pathlib import Path
                import json, sys
                try:
                    input()
                    eof = False
                except EOFError:
                    eof = True
                print(json.dumps({'secret_present': 'secret' in globals(), 'file':Path('persistent.txt').read_text(encoding='utf-8'), 'eof':eof, 'executable':sys.executable}, ensure_ascii=False))
                """;
            var second = await sandbox.ExecuteCodeAsync(secondSource, Path.GetFileName(expected), options);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal("", second.Stderr);
            using var json = JsonDocument.Parse(second.Stdout);
            Assert.False(json.RootElement.GetProperty("secret_present").GetBoolean());
            Assert.Equal("persisted雪", json.RootElement.GetProperty("file").GetString());
            Assert.True(json.RootElement.GetProperty("eof").GetBoolean());
            var actual = json.RootElement.GetProperty("executable").GetString()!;
            Assert.Equal(Canonical(expected), Canonical(actual));
            Receipt(nameof(FreshInterpreterStateAndPersistentFilesWithInputEof), expected, actual);
        });
    }

    [Fact]
    public async Task LocalFileBytesTextListingAndDeleteFollowNativeContract()
    {
        await InOwnedDirectory(async directory =>
        {
            var sandbox = new NotASandboxLocalEnvironment();
            var nested = Path.Combine(directory, "nested");
            var binaryPath = Path.Combine(nested, "binary.bin");
            var textPath = Path.Combine(nested, "text-雪.txt");
            byte[] bytes = [0, 255, 127, 10];
            var writing = sandbox.WriteBytesAsync(binaryPath, bytes);
            Array.Clear(bytes, 0, bytes.Length);
            await writing;
            var firstRead = await sandbox.ReadBytesAsync(binaryPath);
            Assert.Equal(new byte[] { 0, 255, 127, 10 }, firstRead);
            firstRead[0] = 42;
            Assert.Equal(new byte[] { 0, 255, 127, 10 }, await sandbox.ReadBytesAsync(binaryPath));
            await sandbox.WriteBytesAsync(textPath, Encoding.UTF8.GetBytes("text雪"));
            Assert.Equal("text雪", await sandbox.ReadTextAsync(textPath));
            var listed = await sandbox.ListFilesAsync(nested);
            Assert.Equal(new[] { "binary.bin", "text-雪.txt" }, listed.Select(entry => entry.Name).ToArray());
            Assert.All(listed, entry => { Assert.False(entry.IsDirectory); Assert.False(entry.IsSymbolicLink); });
            await sandbox.DeleteFileAsync(binaryPath);
            await sandbox.DeleteFileAsync(binaryPath); // Native IFileSystem deliberately treats missing delete as a no-op.
            await sandbox.DeleteFileAsync(textPath);
            Assert.Empty(await sandbox.ListFilesAsync(nested));
            Receipt(nameof(LocalFileBytesTextListingAndDeleteFollowNativeContract));
        });
    }

    private static ExecuteOptions Options() => new() { Timeout = TimeSpan.FromSeconds(30), Environment = new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" } };

    private static string ExpectedInterpreter()
    {
        var expected = Environment.GetEnvironmentVariable("STRANDS_PYTHON_EXECUTABLE");
        Assert.NotNull(expected);
        Assert.False(string.IsNullOrWhiteSpace(expected), "Root must provide the exact staged interpreter path; no ambient interpreter fallback is permitted.");
        Assert.True(Path.IsPathRooted(expected));
        if (Path.DirectorySeparatorChar == '\\') Assert.True(Path.GetPathRoot(expected)!.Length >= 3);
        var absolute = Path.GetFullPath(expected);
        Assert.True(File.Exists(absolute), "The Root-owned interpreter prerequisite must exist before SDK proof.");
        return absolute;
    }

    private static string Canonical(string path)
    {
        var absolute = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.DirectorySeparatorChar == '\\' ? absolute.ToUpperInvariant() : absolute;
    }

    private static async Task InOwnedDirectory(Func<string, Task> action)
    {
        var parent = Strands.Tests.TestPaths.GetPhysicalTempPath();
        var directory = Path.GetFullPath(Path.Combine(parent, "strands-local-capability-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        Exception? failure = null;
        try { await action(directory); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup) when (failure is not null)
            {
                if (!ReferenceEquals(failure, cleanup))
                {
                    const string key = "Strands.CleanupErrors";
                    var errors = failure.Data[key] is IEnumerable<Exception> existing ? existing.ToList() : [];
                    if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
                    failure.Data[key] = errors;
                }
            }
        }
    }

    private void Receipt(string test, string? expected = null, string? actual = null) => output.WriteLine(JsonSerializer.Serialize(new { Test = test, PointerSize = IntPtr.Size, Runtime = RuntimeInformation.FrameworkDescription, ExpectedInterpreter = expected, ActualInterpreter = actual, Backend = "real-local-root-owned-runner" }));
}
