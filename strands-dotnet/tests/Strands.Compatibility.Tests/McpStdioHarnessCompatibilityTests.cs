using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using Strands.Harness.Mcp;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class McpStdioHarnessCompatibilityTests
{
    [Fact]
    public async Task NativeStdioFixturePreservesUtf8ArgumentsEnvironmentAndWorkingDirectoryThenExits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy mcp " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var explicitVariable = "STRANDS_EXPLICIT_" + Guid.NewGuid().ToString("N");
        var inheritedVariable = "STRANDS_PRIVATE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(inheritedVariable, "host-only");
        Process? process = null;
        try
        {
            const string sentinelName = "working-directory.txt";
            var sentinel = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, sentinelName), sentinel);
            await using var agent = await Bounded(HarnessFactory.CreateAsync(Options(Server(new[] { "argument with spaces", "Café ☕" }) with
            {
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string> { [explicitVariable] = "explicit Café ☕" },
            }) with
            { BackgroundTasks = null }));
            var tool = Assert.Single(agent.Tools);
            var input = JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = explicitVariable, inherited = inheritedVariable, sentinel = sentinelName });
            var result = await Bounded(tool.InvokeAsync(input, new(agent, new("inspect", tool.Spec.Name, input), new Dictionary<string, object?>())).AsTask());
            using var document = JsonDocument.Parse(result);
            var details = document.RootElement;
            Assert.Equal("explicit Café ☕", details.GetProperty("environment").GetString());
            Assert.Equal(JsonValueKind.Null, details.GetProperty("inherited").ValueKind);
            Assert.Equal(new[] { "argument with spaces", "Café ☕" }, details.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(sentinel, details.GetProperty("sentinel").GetString());
            Assert.Equal(sentinel, File.ReadAllText(Path.Combine(details.GetProperty("workingDirectory").GetString()!, sentinelName)));
            process = Process.GetProcessById(details.GetProperty("processId").GetInt32());

            await Bounded(agent.DisposeAsync().AsTask());

            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "Disposing the owner must terminate the actual stdio fixture process.");
        }
        finally
        {
            Cleanup(process);
            Environment.SetEnvironmentVariable(inheritedVariable, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelingInitializationClosesTheActualNativeStdioProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy mcp cancellation " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        Task<Agent>? construction = null;
        Process? process = null;
        try
        {
            var marker = Path.Combine(directory, "ready.pid");
            construction = HarnessFactory.CreateAsync(Options(Server(new[] { "--block-initialize", "--initialize-marker=" + marker })) with { BackgroundTasks = null }, cancellation.Token);
            await WaitForMarkerAsync(marker, construction);
            process = Process.GetProcessById(int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(construction));
            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "Cancellation must terminate the actual started stdio process.");
        }
        finally
        {
            cancellation.Cancel();
            if (construction is not null)
                await Record.ExceptionAsync(async () => { await using var unexpected = await Bounded(construction); });
            Cleanup(process);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HarnessOptions Options(McpServerOptions server) => new()
    {
        Model = new UnusedModel(),
        BuiltinTools = new string[0],
        BuiltinPlugins = new string[0],
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions> { ["stdio"] = server }, continueOnError: false),
    };

    private static McpServerOptions Server(string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture), "Build must stage the native .NET Framework stdio executable and its dependencies.");
        return new() { Command = fixture, Arguments = arguments };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture), "Build must stage the modern stdio fixture and its runtime metadata.");
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture }.Concat(arguments).ToArray() };
#endif
    }

    private static async Task WaitForMarkerAsync(string marker, Task construction)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(marker))
        {
            if (construction.IsCompleted) await construction;
            await Task.Delay(25, deadline.Token);
        }
    }

    private static async Task<T> Bounded<T>(Task<T> task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25))))) throw new TimeoutException("The real stdio fixture operation did not complete.");
        return await task;
    }
    private static async Task Bounded(Task task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25))))) throw new TimeoutException("The real stdio fixture operation did not complete.");
        await task;
    }
    private static void Cleanup(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); } }
        finally { process.Dispose(); }
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture directly invokes the discovered tool.");
    }
}
