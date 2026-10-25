using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Strands.Sandbox.ProcessFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var mode = Environment.GetEnvironmentVariable("STRANDS_SANDBOX_FIXTURE_MODE") ?? "argv";
        if (mode == "reject-shell-start")
        {
            Console.Error.Write("strands-sandbox-fixture: reject-shell-start");
            return 23;
        }
        if (mode == "literal") { Console.Write(Environment.GetEnvironmentVariable("STRANDS_SANDBOX_FIXTURE_OUTPUT")); return 0; }
        if (mode == "sandbox-vending-directory")
        { Console.Write("tree\0" + "1\0" + "0\0"); return 0; }
        if (mode == "external-release-gate")
        {
            using var gate = new System.IO.Pipes.NamedPipeClientStream(".", Environment.GetEnvironmentVariable("STRANDS_SANDBOX_FIXTURE_GATE")!, System.IO.Pipes.PipeDirection.In, System.IO.Pipes.PipeOptions.Asynchronous);
            await gate.ConnectAsync().ConfigureAwait(false);
            var release = new byte[1];
            return await gate.ReadAsync(release, 0, 1).ConfigureAwait(false) == 1 && release[0] == 71 ? 0 : 29;
        }
        if (mode is "orphan" or "orphanHang")
        {
            var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName!) { UseShellExecute = false, CreateNoWindow = true };
            start.EnvironmentVariables["STRANDS_SANDBOX_FIXTURE_MODE"] = "hang";
            using var child = Process.Start(start) ?? throw new IOException("Could not start the owned pipe fixture.");
            File.WriteAllText(Environment.GetEnvironmentVariable("STRANDS_SANDBOX_FIXTURE_PID_FILE")!, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine("orphan:" + child.Id); Console.Out.Flush();
            if (mode == "orphanHang") await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
            return 0;
        }
        if (mode == "hang")
        {
            Console.WriteLine("ready:" + Process.GetCurrentProcess().Id);
            Console.Out.Flush();
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
        }
        if (mode == "invalidUtf8")
        {
            using var output = Console.OpenStandardOutput();
            output.WriteByte(255); output.Flush(); return 0;
        }
        if (mode == "unicode")
        {
            for (var index = 0; index < 100; index++) { Console.Out.Write("é🧵"); Console.Error.Write("é🧵"); }
            return 0;
        }
        using var input = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(input).ConfigureAwait(false);
        if (mode == "stdin") Console.Write(Convert.ToBase64String(input.ToArray()));
        else Console.Write(JsonSerializer.Serialize(new { args, stdin = Convert.ToBase64String(input.ToArray()) }));
        return int.TryParse(Environment.GetEnvironmentVariable("STRANDS_SANDBOX_FIXTURE_EXIT"), out var code) ? code : 0;
    }
}
