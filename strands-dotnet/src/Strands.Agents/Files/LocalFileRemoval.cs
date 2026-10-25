using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Strands.Agents.Files;

/// <summary>Issues one strict name-removal request under the host process's filesystem authority.</summary>
internal static class LocalFileRemoval
{
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparsePointAttribute = 0x400;
    private const uint SymbolicLinkTag = 0xa000000c;
    private const uint MountPointTag = 0xa0000003;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static void Remove(string path, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("A file path cannot contain NUL.", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var directoryLink = IsDirectoryLink(path);
            cancellationToken.ThrowIfCancellationRequested();
            var removed = directoryLink ? RemoveDirectory(path) : DeleteFile(path);
            if (!removed)
            {
                var error = Marshal.GetLastWin32Error();
                throw Failure(path, "win32", error);
            }
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var bytes = StrictUtf8.GetBytes(path + "\0");
            cancellationToken.ThrowIfCancellationRequested();
            if (Unlink(bytes) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw Failure(path, "errno", error);
            }
            return;
        }
        throw new PlatformNotSupportedException("Strict local file removal is supported on Windows, Linux and macOS.");
    }

    private static bool IsDirectoryLink(string path)
    {
        // Query the terminal entry itself. Unavailable metadata never authorizes directory removal.
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var data = new byte[8];
        if (!GetFileInformationByHandleEx(handle, 9, data, (uint)data.Length)) return false;
        var attributes = BitConverter.ToUInt32(data, 0);
        var tag = BitConverter.ToUInt32(data, 4);
        return (attributes & (DirectoryAttribute | ReparsePointAttribute)) == (DirectoryAttribute | ReparsePointAttribute)
            && tag is SymbolicLinkTag or MountPointTag;
    }

    private static Exception Failure(string path, string domain, int error)
    {
        var message = "Failed to remove file '" + path + "' (" + domain + " " + error.ToString(CultureInfo.InvariantCulture) + ").";
        Exception failure = (domain, error) switch
        {
            ("win32", 2) or ("errno", 2) => new FileNotFoundException(message, path),
            ("win32", 3) or ("errno", 20) => new DirectoryNotFoundException(message),
            ("win32", 5) or ("errno", 1 or 13) => new UnauthorizedAccessException(message),
            _ => new IOException(message)
        };
        failure.Data["Strands.NativeErrorCode"] = error;
        failure.Data["Strands.NativeErrorDomain"] = domain;
        return failure;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectory(string path);

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string path);

    [DllImport("libc", EntryPoint = "unlink", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int Unlink([In] byte[] path);
}
