using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Strands.Agents.Bedrock;

internal static class NativeNovaSonicNativeMethods
{
    private static readonly Lazy<Functions> Api = new Lazy<Functions>(Load);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Signal(IntPtr context, int kind, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Body(IntPtr context, IntPtr bytes, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Written(IntPtr context, int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Abi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Error(int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenFunction(byte[] host, ushort port, byte[] path, byte[] ca, byte[] headers, Signal signal, Body body, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteFunction(IntPtr session, byte[] bytes, uint length, int endStream, Written complete, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WindowFunction(IntPtr session, uint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CancelFunction(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PendingFunction(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int JoinFunction(IntPtr session, out int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EventFunction(byte[] secret, byte[] region, byte[] previous, long millis, byte[] bytes, UIntPtr length, [Out] byte[] signature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestFunction(byte[] access, byte[] secret, byte[] token, byte[] region, byte[] host, byte[] path, long millis, [Out] byte[] headers, uint capacity, out uint length, [Out] byte[] signature);

    internal static int AbiVersion() => Api.Value.Version();
    internal static IntPtr ErrorName(int error) => Api.Value.ErrorName(error);
    internal static IntPtr Open(byte[] host, ushort port, byte[] path, byte[] ca, byte[] headers, Signal signal, Body body, IntPtr context) => Api.Value.Open(host, port, path, ca, headers, signal, body, context);
    internal static int Write(IntPtr session, byte[] bytes, uint length, int endStream, Written complete, IntPtr context) => Api.Value.Write(session, bytes, length, endStream, complete, context);
    internal static void UpdateWindow(IntPtr session, uint amount) => Api.Value.Window(session, amount);
    internal static void Cancel(IntPtr session) => Api.Value.Cancel(session);
    internal static int Pending(IntPtr session) => Api.Value.Pending(session);
    internal static int JoinAndDestroy(IntPtr session, out int flags) => Api.Value.Join(session, out flags);
    internal static int SignEvent(byte[] secret, byte[] region, byte[] previous, long millis, byte[] bytes, UIntPtr length, byte[] signature) => Api.Value.Event(secret, region, previous, millis, bytes, length, signature);
    internal static int SignRequest(byte[] access, byte[] secret, byte[] token, byte[] region, byte[] host, byte[] path, long millis, byte[] headers, uint capacity, out uint length, byte[] signature) => Api.Value.Request(access, secret, token, region, host, path, millis, headers, capacity, out length, signature);

    private static Functions Load()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("The .NET Standard Nova Sonic duplex transport requires Windows.");
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture != Architecture.X64 && architecture != Architecture.X86) throw new PlatformNotSupportedException("The native Nova Sonic transport supports Windows x64 and x86.");
        var directory = Path.GetDirectoryName(typeof(NativeNovaSonicNativeMethods).Assembly.Location)!;
        var path = Path.Combine(directory, "runtimes", architecture == Architecture.X64 ? "win-x64" : "win-x86", "native", "strands-nova-crt.dll");
        if (!File.Exists(path)) throw new DllNotFoundException("The packaged Nova Sonic native asset is missing: " + path);
        var module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000); // Exact absolute asset; DLL directory and safe default dependency directories only.
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the packaged Nova Sonic transport.");
        try
        {
            var api = new Functions(module);
            if (api.Version() != 2) throw new InvalidOperationException("The packaged Nova Sonic native ABI does not match this adapter.");
            return api;
        }
        catch { FreeLibrary(module); throw; }
    }

    private sealed class Functions
    {
        // Like a P/Invoke module, code stays loaded for the process lifetime; callbacks cannot outlive its code.
        private readonly IntPtr _module;
        internal readonly Abi Version;
        internal readonly Error ErrorName;
        internal readonly OpenFunction Open;
        internal readonly WriteFunction Write;
        internal readonly WindowFunction Window;
        internal readonly CancelFunction Cancel;
        internal readonly PendingFunction Pending;
        internal readonly JoinFunction Join;
        internal readonly EventFunction Event;
        internal readonly RequestFunction Request;
        internal Functions(IntPtr module)
        {
            _module = module;
            Version = Bind<Abi>("strands_crt_abi_version");
            ErrorName = Bind<Error>("strands_crt_error_name");
            Open = Bind<OpenFunction>("strands_crt_http_open");
            Write = Bind<WriteFunction>("strands_crt_http_write");
            Window = Bind<WindowFunction>("strands_crt_http_update_window");
            Cancel = Bind<CancelFunction>("strands_crt_http_cancel");
            Pending = Bind<PendingFunction>("strands_crt_http_pending_writes");
            Join = Bind<JoinFunction>("strands_crt_http_join_and_destroy");
            Event = Bind<EventFunction>("strands_crt_sign_event");
            Request = Bind<RequestFunction>("strands_crt_sign_request");
        }
        private T Bind<T>(string name) where T : Delegate
        {
            var address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
}
