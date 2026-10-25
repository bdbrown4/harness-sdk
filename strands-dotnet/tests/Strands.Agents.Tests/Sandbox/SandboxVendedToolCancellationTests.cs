using System.Text.Json;
using Core = Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxVendedToolCancellationTests
{
    [Theory]
    [InlineData("spec")]
    [InlineData("filter")]
    public async Task CallerCancellationInsideAnEarlierCallbackPreventsLaterMetadataCallbacks(string location)
    {
        using var caller = new CancellationTokenSource(); var callbacks = new List<string>();
        var laterFailure = new IOException("Later metadata callback must not run after caller cancellation.");
        var first = new MetadataTool("first", () => { callbacks.Add("first.spec"); if (location == "spec") caller.Cancel(); });
        var later = new MetadataTool("later", () => { callbacks.Add("later.spec"); throw laterFailure; });
        var selected = new CallbackSandbox([first, later]);
        await using var agent = new Agent(new()
        {
            Model = new NeverModel(),
            Sandbox = selected,
            ToolFilter = tool =>
            {
                callbacks.Add(ReferenceEquals(tool, first) ? "first.filter" : "later.filter");
                if (ReferenceEquals(tool, first) && location == "filter") caller.Cancel();
                return true;
            },
        });
        var caught = await Record.ExceptionAsync(async () => await agent.InitializeAsync(caller.Token));
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(caught).CancellationToken);
        Assert.DoesNotContain("later.spec", callbacks);
        Assert.DoesNotContain("later.filter", callbacks);
        if (location == "spec") Assert.DoesNotContain("first.filter", callbacks);
        Assert.Empty(agent.Tools); Assert.Equal(0, selected.EnvironmentCalls);
    }

    [Fact]
    public async Task IndependentMetadataFailureKeepsItsPrimaryWhenCallerWasNotCancelled()
    {
        var failure = new IOException("Independent metadata cause.");
        var selected = new CallbackSandbox([new MetadataTool("failure", () => throw failure)]);
        await using var agent = new Agent(new() { Model = new NeverModel(), Sandbox = selected });
        Assert.Same(failure, await Record.ExceptionAsync(async () => await agent.InitializeAsync()));
        Assert.Empty(agent.Tools); Assert.Equal(0, selected.EnvironmentCalls);
    }

    private sealed class MetadataTool(string name, Action read) : ITool
    {
        private readonly ToolSpec _spec = new(name, "Fake metadata callback", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ToolSpec Spec { get { read(); return _spec; } }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No body may run during registration.");
    }
    private sealed class NeverModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model/process in callback cancellation tests.");
    }
    private sealed class CallbackSandbox(IReadOnlyList<ITool> tools) : Core.Sandbox
    {
        internal int EnvironmentCalls;
        public override IReadOnlyList<ITool> GetTools() => tools;
        private Exception NoEnvironment() { EnvironmentCalls++; return new InvalidOperationException("No filesystem, command or interpreter allowed."); }
        public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
        public override ValueTask<IReadOnlyList<Strands.Agents.Files.FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw NoEnvironment();
    }
}
