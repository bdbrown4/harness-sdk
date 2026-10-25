using System.Text.Json;
using Core = Strands.Agents.Sandbox;
using FileEntry = Strands.Agents.Files.FileEntry;

namespace Strands.Agents.Tests.Sandbox;

public sealed class SandboxFileEditorProfileAdditionalTests
{
    [Theory]
    [InlineData("[1.5,2.5]", true)]
    [InlineData("null", false)]
    [InlineData("[1]", false)]
    [InlineData("[1,2,3]", false)]
    [InlineData("[1,\"2\"]", false)]
    public async Task RegisteredTypeScriptInputsUseBothSchemaAndSelectedParser(string range, bool accepted)
    {
        var selected = EditorProfileAssertions.File("a\nb\nc");
        var tool = Core.SandboxFileEditor.CreateForAgent(new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript });
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        var result = await EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.View(range), caller.Token);
        Assert.Equal(accepted ? ToolResultStatus.Success : ToolResultStatus.Error, result.Status);
        if (accepted)
        {
            Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n   1.5  a\n   2.5  b\n", result.Content);
            EditorProfileAssertions.Calls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        }
        else Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal("a\nb\nc", selected.Files["/work/a.txt"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"view_range\":[1,2]")]
    public async Task OmittedAndIntegralTypeScriptRangesKeepOrdinaryViewOutput(string suffix)
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Core.SandboxFileEditor.CreateForAgent(new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript });
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        var result = await EditorProfileAssertions.Registered(agent, tool, "{\"command\":\"view\",\"path\":\"/work/a.txt\"" + suffix + "}", caller.Token);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n     1  a\n     2  b\n", result.Content);
        EditorProfileAssertions.Calls(selected, caller.Token, "list:/work", "read:/work/a.txt");
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task PrecanceledTypeScriptCallUsesOriginalTokenWithoutSelectedIo()
    {
        var selected = EditorProfileAssertions.File("a\nb");
        var tool = Core.SandboxFileEditor.CreateForAgent(new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript });
        await using var agent = EditorProfileAssertions.Agent(selected, tool);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditorProfileAssertions.Registered(agent, tool, EditorProfileAssertions.View("[1.5,2]"), caller.Token));
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
    }

    [Fact]
    public async Task TypeScriptInputRemainsDetachedWhileItsSelectedReadIsHeld()
    {
        var selected = new EditorProfileReadGate();
        var tool = Core.SandboxFileEditor.Create(selected, new() { InputProfile = Core.SandboxFileEditorInputProfile.TypeScript });
        await using var agent = EditorTestContext.Agent(selected);
        using var caller = new CancellationTokenSource();
        using var owner = JsonDocument.Parse(EditorProfileAssertions.View("[1.5,2.5]"));
        var pending = EditorProfileAssertions.Direct(tool, agent, owner.RootElement, caller.Token);
        Exception? primary = null;
        try
        {
            await EditorProfileAssertions.Started(selected.ReadStarted.Task, pending);
            owner.Dispose();
            Assert.False(pending.IsCompleted);
            selected.Release.TrySetResult(true);
            Assert.Equal("Here's the result of running `cat -n` on /work/a.txt:\n   1.5  a\n   2.5  b\n", await pending);
            Assert.True(selected.ReadCompleted);
            Assert.Equal(new[] { "list:/work", "read:/work/a.txt" }, selected.Calls.Select(call => call.Operation + ":" + call.Path));
            Assert.All(selected.Calls, call => Assert.Equal(caller.Token, call.Token));
            Assert.Empty(selected.Writes);
            Assert.Equal(0, selected.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await EditorProfileAssertions.ReleaseAndJoin(selected.Release, pending, primary); }
    }

    [Fact]
    public void UndefinedEditorInputProfileRejectsBeforeConsultingSelectedAuthority()
    {
        var selected = new EditorFakeSandbox { Forbidden = true };
        Assert.Throws<ArgumentOutOfRangeException>(() => Core.SandboxFileEditor.Create(selected,
            new() { InputProfile = (Core.SandboxFileEditorInputProfile)int.MaxValue }));
        Assert.Empty(selected.Calls);
        Assert.Empty(selected.Writes);
        Assert.Equal(0, selected.Disposals);
    }
}

internal static class EditorProfileAssertions
{
    internal static Agent Agent(Core.Sandbox selected, ITool tool) => new(new() { Model = new NoModel(), Sandbox = selected, Tools = [tool] });
    internal static EditorFakeSandbox File(string text)
    {
        var selected = new EditorFakeSandbox();
        selected.Files["/work/a.txt"] = text;
        return selected;
    }
    internal static string View(string range) => "{\"command\":\"view\",\"path\":\"/work/a.txt\",\"view_range\":" + range + "}";
    internal static string Insert(string line) => "{\"command\":\"insert\",\"path\":\"/work/a.txt\",\"insert_line\":" + line + ",\"new_str\":\"X\"}";
    internal static async Task<ToolResultBlock> Registered(Agent agent, ITool tool, string raw, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(raw);
        return await agent.InvokeToolAsync(new("editor-profile-registered", tool.Spec.Name, document.RootElement), cancellationToken: cancellationToken);
    }
    internal static Task<string> Direct(ITool tool, Agent agent, JsonElement input, CancellationToken cancellationToken)
        => tool.InvokeAsync(input, new(agent, new("editor-profile-direct", tool.Spec.Name, input), new Dictionary<string, object?>()), cancellationToken).AsTask();
    internal static void Calls(EditorFakeSandbox selected, CancellationToken token, params string[] expected)
    {
        Assert.Equal(expected, selected.Calls.Select(call => call.Operation + ":" + call.Path));
        Assert.All(selected.Calls, call => Assert.Equal(token, call.Token));
        Assert.Equal(0, selected.Disposals);
    }
    internal static async Task Started(Task expected, Task operation)
    {
        var winner = await Task.WhenAny(expected, operation, Task.Delay(TimeSpan.FromSeconds(5)));
        if (ReferenceEquals(winner, operation)) await operation;
        Assert.Same(expected, winner);
    }
    internal static async Task ReleaseAndJoin(TaskCompletionSource<bool> release, Task operation, Exception? primary)
    {
        release.TrySetResult(true);
        try { await operation; }
        catch (Exception cleanup)
        {
            if (primary is null) throw;
            if (!ReferenceEquals(primary, cleanup)) primary.Data["EditorProfileFixtureCleanup"] = cleanup;
        }
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Editor profile tests must not invoke a model, command or interpreter.");
    }
}

internal sealed class EditorProfileReadGate : Core.Sandbox, IDisposable
{
    internal readonly TaskCompletionSource<bool> ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<(string Operation, string Path, CancellationToken Token)> Calls = [];
    internal readonly List<byte[]> Writes = [];
    internal bool ReadCompleted;
    internal int Disposals;
    public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("/work", path);
        Calls.Add(("list", path, cancellationToken));
        return new(new[] { new FileEntry("a.txt", false) });
    }
    public override async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("/work/a.txt", path);
        Calls.Add(("read", path, cancellationToken));
        ReadStarted.TrySetResult(true);
        await Release.Task;
        ReadCompleted = true;
        return "a\nb\nc";
    }
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        Calls.Add(("write", path, cancellationToken));
        Writes.Add((byte[])data.Clone());
        return default;
    }
    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only selected text reads are allowed.");
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No delete belongs to these profile tests.");
    public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No model, process or interpreter belongs to these profile tests.");
    public void Dispose() => Disposals++;
}
