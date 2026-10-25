using System.Diagnostics;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Mcp;
using Xunit.Abstractions;

namespace Strands.Harness.Tests;

public sealed class McpStdioHarnessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealStdioConnectionPreservesArgumentsWorkingDirectoryAndExplicitEnvironmentThenClosesItsProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands mcp working directory " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var explicitVariable = "STRANDS_MCP_EXPLICIT_" + Guid.NewGuid().ToString("N");
        var inheritedVariable = "STRANDS_MCP_PRIVATE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(inheritedVariable, "host-only-value");
        var started = DateTime.UtcNow;
        Process? process = null;
        Exception? failure = null;
        try
        {
            const string sentinelName = "working-directory.txt";
            var sentinel = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(directory, sentinelName), sentinel);
            await using var agent = await Harness.CreateAsync(Options(new()
            {
                Command = Dotnet(),
                Arguments = [Fixture(), "argument with spaces", "Café ☕"],
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string> { [explicitVariable] = "explicit value" },
            }) with
            { BackgroundTasks = null }).WaitAsync(TimeSpan.FromSeconds(20));
            var tool = Assert.Single(agent.Tools);
            var input = JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = explicitVariable, inherited = inheritedVariable, sentinel = sentinelName });
            var result = await tool.InvokeAsync(input, new(agent, new("inspect", tool.Spec.Name, input), new Dictionary<string, object?>())).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            using var runtime = JsonDocument.Parse(result);
            var details = runtime.RootElement;
            Assert.Equal("explicit value", details.GetProperty("environment").GetString());
            Assert.Equal(JsonValueKind.Null, details.GetProperty("inherited").ValueKind);
            Assert.Equal(new[] { "argument with spaces", "Café ☕" }, details.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(sentinel, details.GetProperty("sentinel").GetString());
            Assert.Equal(sentinel, await File.ReadAllTextAsync(Path.Combine(details.GetProperty("workingDirectory").GetString()!, sentinelName)));
            process = Process.GetProcessById(details.GetProperty("processId").GetInt32());
            await agent.DisposeAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            failure = error;
            ReportProcesses(error, process, started);
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedVariable, null);
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup)
            {
                if (failure is null) { ReportProcesses(cleanup, process, started); throw; }
                var errors = failure.Data["Strands.CleanupErrors"] is IEnumerable<Exception> prior ? prior.ToList() : [];
                errors.Add(cleanup); failure.Data["Strands.CleanupErrors"] = errors;
                ReportProcesses(failure, process, started);
            }
            finally { process?.Dispose(); }
        }
    }

    private void ReportProcesses(Exception failure, Process? child, DateTime started)
    {
        try
        {
            var observations = new List<string>();
            if (child is not null) observations.Add("child: " + Describe(child));
            if (OperatingSystem.IsWindows())
                foreach (var wrapper in Process.GetProcessesByName("cmd"))
                    using (wrapper)
                        if (observations.Count < 17 && wrapper.StartTime.ToUniversalTime() >= started)
                            observations.Add("concurrent cmd candidate, ownership unproven: " + Describe(wrapper));
            failure.Data["Strands.StdioFixture.Processes"] = observations.ToArray();
            output.WriteLine("stdio cleanup diagnosis: " + JsonSerializer.Serialize(observations));
        }
        catch (Exception diagnostic) { try { output.WriteLine("stdio process diagnosis failed: " + diagnostic.GetType().Name); } catch { } }

        static string Describe(Process value)
        {
            var exited = value.HasExited;
            return "pid=" + value.Id + ", exited=" + exited + (exited ? ", exitCode=" + value.ExitCode : "");
        }
    }

    [Fact]
    public async Task CancellationDuringStdioInitializationClosesTheStartedProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands mcp cancellation " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        Task<Agent>? construction = null;
        try
        {
            var marker = Path.Combine(directory, "ready.pid");
            construction = Harness.CreateAsync(Options(new()
            {
                Command = Dotnet(),
                Arguments = [Fixture(), "--block-initialize", "--initialize-marker=" + marker],
            }) with
            { BackgroundTasks = null }, cancellation.Token);
            await WaitForMarkerAsync(marker, construction);
            using var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => construction.WaitAsync(TimeSpan.FromSeconds(15)));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cancellation.Cancel();
            if (construction is not null)
                await Record.ExceptionAsync(async () =>
                {
                    await using var unexpected = await construction.WaitAsync(TimeSpan.FromSeconds(15));
                });
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HarnessOptions Options(McpServerOptions server) => new()
    {
        Model = new UnusedModel(),
        BuiltinTools = [],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions> { ["stdio"] = server }, continueOnError: false),
    };

    private static async Task WaitForMarkerAsync(string marker, Task construction)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker))
        {
            if (construction.IsCompleted) await construction;
            await Task.Delay(25, deadline.Token);
        }
    }

    private static string Dotnet()
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static string Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(path), "The build-only stdio fixture must be copied beside the test assembly.");
        return path;
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test invokes discovered tools directly.");
    }
}
