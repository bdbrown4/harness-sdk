using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Strands.Agents.Bidi;

// Only the device boundary is native. DSP, buffering and the public run lifecycle remain managed.
internal sealed class PortAudioDeviceBackend : IAudioDeviceBackend
{
    internal static string Version => NativePortAudio.Instance.Version;
    public IAsyncDisposable OpenCapture(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Action<byte[]> captured, Action<Exception> failed)
        => NativePortAudio.Instance.Open(format, device, framesPerBuffer, captured, null, failed);
    public IAsyncDisposable OpenPlayback(BidiAudioStreamConfig format, int? device, int framesPerBuffer, Func<int, byte[]> playback, Action<Exception> failed)
        => NativePortAudio.Instance.Open(format, device, framesPerBuffer, null, playback, failed);
}

internal sealed class NativePortAudio
{
    private static readonly Lazy<NativePortAudio> Library = new(() => new());
    internal static NativePortAudio Instance => Library.Value;
    private readonly object _gate = new();
    // A failed native close must retain its callback, rather than permit native use-after-free.
    private readonly HashSet<DeviceStream> _streams = [];
    private readonly IntPtr _library;
    private readonly bool _windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly bool _mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    private readonly SimpleCall _initialize, _terminate, _defaultInput, _defaultOutput;
    private readonly PointerCall _version;
    private readonly ErrorTextCall _errorText;
    private readonly StreamCall _start, _abort, _close;
    private readonly OpenWindowsCall? _openWindows;
    private readonly OpenUnixCall? _openUnix;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SimpleCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PointerCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorTextCall(int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamCall(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenWindowsCall(out IntPtr stream, IntPtr input, IntPtr output, double rate, uint frames, uint flags, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenUnixCall(out IntPtr stream, IntPtr input, IntPtr output, double rate, UIntPtr frames, UIntPtr flags, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WindowsCallback(IntPtr input, IntPtr output, uint frames, IntPtr time, uint flags, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UnixCallback(IntPtr input, IntPtr output, UIntPtr frames, IntPtr time, UIntPtr flags, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsParameters
    {
        internal int Device, Channels;
        internal uint Format;
        internal double Latency;
        internal IntPtr HostInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnixParameters
    {
        internal int Device, Channels;
        internal UIntPtr Format;
        internal double Latency;
        internal IntPtr HostInfo;
    }

    private NativePortAudio()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var rid = _windows && architecture == Architecture.X86 ? "win-x86" :
            _windows && architecture == Architecture.X64 ? "win-x64" :
            _mac && architecture == Architecture.Arm64 ? "osx-arm64" :
            _mac && architecture == Architecture.X64 ? "osx-x64" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && architecture == Architecture.X64 ? "linux-x64" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && architecture == Architecture.Arm64 ? "linux-arm64" :
            throw new PlatformNotSupportedException($"PortAudio has no configured native asset for {RuntimeInformation.OSDescription}/{architecture}.");
        var filename = _windows ? "portaudio.dll" : _mac ? "libportaudio.dylib" : "libportaudio.so";
        var directory = Path.GetDirectoryName(typeof(NativePortAudio).Assembly.Location) ?? AppContext.BaseDirectory;
        var roots = new[] { directory, AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "bin") };
        var path = roots.SelectMany(root => new[] { Path.Combine(root, "runtimes", rid, "native", filename), Path.Combine(root, filename) }).FirstOrDefault(File.Exists)
            ?? throw new DllNotFoundException($"PortAudio asset {rid}/{filename} is missing beside the application. Windows x86 requires the separately built Win32 asset.");
        _library = _windows ? LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000) : _mac ? MacOpen(path, 2) : LinuxOpen(path, 2);
        if (_library == IntPtr.Zero) throw new DllNotFoundException($"Cannot load PortAudio asset {path}.", _windows ? new Win32Exception(Marshal.GetLastWin32Error()) : null);
        // Keep the module alive for the process: delegates and active callbacks must never outlive it.
        _version = Resolve<PointerCall>("Pa_GetVersionText"); _errorText = Resolve<ErrorTextCall>("Pa_GetErrorText");
        _initialize = Resolve<SimpleCall>("Pa_Initialize"); _terminate = Resolve<SimpleCall>("Pa_Terminate");
        _defaultInput = Resolve<SimpleCall>("Pa_GetDefaultInputDevice"); _defaultOutput = Resolve<SimpleCall>("Pa_GetDefaultOutputDevice");
        _start = Resolve<StreamCall>("Pa_StartStream"); _abort = Resolve<StreamCall>("Pa_AbortStream"); _close = Resolve<StreamCall>("Pa_CloseStream");
        if (_windows) _openWindows = Resolve<OpenWindowsCall>("Pa_OpenStream"); else _openUnix = Resolve<OpenUnixCall>("Pa_OpenStream");
    }
    internal string Version => Marshal.PtrToStringAnsi(_version()) ?? throw new IOException("PortAudio returned no version string.");

    internal IAsyncDisposable Open(BidiAudioStreamConfig format, int? device, int frames, Action<byte[]>? captured, Func<int, byte[]>? playback, Action<Exception> failed)
    {
        lock (_gate)
        {
            Check(_initialize(), "initialize");
            DeviceStream? state = null; IntPtr parameters = IntPtr.Zero; Exception? primary = null;
            try
            {
                var index = device ?? (captured is not null ? _defaultInput() : _defaultOutput());
                if (index < 0) throw new IOException("No default PortAudio device is available; select a device explicitly.");
                // C unsigned long is 32-bit on Windows, including Win64; it is pointer-sized on supported Unix ABIs.
                parameters = Marshal.AllocHGlobal(_windows ? Marshal.SizeOf<WindowsParameters>() : Marshal.SizeOf<UnixParameters>());
                if (_windows) Marshal.StructureToPtr(new WindowsParameters { Device = index, Channels = format.Channels, Format = 8, Latency = 0.05 }, parameters, false);
                else Marshal.StructureToPtr(new UnixParameters { Device = index, Channels = format.Channels, Format = new UIntPtr(8), Latency = 0.05 }, parameters, false);
                state = new(this, format.Channels, captured, playback, failed);
                var callback = Marshal.GetFunctionPointerForDelegate(state.Callback);
                var input = captured is not null ? parameters : IntPtr.Zero; var output = playback is not null ? parameters : IntPtr.Zero;
                var code = _windows ? _openWindows!(out state.Handle, input, output, format.SampleRate, checked((uint)frames), 0, callback, IntPtr.Zero) :
                    _openUnix!(out state.Handle, input, output, format.SampleRate, new UIntPtr(checked((uint)frames)), UIntPtr.Zero, callback, IntPtr.Zero);
                if (code != 0) state.Handle = IntPtr.Zero;
                Check(code, "open stream"); _streams.Add(state);
                Check(_start(state.Handle), "start stream");
                return state;
            }
            catch (Exception error)
            {
                primary = error;
                if (state is { Handle: var handle } && handle != IntPtr.Zero)
                    try { Close(state); } catch (Exception cleanup) { Add(ref primary, cleanup); }
                else try { Check(_terminate(), "terminate"); } catch (Exception cleanup) { Add(ref primary, cleanup); }
                ExceptionDispatchInfo.Capture(primary!).Throw(); throw;
            }
            finally { if (parameters != IntPtr.Zero) Marshal.FreeHGlobal(parameters); }
        }
    }
    private void Close(DeviceStream state)
    {
        lock (_gate)
        {
            if (state.Handle == IntPtr.Zero) return;
            Exception? primary = state.CallbackError;
            var aborted = _abort(state.Handle);
            // paStreamIsStopped is expected after a callback has already completed/aborted.
            if (aborted != 0 && aborted != -9983) Add(ref primary, Error(aborted, "abort stream"));
            var closed = _close(state.Handle);
            if (closed == 0)
            {
                state.Handle = IntPtr.Zero; _streams.Remove(state);
                var terminated = _terminate(); if (terminated != 0) Add(ref primary, Error(terminated, "terminate"));
            }
            else Add(ref primary, Error(closed, "close stream"));
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
    private sealed class DeviceStream : IAsyncDisposable
    {
        private readonly NativePortAudio _owner;
        private readonly int _channels;
        private readonly Action<byte[]>? _captured;
        private readonly Func<int, byte[]>? _playback;
        private readonly Action<Exception> _failed;
        internal readonly Delegate Callback;
        internal IntPtr Handle;
        internal Exception? CallbackError;
        internal DeviceStream(NativePortAudio owner, int channels, Action<byte[]>? captured, Func<int, byte[]>? playback, Action<Exception> failed)
        {
            _owner = owner; _channels = channels; _captured = captured; _playback = playback; _failed = failed;
            Callback = owner._windows ? new WindowsCallback((input, output, frames, _, _, _) => Invoke(input, output, frames)) :
                new UnixCallback((input, output, frames, _, _, _) => Invoke(input, output, frames.ToUInt64()));
        }
        private int Invoke(IntPtr input, IntPtr output, ulong frames)
        {
            try
            {
                var count = checked((int)frames * _channels * 2);
                if (_captured is not null)
                {
                    var bytes = new byte[count]; if (input != IntPtr.Zero) Marshal.Copy(input, bytes, 0, count); _captured(bytes);
                }
                if (_playback is not null)
                {
                    var bytes = _playback(checked((int)frames));
                    if (bytes.Length != count) throw new IOException("Audio playback returned an invalid frame count.");
                    if (output != IntPtr.Zero) Marshal.Copy(bytes, 0, output, count);
                }
                return 0;
            }
            catch (Exception error)
            {
                // Never publish uninitialized output or let an exception cross the unmanaged callback boundary.
                if (output != IntPtr.Zero && frames <= (ulong)(int.MaxValue / _channels / 2))
                    try { var silence = new byte[(int)frames * _channels * 2]; Marshal.Copy(silence, 0, output, silence.Length); } catch { }
                if (Interlocked.CompareExchange(ref CallbackError, error, null) is null)
                    try { _failed(error); } catch (Exception cleanup) { Exception? primary = error; Add(ref primary, cleanup); }
                return 2;
            }
        }
        public ValueTask DisposeAsync() { _owner.Close(this); GC.KeepAlive(Callback); return default; }
    }
    private void Check(int code, string operation) { if (code != 0) throw Error(code, operation); }
    private IOException Error(int code, string operation) => new($"PortAudio {operation} failed ({code}): {Marshal.PtrToStringAnsi(_errorText(code))}.");
    private static void Add(ref Exception? primary, Exception error)
    {
        if (primary is null) { primary = error; return; }
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(error);
    }
    private T Resolve<T>(string name) where T : Delegate
    {
        var address = _windows ? GetProcAddress(_library, name) : _mac ? MacSymbol(_library, name) : LinuxSymbol(_library, name);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException($"PortAudio entry point {name} is missing.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr library, string name);
    [DllImport("libdl.so.2", EntryPoint = "dlopen")] private static extern IntPtr LinuxOpen(string path, int flags);
    [DllImport("libdl.so.2", EntryPoint = "dlsym")] private static extern IntPtr LinuxSymbol(IntPtr library, string name);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")] private static extern IntPtr MacOpen(string path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym")] private static extern IntPtr MacSymbol(IntPtr library, string name);
}
