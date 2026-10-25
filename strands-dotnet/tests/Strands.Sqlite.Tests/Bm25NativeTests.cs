using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Strands.Agents.Storage;
using Xunit.Abstractions;

namespace Strands.Sqlite.Tests;

public sealed class Bm25NativeTests(ITestOutputHelper output)
{
    [Fact]
    public void RealNativeFts5ReportsItsArchitectureAndVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE documents USING fts5(key, content); INSERT INTO documents VALUES ('probe', 'native FTS evidence'); SELECT count(*) FROM documents WHERE documents MATCH 'evidence';";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5')";
        Assert.Equal(1L, command.ExecuteScalar());
        var nativeHash = ReadNativeEvidence(out var nativePath);
        output.WriteLine(JsonSerializer.Serialize(new { PointerSize = IntPtr.Size, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription, SQLiteVersion = connection.ServerVersion, Fts5VirtualTableAndMatch = true, EnableFts5 = true, NativePath = nativePath, NativeSHA256 = nativeHash }));
    }

    private static string ReadNativeEvidence(out string nativePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            using var process = Process.GetCurrentProcess();
            var native = Assert.Single(process.Modules.Cast<ProcessModule>(), module => module.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase) || module.ModuleName.StartsWith("libe_sqlite3.", StringComparison.Ordinal));
            nativePath = native.FileName;
            return HashNativeFile(nativePath);
        }

        const string library = "libe_sqlite3.dylib";
        const int rtldLazy = 0x1;
        const int rtldNoLoad = 0x10;
        const int rtldFirst = 0x100;
        var rid = "osx-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var candidates = new[]
        {
            library,
            Path.Combine(AppContext.BaseDirectory, library),
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", library),
        };
        var handle = IntPtr.Zero;
        foreach (var candidate in candidates)
        {
            handle = Dlopen(candidate, rtldLazy | rtldNoLoad | rtldFirst);
            if (handle != IntPtr.Zero) break;
        }
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The SQLite image used by the FTS5 query was not already loaded.");

        string nativeHash;
        int closeResult;
        try
        {
            var symbol = Dlsym(handle, "sqlite3_libversion");
            if (symbol == IntPtr.Zero || Dladdr(symbol, out var info) == 0)
                throw new InvalidOperationException("The loaded SQLite version symbol could not be mapped to its native image.");
            nativePath = Marshal.PtrToStringAnsi(info.FileName) ?? throw new InvalidOperationException("The loaded SQLite image has no pathname.");
            if (Path.GetFileName(nativePath) != library || !File.Exists(nativePath))
                throw new InvalidOperationException("The loaded SQLite image is not the expected packaged native file.");
            nativeHash = HashNativeFile(nativePath);
        }
        finally { closeResult = Dlclose(handle); }
        if (closeResult != 0) throw new InvalidOperationException("The SQLite image witness handle could not be released.");
        return nativeHash;
    }

    private static string HashNativeFile(string path)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr BaseAddress;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr Dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr Dlsym(IntPtr handle, string symbol);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int Dladdr(IntPtr symbol, out DlInfo info);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlclose", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int Dlclose(IntPtr handle);

    [Fact]
    public async Task IndexedContentIsRetrievedThroughTheStrategy()
    {
        var root = Path.Combine(Path.GetTempPath(), "bm25-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        try
        {
            var storage = new LocalFileStorage(root);
            await strategy.IndexAsync(storage, "auth.md", Encoding.UTF8.GetBytes("authentication OAuth tokens"));
            await strategy.IndexAsync(storage, "deploy.md", Encoding.UTF8.GetBytes("deployment pipelines"));
            var results = await strategy.SearchAsync(storage, "authentication tokens");
            Assert.Equal("auth.md", Assert.Single(results).Key);
            Assert.InRange(results[0].Score, double.Epsilon, 1);
            Assert.Null(results[0].Data);
        }
        finally { Directory.Delete(root, true); }
    }
}
