using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Files;
using Strands.Agents.Skills;

namespace Strands.Agents.Tests.Sandbox;

public sealed class AgentSandboxFileAuthorityTests
{
    [Fact]
    public async Task ExplicitSandboxSuppliesImplicitFileAuthorityWithoutBackendIOOrOwnershipTransfer()
    {
        var selected = new SelectedFiles();
        var model = new NeverCalledModel();
        var agent = new Agent(new() { Model = model, Sandbox = selected });
        try
        {
            Assert.Same(selected, agent.FileSystem);
            Assert.Same(selected, agent.Sandbox);
            Assert.Equal(0, selected.Calls);
            Assert.Equal(0, model.Calls);
        }
        finally { await agent.DisposeAsync(); }
        Assert.Equal(0, selected.DisposeCalls);
        Assert.Equal(0, selected.Calls);
    }

    [Fact]
    public async Task ExplicitFilesystemWinsAndTheOldUnconfiguredNullContractIsRetained()
    {
        var selected = new SelectedFiles();
        var explicitFiles = new SelectedFiles();
        await using var configured = new Agent(new() { Model = new NeverCalledModel(), Sandbox = selected, FileSystem = explicitFiles });
        Assert.Same(explicitFiles, configured.FileSystem);
        Assert.Same(selected, configured.Sandbox);
        await using var unconfigured = new Agent(new() { Model = new NeverCalledModel() });
        Assert.Null(unconfigured.FileSystem);
        _ = unconfigured.Sandbox; // Only constructs the lazy object; never execute it.
        Assert.Null(unconfigured.FileSystem);
        Assert.Equal(0, selected.Calls);
        Assert.Equal(0, explicitFiles.Calls);
    }

    [Fact]
    public async Task ChildRebindingTracksItsOwnImplicitAuthorityWhileExplicitFilesystemRemainsIndependent()
    {
        var parentFiles = new SelectedFiles();
        var childFiles = new SelectedFiles();
        await using var parent = new Agent(new() { Model = new NeverCalledModel(), Sandbox = parentFiles });
        await using var inherited = parent.CreateChild();
        await using var rebound = parent.CreateChild(options => options with { Sandbox = childFiles });
        await using var reset = parent.CreateChild(options => options with { Sandbox = null });
        Assert.Same(parentFiles, inherited.FileSystem);
        Assert.Same(childFiles, rebound.FileSystem);
        Assert.Null(reset.FileSystem);
        _ = reset.Sandbox;
        Assert.Null(reset.FileSystem);
        var explicitFiles = new SelectedFiles();
        await using var explicitParent = new Agent(new() { Model = new NeverCalledModel(), Sandbox = parentFiles, FileSystem = explicitFiles });
        await using var explicitChild = explicitParent.CreateChild(options => options with { Sandbox = childFiles });
        Assert.Same(explicitFiles, explicitChild.FileSystem);
        Assert.Equal(0, parentFiles.Calls);
        Assert.Equal(0, childFiles.Calls);
        Assert.Equal(0, explicitFiles.Calls);
    }

    [Fact]
    public async Task AgentSkillsDiscoversAndActivatesResourcesThroughTheSelectedSandbox()
    {
        var selected = new SelectedFiles();
        var plugin = new AgentSkills(new() { Skills = [SelectedFiles.Root], Strict = true });
        var model = new ScriptedSkillModel();
        await using var agent = new Agent(new() { Model = model, Sandbox = selected, Plugins = [plugin] });

        await agent.InitializeAsync();

        var available = Assert.Single(await plugin.GetAvailableSkillsAsync(agent));
        Assert.Equal("selected-review", available.Name);
        Assert.Equal(SelectedFiles.Root + "/selected-review", available.Path);
        Assert.Equal("done", (await agent.InvokeAsync("review")).ToString());
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages.Last().Content));
        Assert.Contains("Selected backend instructions.", result.Content, StringComparison.Ordinal);
        Assert.Contains("references/selected.md", result.Content, StringComparison.Ordinal);
        Assert.Equal(new[] { SelectedFiles.Root + "/selected-review/SKILL.md" }, selected.Reads);
        Assert.Contains(SelectedFiles.Root + "/selected-review/references", selected.Lists);
        Assert.Equal(0, selected.ExecutionCalls);
    }

    private sealed class NeverCalledModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("File authority identity tests must not invoke a model or any process.");
        }
    }

    private sealed class ScriptedSkillModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            ModelStreamEvent[] events = Requests.Count == 1
                ? [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("activate", "skills", JsonSerializer.SerializeToElement(new { }))),
                    new ModelContentBlockDeltaEvent(0, ToolInput: "{\"skill_name\":\"selected-review\"}"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)]
                : [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "done"),
                    new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new())];
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class SelectedFiles : Agents.Sandbox.Sandbox, IDisposable
    {
        // Intentionally not a host path: the selected fake accepts this opaque authority.
        // The unfixed host fallback cannot read a user directory through it.
        internal const string Root = "selected-only://skills";
        internal int DisposeCalls;
        internal int ExecutionCalls;
        internal int Calls => ExecutionCalls + Reads.Count + Lists.Count;
        internal List<string> Reads { get; } = [];
        internal List<string> Lists { get; } = [];
        public override IAsyncEnumerable<Agents.Sandbox.SandboxStreamEvent> ExecuteStreamingAsync(string command, Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
        {
            ExecutionCalls++;
            throw new InvalidOperationException("Filesystem-only fake must never execute a process.");
        }
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(path);
            return path == Root + "/selected-review/SKILL.md" ? new("---\nname: selected-review\ndescription: Review selected files\n---\nSelected backend instructions.") : throw new FileNotFoundException("Unknown selected fake file.", path);
        }
        public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Lists.Add(path);
            IReadOnlyList<FileEntry> result = path switch
            {
                Root => [new("selected-review", true)],
                Root + "/selected-review" => [new("SKILL.md", false), new("references", true)],
                Root + "/selected-review/references" => [new("selected.md", false)],
                _ => throw new DirectoryNotFoundException("Unknown selected fake directory."),
            };
            return new(result);
        }
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No byte operation expected.");
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writes expected.");
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No deletes expected.");
        public void Dispose() => DisposeCalls++;
    }
}
