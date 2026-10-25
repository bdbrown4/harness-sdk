using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxVendedToolChildTests
{
    [Fact]
    public async Task ReboundChildVendsItsOwnWrapperInsteadOfTheParentBoundWrapper()
    {
        var first = new OwnerSandbox("parent"); var second = new OwnerSandbox("child");
        await using var parent = Make(first); await parent.InitializeAsync(); var parentTool = Assert.Single(parent.Tools);
        await using var child = parent.CreateChild(options => options with { Sandbox = second });
        await child.InitializeAsync();
        Assert.Same(second, child.Sandbox);
        var childTool = Assert.Single(child.Tools); Assert.NotSame(parentTool, childTool);
        Assert.Equal("child", await Invoke(childTool, child));
        Assert.Equal(1, first.GetToolsCalls); Assert.Equal(1, second.GetToolsCalls);
        Assert.Equal(0, first.EnvironmentCalls); Assert.Equal(0, second.EnvironmentCalls);
    }

    [Fact]
    public async Task ResetChildDoesNotInheritItsParentsEnvironmentBoundTools()
    {
        var selected = new OwnerSandbox("parent");
        await using var parent = Make(selected); await parent.InitializeAsync(); Assert.Single(parent.Tools);
        await using var child = parent.CreateChild(options => options with { Sandbox = null });
        Assert.IsType<Core.NotASandboxLocalEnvironment>(child.Sandbox); // Object only; no execution.
        await child.InitializeAsync();
        Assert.Empty(child.Tools);
        Assert.Equal(1, selected.GetToolsCalls); Assert.Equal(0, selected.EnvironmentCalls);
    }

    [Fact]
    public async Task ExplicitUserReplacementStaysInheritedWhenTheChildRebindsItsSandbox()
    {
        var first = new OwnerSandbox("parent"); var second = new OwnerSandbox("child");
        await using var parent = Make(first); await parent.InitializeAsync();
        var user = Tool("user"); parent.ReplaceApplicationTool(user);
        await using var child = parent.CreateChild(options => options with { Sandbox = second }); await child.InitializeAsync();
        Assert.Same(user, Assert.Single(child.Tools)); Assert.Equal("user", await Invoke(user, child));
        Assert.Equal(0, first.EnvironmentCalls); Assert.Equal(0, second.EnvironmentCalls);
    }

    [Fact]
    public async Task ExplicitReAddingTheSameVendedObjectDeclaresUserAuthorityForChildren()
    {
        var first = new OwnerSandbox("parent"); var second = new OwnerSandbox("child");
        await using var parent = Make(first); await parent.InitializeAsync();
        var explicitUserChoice = Assert.Single(parent.Tools); parent.AddOrReplaceApplicationTool(explicitUserChoice);
        await using var child = parent.CreateChild(options => options with { Sandbox = second }); await child.InitializeAsync();
        Assert.Same(explicitUserChoice, Assert.Single(child.Tools));
        Assert.Equal("parent", await Invoke(explicitUserChoice, child)); // Deliberate user-bound authority wins.
        Assert.Equal(0, first.EnvironmentCalls); Assert.Equal(0, second.EnvironmentCalls);
    }

    private static Agent Make(OwnerSandbox selected) => new(new() { Model = new NeverModel(), Sandbox = selected });
    private static ITool Tool(string label) => new FunctionTool(new("sandbox_owned", "Fake selected owner", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new(label));
    private static ValueTask<string> Invoke(ITool tool, Agent agent)
    {
        var input = JsonSerializer.SerializeToElement(new { });
        return tool.InvokeAsync(input, new(agent, new("owner-call", tool.Spec.Name, input), new Dictionary<string, object?>()));
    }
    private sealed class NeverModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fake child registration tests never invoke a model or process.");
    }
    private sealed class OwnerSandbox(string label) : Core.Sandbox
    {
        internal int GetToolsCalls, EnvironmentCalls;
        public override IReadOnlyList<ITool> GetTools() { GetToolsCalls++; return [Tool(label)]; }
        private Exception NoEnvironment() { EnvironmentCalls++; return new InvalidOperationException("No filesystem, command or interpreter allowed."); }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<IReadOnlyList<Strands.Agents.Files.FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
    }
}
