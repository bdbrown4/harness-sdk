using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Strands.Agents.Mcp;

internal sealed class WindowsStdioJob : IDisposable
{
    private readonly JobHandle _handle;
    internal WindowsStdioJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned MCP process job.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error(); _handle.Dispose();
            throw new Win32Exception(error, "Could not configure MCP process termination.");
        }
    }
    internal void Assign(SafeProcessHandle processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the MCP release gate to its owned job. The server was not released.");
    }
    internal async Task TerminateAndWaitAsync()
    {
        if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate the owned MCP job.");
        while (true)
        {
            if (!QueryInformationJobObject(_handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect MCP process termination.");
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTime, PerJobUserTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        internal long User, Kernel, PeriodUser, PeriodKernel;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out Accounting accounting, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
