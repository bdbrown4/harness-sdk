#if NETSTANDARD2_0
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Strands.Agents.Processes;

internal static class WindowsChildProcess
{
    private const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400, ExtendedStartupInfoPresent = 0x80000, CreateNoWindow = 0x8000000;
    private static readonly IntPtr HandleListAttribute = new(0x20002);

    internal static void Start(ProcessStartInfo start, Action<Process, Stream, Stream, Stream> attach, CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("The legacy owned binary process implementation requires Windows.");
        var filename = start.FileName.Trim();
        if (filename.Length == 0 || filename.IndexOf('\0') >= 0 || start.Arguments.IndexOf('\0') >= 0)
            throw new ArgumentException("A child executable and arguments must be valid non-NUL strings.", nameof(start));
        var command = new StringBuilder(Quote(filename));
        if (start.Arguments.Length != 0) command.Append(' ').Append(start.Arguments);
        if (command.Length >= 32767) throw new ArgumentException("The native client arguments exceed the Windows command-line limit.", nameof(start));
        var environment = EnvironmentBlock(start);
        AnonymousPipeServerStream? input = null, output = null, error = null;
        Process? managed = null;
        SafeKernelHandle? processHandle = null, threadHandle = null;
        IntPtr attributes = IntPtr.Zero, inherited = IntPtr.Zero;
        var attributesInitialized = false;
        var resumed = false;
        try
        {
            input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            foreach (var pipe in new[] { input, output, error })
                if (!SetHandleInformation(pipe.SafePipeHandle, 1, 0)) throw LastError();
            var handles = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            var size = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw LastError();
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw LastError();
            attributesInitialized = true;
            inherited = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, inherited, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, inherited, new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero)) throw LastError();
            var startup = new StartupInfoEx
            {
                StartupInfo = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, StandardInput = handles[0], StandardOutput = handles[1], StandardError = handles[2] },
                AttributeList = attributes,
            };
            var flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (start.CreateNoWindow) flags |= CreateNoWindow;
            cancellationToken.ThrowIfCancellationRequested();
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, flags, environment, string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref startup, out var information)) throw LastError();
            processHandle = new(information.Process);
            threadHandle = new(information.Thread);
            managed = Process.GetProcessById(information.ProcessId);
            _ = managed.SafeHandle; // Acquire the managed lifetime handle before releasing the native one or executing user code.
            attach(managed, input, output, error);
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(threadHandle) == uint.MaxValue) throw LastError();
            resumed = true;
            input = output = error = null; // All parent endpoints and the managed Process now belong to OwnedChildProcess.
            managed = null;
        }
        catch (Exception primary)
        {
            if (!resumed && processHandle is { IsInvalid: false })
            {
                if (!TerminateProcess(processHandle, 1)) Attach(primary, LastError());
                var joined = WaitForSingleObject(processHandle, 5000);
                if (joined == uint.MaxValue) Attach(primary, LastError());
                else if (joined != 0)
                {
                    var cleanup = new IOException("The suspended child could not be reaped within the startup cleanup deadline.");
                    if (managed is not null) cleanup.Data["Strands.ProcessId"] = managed.Id;
                    Attach(primary, cleanup);
                }
            }
            ClosePipe(input, primary); ClosePipe(output, primary); ClosePipe(error, primary); Attempt(managed, primary);
            throw;
        }
        finally
        {
            threadHandle?.Dispose();
            processHandle?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
        }
    }

    // The executable is a CRT argv[0], and the caller already supplied the remaining Windows argv encoding.
    private static string Quote(string filename)
    {
        if (filename.IndexOf('"') >= 0) throw new ArgumentException("A child executable path cannot contain quotes.", nameof(filename));
        return "\"" + filename + "\"";
    }
    private static string EnvironmentBlock(ProcessStartInfo start)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry entry in start.EnvironmentVariables)
        {
            var key = (string)entry.Key;
            var value = (string?)entry.Value ?? string.Empty;
            if (key.Length == 0 || key.IndexOf('\0') >= 0 || value.IndexOf('\0') >= 0) throw new ArgumentException("Child environment entries cannot contain NUL.", nameof(start));
            entries.Add(new(key, value));
        }
        return string.Join("\0", entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";
    }
    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());
    private static void ClosePipe(AnonymousPipeServerStream? pipe, Exception primary)
    {
        if (pipe is null) return;
        try { pipe.DisposeLocalCopyOfClientHandle(); } catch (Exception error) { Attach(primary, error); }
        Attempt(pipe, primary);
    }
    private static void Attempt(IDisposable? owned, Exception primary) { try { owned?.Dispose(); } catch (Exception error) { Attach(primary, error); } }
    private static void Attach(Exception primary, Exception error)
    {
        if (ReferenceEquals(primary, error)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = new();
        errors.Add(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        internal uint Flags;
        internal short ShowWindow, ReservedBytes;
        internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafePipeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, string environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
#endif
