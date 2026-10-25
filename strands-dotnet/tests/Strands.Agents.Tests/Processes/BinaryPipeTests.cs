using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Strands.Agents.Tests.Processes;

public sealed class BinaryPipeTests(Xunit.Abstractions.ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("cp437", "bytes")]
    [InlineData("utf8-bom", "bytes")]
    [InlineData("utf8-no-bom", "bytes")]
    [InlineData("cp437", "original")]
    [InlineData("utf8-bom", "original")]
    [InlineData("utf8-no-bom", "original")]
    [InlineData("utf8-bom", "describe")]
    [InlineData("utf8-bom", "lifetime")]
    [InlineData("utf8-bom", "startup")]
    [InlineData("utf8-bom", "inheritance")]
    [InlineData("utf8-bom", "cancel-start")]
    [InlineData("cp437", "mcp")]
    [InlineData("utf8-bom", "mcp")]
    [InlineData("utf8-no-bom", "mcp")]
    public async Task BinaryProtocolsAreIndependentOfTheHostsConsoleEncoding(string profile, string scenario)
    {
        var input = Console.InputEncoding;
        var output = Console.OutputEncoding;
        var root = Path.Combine(Strands.Tests.TestPaths.GetPhysicalTempPath(), "strands-pipes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = Path.Combine(root, "result.txt");
            var start = BinaryPipeScenarios.StartInfo("private", profile, typeof(BinaryPipeScenarios).Assembly.Location, scenario, result);
#if NET8_0_OR_GREATER
            start.EnvironmentVariables["DOTNET_ADDITIONAL_DEPS"] = Path.ChangeExtension(typeof(BinaryPipeScenarios).Assembly.Location, ".deps.json");
#endif
            // No redirected stdin on this bootstrap: only the owned private console changes its own encoding.
            start.RedirectStandardInput = false;
            using var child = Process.Start(start) ?? throw new IOException("The private-console fixture did not start.");
            var bootstrapOutput = child.StandardOutput.ReadToEndAsync();
            var bootstrapError = child.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => child.WaitForExit(90_000));
            if (!exited) { child.Kill(); child.WaitForExit(); }
            Assert.True(exited, "The private-console scenario did not finish.");
            var evidence = File.Exists(result) ? File.ReadAllText(result) : "missing result";
            testOutput.WriteLine(evidence);
            testOutput.WriteLine(await bootstrapOutput);
            testOutput.WriteLine(await bootstrapError);
            var cases = Path.Combine(root, "case-evidence.log");
            if (File.Exists(cases)) testOutput.WriteLine(File.ReadAllText(cases));
            Assert.True(child.ExitCode == 0, evidence);
            Assert.StartsWith("PASS|" + IntPtr.Size + "|", evidence);
            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.Contains(profile == "cp437" ? "|437||" : profile == "utf8-bom" ? "|65001|EF-BB-BF|" : "|65001||", evidence);
            }
            Assert.Equal(input.CodePage, Console.InputEncoding.CodePage);
            Assert.Equal(input.GetPreamble(), Console.InputEncoding.GetPreamble());
            Assert.Equal(output.CodePage, Console.OutputEncoding.CodePage);
            Assert.Equal(output.GetPreamble(), Console.OutputEncoding.GetPreamble());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public static class BinaryPipeScenarios
{
    public static async Task RunAsync(string scenario, string root)
    {
        switch (scenario)
        {
            case "bytes":
                foreach (var data in new[] { Array.Empty<byte>(), new byte[] { 239, 187, 191, 0, 255, 13, 10 }, Enumerable.Range(0, 1_500_000).Select(index => (byte)(index * 31)).ToArray() })
                {
                    var result = await ExchangeAsync(StartInfo("echo"), data);
                    Assert.Equal(data, result.Output); Assert.Equal(0, result.Code);
                }
                break;
            case "original":
                var cases = new[]
                {
                    ("Strands.Agents.Tests.Sandbox.SandboxContractTests", "CombinedUtf8CaptureCannotExceedTheApplicationPolicy"),
                    ("Strands.Agents.Tests.Sandbox.SandboxContractTests", "RemoteBinaryReadsAreByteExactAndTextDecodingIsStrict"),
                    ("Strands.Agents.Tests.Sandbox.SandboxContractTests", "RealClientProcessAcceptsLargeDetachedStdinAndReportsNonzeroExit"),
                    ("Strands.Harness.Tests.HarnessProgrammaticToolTests", "FactoryRegistersTheGenuineCallerInTheSharedDefaultPosition"),
                    ("Strands.Harness.Tests.HarnessProgrammaticToolTests", "PortableSettingsSelectToolsAndApplyTheDeclaredWallDeadline"),
                };
                foreach (var (typeName, methodName) in cases)
                {
                    var type = typeof(BinaryPipeScenarios).Assembly.GetType(typeName);
                    if (type is null) { Assert.Equal("Strands.Agents.Tests", typeof(BinaryPipeScenarios).Assembly.GetName().Name); continue; }
                    await (Task)type.GetMethod(methodName)!.Invoke(Activator.CreateInstance(type), null)!;
                    File.AppendAllText(Path.Combine(root, "case-evidence.log"), "PASS " + typeName + "." + methodName + Environment.NewLine);
                }
                break;
            case "mcp":
                await new Mcp.McpStdioLifetimeTests(new ScenarioOutput(root)).NativeRuntimeRetainsUtf8SeparatedArgumentsSelectedEnvironmentAndNoReleaseVariable();
                await new Mcp.McpTaskLifetimeTests().RealStdioTaskCompletesAndItsOwnedProcessExitsOnDisposal();
                await new Mcp.McpLegacyTaskLifetimeTests().RealLegacyStdioDispatchesPendingElicitationDuringTheResultWait();
                File.AppendAllText(Path.Combine(root, "case-evidence.log"), "PASS ordinary/finalized/legacy MCP calls and owned-process joins" + Environment.NewLine);
                break;
            case "describe":
                var directory = Path.Combine(root, "cwd é 🧵"); Directory.CreateDirectory(directory);
                var arguments = new[] { "", "a b", "quote\"value", "trailing\\", "é 🧵", "%PATH% &|<>^!" };
                var start = StartInfo(new[] { "describe" }.Concat(arguments).ToArray());
                var binaries = Path.Combine(root, "工具 bin"); Directory.CreateDirectory(binaries);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "Strands.BinaryPipe.ProcessFixture*")) File.Copy(file, Path.Combine(binaries, Path.GetFileName(file)));
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "System.Text.Encoding.CodePages.dll", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(binaries, file.Substring(AppContext.BaseDirectory.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
                }
                var executable = Path.Combine(binaries, "child é 🧵" + (Path.DirectorySeparatorChar == '\\' ? ".exe" : ""));
                File.Copy(start.FileName, executable);
                if (File.Exists(start.FileName + ".config")) File.Copy(start.FileName + ".config", executable + ".config");
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(start.FileName));
#endif
                start.FileName = executable;
                start.WorkingDirectory = directory;
                start.EnvironmentVariables["STRANDS_PIPE_UNICODE"] = "环境 é 🧵";
                start.EnvironmentVariables["STRANDS_PIPE_EMPTY"] = "";
                start.EnvironmentVariables["STRANDS_PIPE_REMOVED"] = "removed";
                start.EnvironmentVariables.Remove("STRANDS_PIPE_REMOVED");
                var described = await ExchangeAsync(start, Array.Empty<byte>());
                var values = Encoding.UTF8.GetString(described.Output).Split('\n').Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value))).ToArray();
                Assert.Equal(arguments.Concat(new[] { directory, "环境 é 🧵", "", "absent" }), values);
                Assert.Equal("stderr é 🧵", described.Error); Assert.Equal(17, described.Code);
                break;
            case "lifetime":
                var pending = Enumerable.Range(0, 6).Select(async index =>
                {
                    var value = Encoding.UTF8.GetBytes("parallel:" + index);
                    Assert.Equal(value, (await ExchangeAsync(StartInfo("echo"), value)).Output);
                });
                await Task.WhenAll(pending);
                using (var process = new Strands.Agents.Processes.OwnedChildProcess { StartInfo = StartInfo("exit") })
                {
                    Assert.True(process.Start()); Assert.True(await Task.Run(() => process.WaitForExit(10_000))); Assert.Equal(23, process.ExitCode);
                    await process.StandardInput.BaseStream.FlushAsync();
                    await Assert.ThrowsAsync<IOException>(async () => await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, 0, 1));
                }
                var existing = new Sandbox.SandboxContractTests();
                await existing.CancellationAfterIncrementalReadinessReapsTheOwnedClient();
                await existing.DisposingAnIncrementalReaderReapsTheOwnedClient();
                await existing.TimeoutIsBoundedAndDoesNotRequireStartupOutput();
                break;
            case "startup":
                using (var missing = new Strands.Agents.Processes.OwnedChildProcess { StartInfo = new(Path.Combine(root, "missing.exe")) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } })
                    Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => missing.Start());
#if NET48
                var marker = Path.Combine(root, "must-not-run.txt");
                var primary = new IOException("attachment failed before release");
                var pid = 0;
                var failure = Assert.Throws<IOException>(() => Strands.Agents.Processes.WindowsChildProcess.Start(StartInfo("marker", marker), (process, _, _, _) => { pid = process.Id; throw primary; }));
                Assert.Same(primary, failure); Assert.False(File.Exists(marker)); AssertExited(pid);
                using (var current = Process.GetCurrentProcess())
                {
                    var before = current.HandleCount;
                    for (var index = 0; index < 20; index++)
                    {
                        Assert.Same(primary, Assert.Throws<IOException>(() => Strands.Agents.Processes.WindowsChildProcess.Start(StartInfo("marker", marker), (process, _, _, _) => { pid = process.Id; throw primary; })));
                        AssertExited(pid);
                    }
                    current.Refresh();
                    Assert.InRange(current.HandleCount, 0, before + 2);
                }
#endif
                break;
            case "inheritance":
#if NET48
                var pipes = Enumerable.Range(0, 128).Select(_ => new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out, HandleInheritability.Inheritable)).ToArray();
                try
                {
                    var handle = pipes[pipes.Length - 1].ClientSafePipeHandle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Equal(new byte[] { 0 }, (await ExchangeAsync(StartInfo("handle", handle), Array.Empty<byte>())).Output);
                }
                finally { foreach (var pipe in pipes) pipe.Dispose(); }
#endif
                break;
            case "cancel-start":
                using (var cancellation = new CancellationTokenSource())
                using (var process = new Strands.Agents.Processes.OwnedChildProcess { StartInfo = StartInfo("marker", Path.Combine(root, "pre-cancelled.txt")) })
                {
                    cancellation.Cancel();
                    Assert.Equal(cancellation.Token, Assert.ThrowsAny<OperationCanceledException>(() => process.Start(cancellation.Token)).CancellationToken);
                    Assert.False(File.Exists(Path.Combine(root, "pre-cancelled.txt")));
                }
#if NET48
                CancelDuringAttachment(root);
                using (var current = Process.GetCurrentProcess())
                {
                    var before = current.HandleCount;
                    for (var index = 0; index < 20; index++) CancelDuringAttachment(root);
                    current.Refresh(); Assert.InRange(current.HandleCount, 0, before + 2);
                }
#endif
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private sealed class ScenarioOutput(string root) : Xunit.Abstractions.ITestOutputHelper
    {
        public void WriteLine(string message) => File.AppendAllText(Path.Combine(root, "case-evidence.log"), message + Environment.NewLine);
        public void WriteLine(string format, params object[] args) => WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
    }

    internal static ProcessStartInfo StartInfo(params string[] arguments)
    {
        var name = "Strands.BinaryPipe.ProcessFixture";
#if NET48
        name += IntPtr.Size == 4 ? ".x86" : ".x64";
#endif
        var file = Path.Combine(AppContext.BaseDirectory, name + (Path.DirectorySeparatorChar == '\\' ? ".exe" : ""));
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
#if NET8_0_OR_GREATER
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
#else
        start.Arguments = string.Join(" ", arguments.Select(Quote));
#endif
        return start;
    }
    private static async Task<(byte[] Output, string Error, int Code)> ExchangeAsync(ProcessStartInfo start, byte[] input)
    {
        using var process = new Strands.Agents.Processes.OwnedChildProcess { StartInfo = start };
        Assert.True(process.Start());
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        var operation = CompleteAsync();
        try
        {
            Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(15_000)));
            return await operation;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            Assert.True(process.WaitForExit(10_000));
            process.StandardInput.Close();
        }
        async Task<(byte[] Output, string Error, int Code)> CompleteAsync()
        {
            await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length);
            await process.StandardInput.BaseStream.FlushAsync(); process.StandardInput.Close();
            await Task.WhenAll(read, error);
            Assert.True(await Task.Run(() => process.WaitForExit(10_000)));
            return (output.ToArray(), await error, process.ExitCode);
        }
    }
#if NET48
    private static void CancelDuringAttachment(string root)
    {
        using var cancellation = new CancellationTokenSource();
        var marker = Path.Combine(root, "cancelled-" + Guid.NewGuid().ToString("N") + ".txt");
        Process? child = null; Stream? input = null, output = null, error = null;
        var pid = 0;
        Exception? failure = null;
        try
        {
            Strands.Agents.Processes.WindowsChildProcess.Start(StartInfo("marker", marker), (process, stdin, stdout, stderr) =>
            {
                child = process; pid = process.Id; input = stdin; output = stdout; error = stderr;
                cancellation.Cancel();
            }, cancellation.Token);
        }
        catch (Exception caught) { failure = caught; }
        if (failure is null)
        {
            try { Assert.True(child!.WaitForExit(10_000)); }
            finally
            {
                if (!child!.HasExited) { child.Kill(); child.WaitForExit(); }
                input!.Dispose(); output!.Dispose(); error!.Dispose(); child.Dispose();
            }
        }
        File.AppendAllText(Path.Combine(root, "case-evidence.log"), "cancel-during-attachment: error=" + (failure?.GetType().Name ?? "none") + "; marker=" + File.Exists(marker) + "; pid=" + pid + Environment.NewLine);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        Assert.False(File.Exists(marker)); AssertExited(pid);
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void AssertExited(int pid)
    {
        Assert.True(pid > 0);
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
#endif
}
