using System.Diagnostics;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Mcp;
using Strands.Harness;
using Strands.Harness.Mcp;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class McpTaskHarnessCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HarnessOwnsTheConfiguredTaskConnectionAndItsRealStdioProcess(bool legacy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands harness mcp tasks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sentinel.txt"), "selected workdir Café 雪");
            var variable = "STRANDS_TASK_" + Guid.NewGuid().ToString("N");
            var server = Server(legacy) with
            {
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string> { [variable] = "explicit task environment" },
                TasksConfig = new()
                {
                    PollTimeout = TimeSpan.FromSeconds(20),
                    ProtocolProfile = legacy ? McpTasksProtocolProfile.Legacy20251125 : McpTasksProtocolProfile.Finalized20260728,
                    LegacyTimeToLive = legacy ? TimeSpan.FromSeconds(45) : null
                }
            };
            await using var agent = await HarnessFactory.CreateAsync(new()
            {
                Model = new UnusedModel(),
                BuiltinTools = [],
                BuiltinPlugins = [],
                Skills = new() { Enabled = false },
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Enabled = false },
                BackgroundTasks = null,
                McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions> { ["tasks"] = server }, continueOnError: false)
            });
            var tool = Assert.Single(agent.Tools);
            Assert.Equal("tasks_echo", tool.Spec.Name);
            var input = JsonSerializer.SerializeToElement(new { inspectRuntime = true, environment = variable, inherited = "STRANDS_TASK_NOT_INHERITED", sentinel = "sentinel.txt" });

            var result = await tool.InvokeAsync(input, new(agent, new("harness-task", tool.Spec.Name, input), new Dictionary<string, object?>()));

            using var document = JsonDocument.Parse(result);
            var details = document.RootElement;
            Assert.Equal("explicit task environment", details.GetProperty("environment").GetString());
            Assert.Equal("selected workdir Café 雪", details.GetProperty("sentinel").GetString());
            using var process = Process.GetProcessById(details.GetProperty("processId").GetInt32());
            await agent.DisposeAsync();
            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "The harness must dispose its owned native task connection and process.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static McpServerOptions Server(bool legacy)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = new[] { legacy ? "--legacy-tasks" : "--tasks" } };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture, legacy ? "--legacy-tasks" : "--tasks" } };
#endif
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test directly invokes the configured task tool.");
    }
}
