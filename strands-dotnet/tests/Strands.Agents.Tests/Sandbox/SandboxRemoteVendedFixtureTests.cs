using System.Text.Json;
using Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

// ROOT-OWNED RUNNER ONLY. These tests launch the existing owned CLI fixture.
// They never connect to Docker/SSH services or execute the supplied command.
public sealed class SandboxRemoteVendedFixtureTests
{
    [Theory]
    [InlineData("docker")]
    [InlineData("ssh")]
    public async Task VendedShellPreservesSelectedClientAuthorityCwdAndNonzeroResult(string kind)
    {
        var selected = Select(kind, Fixture("argv", "23"));
        await using var agent = new Agent(new() { Model = new NoModel(), Sandbox = selected });
        await agent.InitializeAsync();
        var tool = agent.Tools.Single(value => value.Spec.Name == "sandbox_shell");
        const string command = "literal ' command $HOME\nnext";
        var result = await agent.InvokeToolAsync(new("selected-shell", tool.Spec.Name, JsonSerializer.SerializeToElement(new { command })));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        using var output = JsonDocument.Parse(result.Content);
        Assert.Equal(23, output.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("", output.RootElement.GetProperty("error").GetString());
        using var wire = JsonDocument.Parse(output.RootElement.GetProperty("output").GetString()!);
        Assert.Equal("", wire.RootElement.GetProperty("stdin").GetString());
        var argv = wire.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(kind == "docker"
            ? new[] { "exec", "-w", "/selected's cwd", "--", "selected 'container", "sh", "-c", command }
            : new[] { "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes", "-p", "2222", "-i", "private key 'literal", "-o", "ConnectTimeout=5", "--", "selected 'host", "cd '/selected'\\''s cwd' && " + command }, argv);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("ssh")]
    public async Task VendedEditorConsumesActualNulDirectoryTransportThroughItsSelectedClient(string kind)
    {
        var selected = Select(kind, Fixture("sandbox-vending-directory", "0"));
        await using var agent = new Agent(new() { Model = new NoModel(), Sandbox = selected });
        await agent.InitializeAsync();
        var tool = agent.Tools.Single(value => value.Spec.Name == "sandbox_file_editor");
        var result = await agent.InvokeToolAsync(new("selected-directory", tool.Spec.Name, JsonSerializer.SerializeToElement(new { command = "view", path = "/work/tree" })));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("Here's the files and directories up to 2 levels deep in /work/tree, excluding hidden items:\ntree\ntree/tree\ntree/tree/tree\n", result.Content);
    }

    private static Strands.Agents.Sandbox.Sandbox Select(string kind, SandboxProcessOptions process) => kind == "docker"
        ? new DockerSandbox(new() { Container = "selected 'container", WorkingDirectory = "/selected's cwd", Process = process })
        : new SshSandbox(new() { Host = "selected 'host", WorkingDirectory = "/selected's cwd", IdentityFile = "private key 'literal", Port = 2222, SshOptions = ["ConnectTimeout=5"], Process = process });
    private static SandboxProcessOptions Fixture(string mode, string exit) => new()
    {
        ExecutablePath = Path.Combine(AppContext.BaseDirectory, "SandboxProcessFixture", "Strands.Sandbox.ProcessFixture" + (Path.DirectorySeparatorChar == '\\' ? ".exe" : "")),
        Environment = new Dictionary<string, string> { ["STRANDS_SANDBOX_FIXTURE_MODE"] = mode, ["STRANDS_SANDBOX_FIXTURE_EXIT"] = exit },
    };
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Owned client fixtures must never invoke a model, remote service or supplied shell command.");
    }
}
