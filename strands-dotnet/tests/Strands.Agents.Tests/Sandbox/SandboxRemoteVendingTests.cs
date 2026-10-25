using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxRemoteVendingTests
{
    [Theory]
    [InlineData("docker")]
    [InlineData("ssh")]
    public void SelectedRemoteEnvironmentVendsFreshOrderedBoundToolSpecifications(string kind)
    {
        Core.Sandbox selected = kind == "docker"
            ? new Core.DockerSandbox(new() { Container = "selected-container", WorkingDirectory = "/remote/work" })
            : new Core.SshSandbox(new() { Host = "operator@example.test", WorkingDirectory = "/remote/work" });
        var first = selected.GetTools();
        Assert.Equal(new[] { "sandbox_file_editor", "sandbox_shell" }, first.Select(tool => tool.Spec.Name));
        var second = selected.GetTools();
        Assert.NotSame(first[0], second[0]);
        Assert.NotSame(first[1], second[1]);
        Assert.Equal(Core.SandboxFileEditor.DefaultDescription + (kind == "docker" ? " Files are in Docker container \"selected-container\"." : " Files are on host \"operator@example.test\"."), first[0].Spec.Description);
        Assert.Equal(Core.SandboxShell.DefaultDescription + (kind == "docker" ? " Runs in Docker container \"selected-container\"." : " Runs on host \"operator@example.test\".") + " Working directory: /remote/work.", first[1].Spec.Description);
        Assert.Equal("object", first[0].Spec.InputSchema.GetProperty("type").GetString());
        Assert.Equal("object", first[1].Spec.InputSchema.GetProperty("type").GetString());
        // Metadata only: never invoke or enumerate either actual remote environment.
    }

    [Fact]
    public async Task DefaultLocalAndBaseVendingStayEmptyWithoutCallingEnvironmentCapabilities()
    {
        Assert.Empty(new Core.NotASandboxLocalEnvironment().GetTools());
        var selected = new NoOperationSandbox();
        Assert.Empty(selected.GetTools());
        await using var agent = new Agent(new() { Model = new NoModel(), Sandbox = selected });
        await agent.InitializeAsync();
        Assert.Empty(agent.Tools);
        Assert.Equal(0, selected.Calls);
    }

    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Metadata tests must not invoke a model or process.");
    }
    private sealed class NoOperationSandbox : Core.Sandbox
    {
        internal int Calls;
        private Exception NoOperation() { Calls++; return new InvalidOperationException("No environment operation may run during vending metadata tests."); }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw NoOperation();
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw NoOperation();
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw NoOperation();
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw NoOperation();
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw NoOperation();
        public override ValueTask<IReadOnlyList<Strands.Agents.Files.FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw NoOperation();
    }
}
