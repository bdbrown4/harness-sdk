using System.Diagnostics;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;
using Xunit;

namespace Strands.Harness.Tests;

public sealed class McpProviderDeclarationTests
{
    [Fact]
    public async Task DeclarationCreationSnapshotsHeadersPrefixFiltersAndExpandedEnvironmentBeforeConnecting()
    {
        await using var original = new McpTestServer();
        await using var replacement = new McpTestServer();
        var variable = "STRANDS_DECLARATION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "expanded");
        try
        {
            var headers = new Dictionary<string, string> { ["x-api-key"] = "snapshot" };
            var allowed = new List<string> { "^echo" };
            var definitions = new Dictionary<string, McpServerOptions>
            { ["docs"] = new() { Url = original.Endpoint.AbsoluteUri, Prefix = "${" + variable + "}", Headers = headers, ToolFilters = new() { Allowed = allowed } } };
            var configuration = McpServerConfiguration.FromServers(definitions, continueOnError: false);
            definitions["docs"] = new() { Url = replacement.Endpoint.AbsoluteUri }; headers["x-api-key"] = "mutated"; allowed.Clear();
            var declarations = await configuration.CreateToolProvidersAsync(new LocalFileSandbox());
            Assert.Equal(0, original.Initializations); Assert.Equal(0, replacement.Initializations);
            Assert.Equal("docs", Assert.Single(declarations).Name);
            Environment.SetEnvironmentVariable(variable, "later");
            await using var agent = new Agent(new() { Model = new McpProviderHarnessTests.UnusedModel(), ToolProviders = declarations });
            await agent.InitializeAsync();
            Assert.Equal("expanded_echo", Assert.Single(agent.Tools).Spec.Name);
            var result = await agent.InvokeToolAsync(new("snapshot", "expanded_echo", JsonSerializer.SerializeToElement(new { value = "preserved" })));
            Assert.Equal("preserved", result.Content); Assert.Equal("snapshot", original.LastCallApiKey); Assert.Equal(0, replacement.Initializations);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task SelectedSandboxFileIsReadOnceAndResolvedDeclarationsPreserveOrderAndDisabledPolicy()
    {
        await using var first = new McpTestServer(); await using var second = new McpTestServer();
        var sandbox = new ConfigurationSandbox(JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["second"] = new { url = second.Endpoint.AbsoluteUri },
                ["disabled"] = new { disabled = true, command = "${STRANDS_MISSING_DECLARATION_VARIABLE}" },
                ["first"] = new { url = first.Endpoint.AbsoluteUri }
            }
        }));
        var declarations = await McpServerConfiguration.FromFile("virtual-servers.json").CreateToolProvidersAsync(sandbox);
        Assert.Equal(1, sandbox.Reads); Assert.Equal(new[] { "second", "first" }, declarations.Select(provider => provider.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<Strands.Agents.Mcp.McpToolProvider>)declarations).Clear());
        Assert.Equal(0, first.Initializations); Assert.Equal(0, second.Initializations);
        sandbox.Body = "invalid new source";
        await using var agent = new Agent(new() { Model = new McpProviderHarnessTests.UnusedModel(), ToolProviders = declarations });
        await agent.InitializeAsync();
        Assert.Equal(new[] { "second_echo", "first_echo" }, agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal(1, sandbox.Reads); Assert.Equal(0, sandbox.Writes);
    }

    [Fact]
    public async Task PrecancelledDeclarationCreationDoesNotReadTheSelectedSandbox()
    {
        var sandbox = new ConfigurationSandbox("{}"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpServerConfiguration.FromFile("unused.json").CreateToolProvidersAsync(sandbox, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Equal(0, sandbox.Reads);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("finalized")]
    [InlineData("legacy")]
    public async Task RealStdioDeclarationsDetachArgumentsEnvironmentWorkingDirectoryAndTaskProfile(string profile)
    {
        var directory = Path.Combine(Strands.Tests.TestPaths.GetPhysicalTempPath(), "strands provider declaration " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var explicitName = "STRANDS_DECLARATION_EXPLICIT_" + Guid.NewGuid().ToString("N");
        var arguments = new List<string>();
#if NETFRAMEWORK
        var command = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
#else
        var command = Path.Combine(new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!.FullName,
            Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet");
        arguments.Add(Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll"));
#endif
        if (profile != "ordinary") arguments.Add(profile == "finalized" ? "--tasks" : "--legacy-tasks");
        arguments.Add("literal Café 雪"); var expectedArguments = arguments.Skip(Path.GetFileName(command).StartsWith("dotnet", StringComparison.Ordinal) ? 1 : 0).ToArray();
        var environment = new Dictionary<string, string> { [explicitName] = "original Café 雪" };
        Agent? agent = null; Process? process = null;
        try
        {
            File.WriteAllText(Path.Combine(directory, "sentinel.txt"), "directory snapshot");
            var declarations = await McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["stdio"] = new()
                {
                    Command = command,
                    Arguments = arguments,
                    Environment = environment,
                    WorkingDirectory = directory,
                    TasksConfig = profile == "ordinary" ? null : new()
                }
            }, continueOnError: false).CreateToolProvidersAsync(new LocalFileSandbox());
            arguments.Clear(); arguments.Add("must not launch"); environment[explicitName] = "mutated";
            agent = new(new() { Model = new McpProviderHarnessTests.UnusedModel(), ToolProviders = declarations }); await agent.InitializeAsync();
            var result = await agent.InvokeToolAsync(new("inspect", "stdio_echo", JsonSerializer.SerializeToElement(new
            { inspectRuntime = true, environment = explicitName, inherited = "STRANDS_NOT_INHERITED_" + Guid.NewGuid().ToString("N"), sentinel = "sentinel.txt" })));
            Assert.Equal(ToolResultStatus.Success, result.Status); using var doc = JsonDocument.Parse(result.Content); var details = doc.RootElement;
            Assert.Equal("original Café 雪", details.GetProperty("environment").GetString()); Assert.Equal(expectedArguments, details.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("directory snapshot", details.GetProperty("sentinel").GetString()); Assert.Equal(directory, details.GetProperty("workingDirectory").GetString());
            Assert.Empty(details.GetProperty("coordinationEnvironment").EnumerateArray()); process = Process.GetProcessById(details.GetProperty("processId").GetInt32());
            await agent.DisposeAsync(); Assert.True(process.HasExited, "Final declaration consumer removal must physically join the stdio process.");
        }
        finally { if (agent is not null) await agent.DisposeAsync(); process?.Dispose(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HarnessBorrowsTheSelectedSandboxAndClosesOnlyItsOwnedConnection()
    {
        await using var server = new McpTestServer();
        var sandbox = new ConfigurationSandbox(JsonSerializer.Serialize(new { docs = new { url = server.Endpoint.AbsoluteUri } }));
        var agent = await Harness.CreateAsync(McpProviderHarnessTests.Options() with
        { Sandbox = sandbox, Skills = new() { Enabled = false }, McpServers = McpServerConfiguration.FromFile("virtual-servers.json", continueOnError: false) });
        await agent.DisposeAsync();
        Assert.Equal(1, sandbox.Reads); Assert.False(sandbox.Disposed); Assert.Equal(1, server.Deletions);
    }

    private sealed class ConfigurationSandbox(string body) : ISandbox, IDisposable
    {
        internal string Body { get; set; } = body;
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Reads++; return new(Body); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) { Writes++; throw new NotSupportedException(); }
    }
}
