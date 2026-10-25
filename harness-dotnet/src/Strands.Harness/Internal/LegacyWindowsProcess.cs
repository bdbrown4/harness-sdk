#if NETSTANDARD2_0
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Strands.Harness.Internal;

// The legacy asset has no Process.Kill(entireProcessTree). Its Windows job is configured before
// launch, and user code remains opaque data until assignment succeeds and the gate is released.
internal sealed class LegacyWindowsJob : IDisposable
{
    private readonly SafeJobHandle _handle;

    internal LegacyWindowsJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a shell process job.");
        var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "Could not configure shell job termination.");
        }
    }

    internal void Assign(Process process) => Assign(process.SafeHandle);

    internal void Assign(SafeProcessHandle process)
    {
        if (!AssignProcessToJobObject(_handle, process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the gated shell to its process job. User commands were not released.");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class ProcessReleaseGate : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    internal string Name { get; } = "strands-shell-" + Guid.NewGuid().ToString("N");

    internal ProcessReleaseGate() => _pipe = new(Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    internal async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(new byte[] { 71 }, 0, 1, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _pipe.Dispose();
}
#endif
