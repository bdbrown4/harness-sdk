using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Interventions.Cedar;

internal static class NativeCedar
{
    private static readonly Lazy<Library> Engine = new(() => new("strands_cedar_engine"));
    private static readonly Lazy<Library> Schema = new(() => new("strands_cedar_schema"));

    internal static JsonElement Execute(string operation, object request, bool schema = false)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(new { operation, request });
        return (schema ? Schema : Engine).Value.Execute(input);
    }

    private sealed class Library
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ExecuteCall([In] byte[] input, UIntPtr length, out IntPtr output, out UIntPtr outputLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeCall(IntPtr buffer, UIntPtr length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint VersionCall();

        private readonly ExecuteCall _execute;
        private readonly FreeCall _free;
        // Keep one library reference alive for the process. Delegates and active
        // native calls must never outlive an unloaded module.
        private readonly IntPtr _handle;

        internal Library(string name)
        {
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            var architecture = RuntimeInformation.ProcessArchitecture;
            var rid = windows && architecture == Architecture.X86 ? "win-x86" :
                windows && architecture == Architecture.X64 ? "win-x64" :
                mac && architecture == Architecture.Arm64 ? "osx-arm64" :
                mac && architecture == Architecture.X64 ? "osx-x64" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && architecture == Architecture.X64 ? "linux-x64" :
                throw new PlatformNotSupportedException($"Cedar has no native asset for {RuntimeInformation.OSDescription}/{architecture}.");
            var filename = windows ? name + ".dll" : "lib" + name + (mac ? ".dylib" : ".so");
            var directory = Path.GetDirectoryName(typeof(NativeCedar).Assembly.Location) ?? AppContext.BaseDirectory;
            // Framework test hosts and ASP.NET can shadow-copy only managed
            // assemblies. Their original application directory retains assets.
            var directories = new[] { directory, AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "bin") };
            var path = directories.SelectMany(root => new[] { Path.Combine(root, "runtimes", rid, "native", filename), Path.Combine(root, filename) }).FirstOrDefault(File.Exists)
                ?? throw new DllNotFoundException($"Cedar native asset {rid}/{filename} is missing beside {typeof(NativeCedar).Assembly.GetName().Name} or its application directory.");
            _handle = windows ? LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000) : mac ? MacOpen(path, 2) : LinuxOpen(path, 2);
            if (_handle == IntPtr.Zero) throw new DllNotFoundException($"Cannot load Cedar native asset {path}.", windows ? new Win32Exception(Marshal.GetLastWin32Error()) : null);
            try
            {
                var version = Resolve<VersionCall>("strands_cedar_abi_version");
                if (version() != 1) throw new InvalidOperationException("Cedar native ABI version is not supported.");
                _execute = Resolve<ExecuteCall>("strands_cedar_execute");
                _free = Resolve<FreeCall>("strands_cedar_free");
            }
            catch
            {
                if (windows) FreeLibrary(_handle);
                else if (mac) MacClose(_handle);
                else LinuxClose(_handle);
                throw;
            }
        }

        private T Resolve<T>(string name) where T : Delegate
        {
            var address = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetProcAddress(_handle, name) :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacSymbol(_handle, name) : LinuxSymbol(_handle, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException($"Cedar native entry point {name} is missing.");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        internal JsonElement Execute(byte[] input)
        {
            IntPtr output = IntPtr.Zero;
            UIntPtr length = UIntPtr.Zero;
            try
            {
                var status = _execute(input, new UIntPtr((uint)input.Length), out output, out length);
                var count = length.ToUInt64();
                if (output == IntPtr.Zero || count > int.MaxValue) throw new InvalidOperationException("Cedar returned an invalid native output buffer.");
                var bytes = new byte[(int)count];
                Marshal.Copy(output, bytes, 0, bytes.Length);
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
                var envelope = document.RootElement;
                if (status != 0 || !envelope.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Cedar native bridge failed: " + (envelope.TryGetProperty("error", out var error) ? error.GetString() : "invalid protocol response"));
                return envelope.GetProperty("result").Clone();
            }
            finally { if (output != IntPtr.Zero) _free(output, length); }
        }

        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr library);
        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr LinuxOpen(string path, int flags);
        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        private static extern IntPtr LinuxSymbol(IntPtr library, string name);
        [DllImport("libdl.so.2", EntryPoint = "dlclose")]
        private static extern int LinuxClose(IntPtr library);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
        private static extern IntPtr MacOpen(string path, int flags);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym")]
        private static extern IntPtr MacSymbol(IntPtr library, string name);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlclose")]
        private static extern int MacClose(IntPtr library);
    }
}
