using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents;
using Strands.Agents.Monty;

var architecture = RuntimeInformation.ProcessArchitecture.ToString();
if (args.Length != 1 || args[0] != architecture) throw new InvalidOperationException("The consumer has the wrong process architecture.");
var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
var rid = (windows ? "win-" : mac ? "osx-" : "linux-") + architecture.ToLowerInvariant();
var filename = "strands_monty_worker" + (windows ? ".exe" : string.Empty);
var worker = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", filename);
if (!File.Exists(worker)) throw new FileNotFoundException("The independently restored package did not copy its worker.", worker);
var caller = ProgrammaticToolCaller.Create();
var echo = new FunctionTool(new("echo", "An independently packaged host tool", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => new(input.GetProperty("value").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)));
await using (var agent = new Agent(new() { Model = new NoModel(), Tools = new[] { echo, caller } }))
{
    var result = await agent.InvokeToolAsync(new("consumer-call", caller.Spec.Name, JsonSerializer.SerializeToElement(new { code = "print(int(await echo(value=21)) * 2)" })));
    if (result.Status != ToolResultStatus.Success || result.Content != "42" || agent.Messages.Count != 0) throw new InvalidOperationException("Genuine packaged Monty tool orchestration failed: " + result.Content);
}
// The public tool has already exercised the package's default path. Execute that
// exact asset's version command to retain independent native architecture evidence.
using var process = new Process { StartInfo = new() { FileName = worker, Arguments = "--version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
if (!process.Start()) throw new IOException("The packaged native identity command failed to start.");
if (!process.WaitForExit(10000)) { process.Kill(); throw new IOException("The packaged native identity command exceeded its deadline."); }
var version = process.StandardOutput.ReadToEnd();
var stderr = process.StandardError.ReadToEnd();
if (process.ExitCode != 0) throw new IOException("The native worker identity command failed: " + stderr);
var match = Regex.Match(version.Trim(), "^strands_monty_worker ABI1 Monty0\\.0\\.23 (32|64)-bit$");
if (!match.Success) throw new InvalidDataException("Unexpected native engine identity: " + version);
var bits = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
if (bits != IntPtr.Size * 8) throw new InvalidOperationException("The native worker has the wrong pointer size.");
Console.WriteLine("Monty:real-python,host-tool,print-only;Engine:0.0.23;ABI:1;ProcessArchitecture:" + architecture + ";HostPointerSize:" + IntPtr.Size + ";WorkerPointerBits:" + bits);

internal sealed class NoModel : IModel
{
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model request is authorized by this consumer.");
}
