using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Strands.BinaryPipe.ProcessFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 64;
        if (args[0] == "private") return await PrivateAsync(args).ConfigureAwait(false);
        if (args[0] == "echo")
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await input.CopyToAsync(output).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        if (args[0] == "describe")
        {
            var variables = Environment.GetEnvironmentVariables();
            var values = args.Skip(1).Concat(new[] { Environment.CurrentDirectory, Environment.GetEnvironmentVariable("STRANDS_PIPE_UNICODE") ?? "", variables.Contains("STRANDS_PIPE_EMPTY") ? (string)variables["STRANDS_PIPE_EMPTY"]! : "<absent>", variables.Contains("STRANDS_PIPE_REMOVED") ? "present" : "absent" });
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", values.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))));
            using var output = Console.OpenStandardOutput();
            await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            using var error = Console.OpenStandardError();
            var text = Encoding.UTF8.GetBytes("stderr é 🧵");
            await error.WriteAsync(text, 0, text.Length).ConfigureAwait(false);
            return 17;
        }
        if (args[0] == "marker") { File.WriteAllText(args[1], "executed"); return 0; }
        if (args[0] == "exit") return 23;
        if (args[0] == "hold") { File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); await Task.Delay(Timeout.Infinite).ConfigureAwait(false); }
        if (args[0] == "handle")
        {
            var handle = new IntPtr(long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
            var valid = GetHandleInformation(handle, out _);
            using var output = Console.OpenStandardOutput(); output.WriteByte(valid ? (byte)1 : (byte)0);
            return 0;
        }
        return 64;
    }

    private static async Task<int> PrivateAsync(string[] args)
    {
        if (args.Length != 5 || args[1] is not ("cp437" or "utf8-bom" or "utf8-no-bom") || args[3] is not ("bytes" or "original" or "describe" or "lifetime" or "startup" or "inheritance" or "cancel-start" or "mcp")) return 64;
        var ownsConsole = false;
        try
        {
            // The bootstrap loads the trusted test assembly's dependencies; plain child modes use only their own fixture dependencies.
            Environment.SetEnvironmentVariable("DOTNET_ADDITIONAL_DEPS", null);
            if (Path.DirectorySeparatorChar == '\\')
            {
#if NET8_0_OR_GREATER
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
                _ = FreeConsole();
                if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
                ownsConsole = true;
                _ = ShowWindow(GetConsoleWindow(), 0);
                Console.InputEncoding = args[1] == "cp437" ? Encoding.GetEncoding(437) : new UTF8Encoding(args[1] == "utf8-bom");
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[2]));
            var scenario = assembly.GetType("Strands.Agents.Tests.Processes.BinaryPipeScenarios", throwOnError: true)!;
            var method = scenario.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.Public)!;
            await (Task)method.Invoke(null, new object[] { args[3], Path.GetDirectoryName(Path.GetFullPath(args[4]))! })!;
            File.WriteAllText(args[4], "PASS|" + IntPtr.Size + "|" + Console.InputEncoding.CodePage + "|" + BitConverter.ToString(Console.InputEncoding.GetPreamble()) + "|pid=" + Process.GetCurrentProcess().Id + "|runtime=" + Environment.Version);
            return 0;
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
            File.WriteAllText(args[4], "FAIL|" + IntPtr.Size + "|" + error);
            return 1;
        }
        finally { if (ownsConsole) _ = FreeConsole(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
}
